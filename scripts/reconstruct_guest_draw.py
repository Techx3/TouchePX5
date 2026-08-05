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


def reconstruct_manifest(manifest_path: Path, output: Path, scale: int) -> dict[str, object]:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    draw_output = output / manifest_path.parent.name
    textures: list[dict[str, object]] = []
    indices: list[tuple[dict[str, object], bytes]] = []
    palettes: list[tuple[dict[str, object], bytes]] = []

    for texture in manifest.get("Textures", []):
        file_name = texture.get("File")
        if not file_name:
            textures.append({**texture, "reconstructed": False, "reason": "empty payload"})
            continue
        if texture.get("SourceKind") != "linear":
            textures.append({**texture, "reconstructed": False, "reason": "payload is tiled"})
            continue

        source = (manifest_path.parent / str(file_name)).read_bytes()
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
                palettes.append((texture, visible))
        else:
            textures.append({**texture, "reconstructed": False, "reason": "unsupported guest format"})
            continue

        textures.append({**texture, **byte_stats(visible), "reconstructed": True, "png": name})

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
            pairs.append(
                {
                    "indexTexture": index_texture["Index"],
                    "paletteTexture": palette_texture["Index"],
                    "paletteEntries": entries,
                    "maxIndex": max(index_bytes, default=0),
                    "outOfRangeTexels": out_of_range,
                    "png": name,
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


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Convert pre-Vulkan Touché PX5 guest draw captures to PNG"
    )
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--scale", type=int, default=1)
    args = parser.parse_args()

    manifests = sorted(args.input.glob("draw-*/manifest.json"))
    if manifests:
        reports = [reconstruct_manifest(path, args.output, args.scale) for path in manifests]
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
