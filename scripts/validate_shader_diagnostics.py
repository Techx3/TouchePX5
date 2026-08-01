#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

"""Validate Touché PX5 shader diagnostic bundles and their SPIR-V modules."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


def find_spirv_val(explicit: str | None) -> str | None:
    if explicit:
        return explicit
    executable = "spirv-val.exe" if os.name == "nt" else "spirv-val"
    from_path = shutil.which(executable)
    if from_path:
        return from_path
    sdk = os.environ.get("VULKAN_SDK")
    if sdk:
        candidate = Path(sdk) / "Bin" / executable
        if candidate.is_file():
            return str(candidate)
    return None


def validate_report(path: Path) -> list[str]:
    errors: list[str] = []
    try:
        report = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exception:
        return [f"{path.name}: invalid JSON: {exception}"]

    observed = report.get("SpirvImageOperations", {})
    for binding in report.get("ImageBindings", []):
        expected = binding.get("ExpectedSpirvOpcode", "")
        if expected.startswith("Op") or expected in {
            "query-or-unknown",
            "OpImageSample*",
            "OpImageGather*",
            "OpAtomic*",
        }:
            continue
        if observed.get(expected, 0) == 0:
            errors.append(
                f"{path.name}: guest {binding.get('GuestOpcode')} at "
                f"0x{binding.get('Pc', 0):X} expects {expected}, not observed"
            )

        descriptor = binding.get("Descriptor")
        if descriptor is None:
            errors.append(
                f"{path.name}: descriptor at 0x{binding.get('Pc', 0):X} "
                f"did not decode: {binding.get('DescriptorError')}"
            )
        elif descriptor.get("Width", 0) < 1 or descriptor.get("Height", 0) < 1:
            errors.append(
                f"{path.name}: descriptor at 0x{binding.get('Pc', 0):X} "
                "has an empty extent"
            )
    return errors


def validate_spirv(tool: str, path: Path, target_env: str) -> list[str]:
    result = subprocess.run(
        [tool, "--target-env", target_env, str(path)],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode == 0:
        return []
    detail = (result.stderr or result.stdout).strip()
    return [f"{path.name}: spirv-val failed: {detail}"]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--spirv-val")
    parser.add_argument("--target-env", default="vulkan1.2")
    parser.add_argument(
        "--allow-missing-validator",
        action="store_true",
        help="validate JSON only when spirv-val is unavailable",
    )
    args = parser.parse_args()

    reports = sorted(args.directory.glob("*.json"))
    if not reports:
        print(f"No diagnostic JSON files found in {args.directory}", file=sys.stderr)
        return 2

    validator = find_spirv_val(args.spirv_val)
    if validator is None and not args.allow_missing_validator:
        print("spirv-val was not found; set VULKAN_SDK or use --spirv-val", file=sys.stderr)
        return 2

    errors: list[str] = []
    validated_spirv = 0
    for report in reports:
        errors.extend(validate_report(report))
        module = report.with_suffix(".spv")
        if not module.is_file():
            errors.append(f"{report.name}: matching {module.name} is missing")
        elif validator is not None:
            errors.extend(validate_spirv(validator, module, args.target_env))
            validated_spirv += 1

    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1

    print(
        f"Validated {len(reports)} diagnostic reports and "
        f"{validated_spirv} SPIR-V modules."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
