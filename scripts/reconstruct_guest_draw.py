#!/usr/bin/env python3
# Copyright (C) 2026 Touché PX5 Project
# SPDX-License-Identifier: GPL-2.0-or-later

"""Reconstruct guest textures captured before Vulkan into ordinary PNG files."""

from __future__ import annotations

import argparse
import binascii
import json
from pathlib import Path
import re
import struct
import zlib


DUMP_NAME = re.compile(
    r"(?P<sequence>\d+)-0x(?P<address>[0-9A-Fa-f]+)-"
    r"(?P<width>\d+)x(?P<height>\d+)-p(?P<pitch>\d+)-"
    r"f(?P<format>\d+)-t(?P<tile>\d+)(?:\.linear)?\.bin$"
)


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    checksum = binascii.crc32(kind)
    checksum = binascii.crc32(payload, checksum) & 0xFFFFFFFF
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", checksum)


def write_rgba_png(path: Path, width: int, height: int, rgba: bytes, scale: int) -> None:
    if len(rgba) != width * height * 4:
        raise ValueError(f"{path.name}: expected {width * height * 4} RGBA bytes, got {len(rgba)}")

    scale = max(scale, 1)
    output_width = width * scale
    output_height = height * scale
    scanlines = bytearray()
    for y in range(height):
        source_row = rgba[y * width * 4 : (y + 1) * width * 4]
        expanded = b"".join(source_row[x : x + 4] * scale for x in range(0, len(source_row), 4))
        for _ in range(scale):
            scanlines.append(0)
            scanlines.extend(expanded)

    header = struct.pack(">IIBBBBB", output_width, output_height, 8, 6, 0, 0, 0)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", header)
        + png_chunk(b"IDAT", zlib.compress(bytes(scanlines), 9))
        + png_chunk(b"IEND", b"")
    )


def crop_texture(data: bytes, width: int, height: int, pitch: int, bytes_per_texel: int) -> bytes:
    pitch = pitch or width
    row_bytes = pitch * bytes_per_texel
    visible_bytes = width * bytes_per_texel
    required = row_bytes * height
    if len(data) < required:
        raise ValueError(f"requires {required} bytes, got {len(data)}")
    return b"".join(data[y * row_bytes : y * row_bytes + visible_bytes] for y in range(height))


def indices_to_rgba(indices: bytes) -> bytes:
    return b"".join(bytes((value, value, value, 255)) for value in indices)


def palette_image_rgba(palette: bytes) -> tuple[int, int, bytes]:
    entries = len(palette) // 4
    return entries, 1, palette[: entries * 4]


def apply_dst_select(rgba: bytes, dst_select: int) -> bytes:
    """Apply the guest image descriptor component selection to RGBA texels."""
    output = bytearray(len(rgba))
    for offset in range(0, len(rgba), 4):
        source = rgba[offset : offset + 4]
        for destination in range(4):
            selector = (dst_select >> (destination * 3)) & 7
            if selector == 0:
                value = 0
            elif selector == 1:
                value = 255
            elif 4 <= selector <= 7:
                value = source[selector - 4]
            else:
                value = source[destination]
            output[offset + destination] = value
    return bytes(output)


def apply_palette(indices: bytes, palette: bytes) -> tuple[bytes, int, int]:
    entries = len(palette) // 4
    output = bytearray()
    out_of_range = 0
    for index in indices:
        if index < entries:
            output.extend(palette[index * 4 : index * 4 + 4])
        else:
            output.extend((255, 0, 255, 255))
            out_of_range += 1
    return bytes(output), entries, out_of_range


def byte_stats(data: bytes) -> dict[str, int]:
    return {
        "bytes": len(data),
        "nonZeroBytes": sum(value != 0 for value in data),
        "uniqueByteValues": len(set(data)),
    }


def find_external_texture_payload(
    texture: dict[str, object],
    texture_source: Path | None,
) -> Path | None:
    if texture_source is None:
        return None

    address = int(str(texture["Address"]), 0)
    pattern = (
        f"*-texture-0x{address:016X}-"
        f"{int(texture['Width'])}x{int(texture['Height'])}-"
        f"row{int(texture.get('Pitch') or texture['Width'])}-"
        f"fmt{int(texture['Format'])}-*.rgba"
    )
    candidates = sorted(texture_source.glob(pattern))
    return candidates[-1] if candidates else None


def reconstruct_manifest(
    manifest_path: Path,
    output: Path,
    scale: int,
    texture_source: Path | None = None,
) -> dict[str, object]:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    draw_output = output / manifest_path.parent.name
    textures: list[dict[str, object]] = []
    indices: list[tuple[dict[str, object], bytes]] = []
    palettes: list[tuple[dict[str, object], bytes]] = []

    for texture in manifest.get("Textures", []):
        file_name = texture.get("File")
        external_file = None
        if file_name:
            source_path = manifest_path.parent / str(file_name)
        else:
            external_file = find_external_texture_payload(texture, texture_source)
            source_path = external_file
        if source_path is None:
            textures.append({**texture, "reconstructed": False, "reason": "empty payload"})
            continue
        if external_file is None and texture.get("SourceKind") != "linear":
            textures.append({**texture, "reconstructed": False, "reason": "payload is tiled"})
            continue

        source = source_path.read_bytes()
        width = int(texture["Width"])
        height = int(texture["Height"])
        pitch = int(texture.get("Pitch") or width)
        guest_format = int(texture["Format"])
        if guest_format == 1:
            visible = crop_texture(source, width, height, pitch, 1)
            image = indices_to_rgba(visible)
            name = f"texture-{int(texture['Index']):02d}-indices.png"
            write_rgba_png(draw_output / name, width, height, image, scale)
            indices.append((texture, visible))
        elif guest_format == 10:
            visible = crop_texture(source, width, height, pitch, 4)
            name = f"texture-{int(texture['Index']):02d}-rgba.png"
            write_rgba_png(draw_output / name, width, height, visible, scale)
            if height == 1:
                dst_select = int(texture.get("DstSelect") or 0)
                selected = apply_dst_select(visible, dst_select)
                selected_name = (
                    f"texture-{int(texture['Index']):02d}-"
                    f"dstsel-{dst_select:03X}.png"
                )
                write_rgba_png(draw_output / selected_name, width, height, selected, scale)
                palettes.append((texture, visible))
        else:
            textures.append({**texture, "reconstructed": False, "reason": "unsupported guest format"})
            continue

        textures.append(
            {
                **texture,
                **byte_stats(visible),
                "reconstructed": True,
                "png": name,
                "externalFile": str(external_file) if external_file else None,
            }
        )

    pairs: list[dict[str, object]] = []
    for index_texture, index_bytes in indices:
        for palette_texture, palette_bytes in palettes:
            rgba, entries, out_of_range = apply_palette(index_bytes, palette_bytes)
            name = (
                f"pair-index-{int(index_texture['Index']):02d}-"
                f"palette-{int(palette_texture['Index']):02d}.png"
            )
            write_rgba_png(
                draw_output / name,
                int(index_texture["Width"]),
                int(index_texture["Height"]),
                rgba,
                scale,
            )
            dst_select = int(palette_texture.get("DstSelect") or 0)
            selected_palette = apply_dst_select(palette_bytes, dst_select)
            selected_rgba, _, selected_out_of_range = apply_palette(
                index_bytes,
                selected_palette,
            )
            selected_name = (
                f"pair-index-{int(index_texture['Index']):02d}-"
                f"palette-{int(palette_texture['Index']):02d}-"
                f"dstsel-{dst_select:03X}.png"
            )
            write_rgba_png(
                draw_output / selected_name,
                int(index_texture["Width"]),
                int(index_texture["Height"]),
                selected_rgba,
                scale,
            )
            pairs.append(
                {
                    "indexTexture": index_texture["Index"],
                    "paletteTexture": palette_texture["Index"],
                    "paletteEntries": entries,
                    "maxIndex": max(index_bytes, default=0),
                    "outOfRangeTexels": out_of_range,
                    "png": name,
                    "dstSelect": f"0x{dst_select:03X}",
                    "dstSelectOutOfRangeTexels": selected_out_of_range,
                    "dstSelectPng": selected_name,
                }
            )

    return {
        "manifest": str(manifest_path),
        "shaderAddress": manifest.get("ShaderAddress"),
        "target": manifest.get("Target"),
        "textures": textures,
        "pairs": pairs,
    }


def reconstruct_legacy_dump(path: Path, output: Path, scale: int) -> dict[str, object] | None:
    match = DUMP_NAME.match(path.name)
    if match is None:
        return None
    fields = {key: int(value, 16 if key == "address" else 10) for key, value in match.groupdict().items()}
    data = path.read_bytes()
    if fields["format"] == 1:
        visible = crop_texture(data, fields["width"], fields["height"], fields["pitch"], 1)
        rgba = indices_to_rgba(visible)
        suffix = "indices"
    elif fields["format"] == 10:
        visible = crop_texture(data, fields["width"], fields["height"], fields["pitch"], 4)
        rgba = visible
        suffix = "rgba"
    else:
        return {"file": str(path), "reconstructed": False, "reason": "unsupported guest format"}
    name = f"{path.stem}-{suffix}.png"
    write_rgba_png(output / name, fields["width"], fields["height"], rgba, scale)
    return {"file": str(path), **fields, **byte_stats(visible), "reconstructed": True, "png": name}


def reconstruct_legacy_pairs(
    input_path: Path,
    output: Path,
    scale: int,
    palette_address: int,
    index_addresses: list[int],
) -> list[dict[str, object]]:
    captures: dict[int, list[tuple[Path, dict[str, int]]]] = {}
    for path in input_path.glob("*.bin"):
        match = DUMP_NAME.match(path.name)
        if match is None:
            continue
        fields = {
            key: int(value, 16 if key == "address" else 10)
            for key, value in match.groupdict().items()
        }
        captures.setdefault(fields["address"], []).append((path, fields))

    palette_captures = captures.get(palette_address, [])
    if not palette_captures:
        raise ValueError(f"palette 0x{palette_address:X} was not captured")
    palette_path, palette_fields = max(
        palette_captures,
        key=lambda capture: capture[1]["sequence"],
    )
    if palette_fields["format"] != 10:
        raise ValueError(f"palette 0x{palette_address:X} is not format 10")
    palette = crop_texture(
        palette_path.read_bytes(),
        palette_fields["width"],
        palette_fields["height"],
        palette_fields["pitch"],
        4,
    )

    reports: list[dict[str, object]] = []
    for address in index_addresses:
        candidates = captures.get(address, [])
        if not candidates:
            reports.append(
                {
                    "indexAddress": f"0x{address:X}",
                    "reconstructed": False,
                    "reason": "index texture was not captured",
                }
            )
            continue
        index_path, index_fields = max(
            candidates,
            key=lambda capture: capture[1]["sequence"],
        )
        if index_fields["format"] != 1:
            reports.append(
                {
                    "indexAddress": f"0x{address:X}",
                    "reconstructed": False,
                    "reason": "index texture is not format 1",
                }
            )
            continue
        indices = crop_texture(
            index_path.read_bytes(),
            index_fields["width"],
            index_fields["height"],
            index_fields["pitch"],
            1,
        )
        rgba, entries, out_of_range = apply_palette(indices, palette)
        name = f"pair-0x{address:016X}-palette-0x{palette_address:016X}.png"
        write_rgba_png(
            output / name,
            index_fields["width"],
            index_fields["height"],
            rgba,
            scale,
        )
        selected_palette = apply_dst_select(palette, 0xF2E)
        selected_rgba, _, selected_out_of_range = apply_palette(
            indices,
            selected_palette,
        )
        selected_name = (
            f"pair-0x{address:016X}-palette-0x{palette_address:016X}-"
            "dstsel-F2E.png"
        )
        write_rgba_png(
            output / selected_name,
            index_fields["width"],
            index_fields["height"],
            selected_rgba,
            scale,
        )
        reports.append(
            {
                "indexAddress": f"0x{address:X}",
                "indexFile": str(index_path),
                "paletteAddress": f"0x{palette_address:X}",
                "paletteFile": str(palette_path),
                "paletteEntries": entries,
                "maxIndex": max(indices, default=0),
                "outOfRangeTexels": out_of_range,
                "dstSelectOutOfRangeTexels": selected_out_of_range,
                "reconstructed": True,
                "png": name,
                "dstSelectPng": selected_name,
            }
        )
    return reports


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Convert pre-Vulkan Touché PX5 guest draw captures to PNG"
    )
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--scale", type=int, default=1)
    parser.add_argument(
        "--texture-source",
        type=Path,
        help="Optional directory containing correlated linear guest texture dumps",
    )
    parser.add_argument("--palette-address", type=lambda value: int(value, 0))
    parser.add_argument(
        "--index-address",
        action="append",
        type=lambda value: int(value, 0),
        default=[],
    )
    args = parser.parse_args()

    manifests = sorted(args.input.glob("draw-*/manifest.json"))
    if args.palette_address is not None and args.index_address:
        reports = reconstruct_legacy_pairs(
            args.input,
            args.output,
            args.scale,
            args.palette_address,
            args.index_address,
        )
    elif manifests:
        reports = [
            reconstruct_manifest(path, args.output, args.scale, args.texture_source)
            for path in manifests
        ]
    else:
        reports = []
        for path in sorted(args.input.glob("*.bin")):
            report = reconstruct_legacy_dump(path, args.output, args.scale)
            if report is not None:
                reports.append(report)

    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "summary.json").write_text(
        json.dumps(reports, indent=2, ensure_ascii=False),
        encoding="utf-8",
    )
    reconstructed = sum(bool(report.get("reconstructed", True)) for report in reports)
    print(f"Processed {len(reports)} captures; reconstructed {reconstructed}; output: {args.output}")
    return 0 if reports else 2


if __name__ == "__main__":
    raise SystemExit(main())
