# Copyright (C) 2026 Touché PX5 Project
# SPDX-License-Identifier: GPL-2.0-or-later

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest


SCRIPT_PATH = (
    Path(__file__).resolve().parents[2] / "scripts" / "aerolib_catalog.py"
)
SPEC = importlib.util.spec_from_file_location("aerolib_catalog", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
AEROLIB = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = AEROLIB
SPEC.loader.exec_module(AEROLIB)


class RuntimeAuditTests(unittest.TestCase):
    def test_parses_unresolved_and_failed_imports(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "game.log"
            log.write_text(
                "[LOADER][WARN] Import#10 unresolved: nid=4fU5yvOkVG4 ret=0x1\n"
                "[LOADER][WARN] Import#11 unresolved: nid=4fU5yvOkVG4 ret=0x2\n"
                "[LOADER][WARN] Import#12 result: -1 (VAzswvTOCzI) rdi=0\n",
                encoding="utf-8",
            )

            unresolved, failed = AEROLIB.parse_import_diagnostics(log)

        self.assertEqual(2, unresolved["4fU5yvOkVG4"])
        self.assertEqual(1, failed[("VAzswvTOCzI", "-1")])

    def test_scans_export_name_and_explicit_nid(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source_root = Path(directory)
            source = source_root / "Exports.cs"
            source.write_text(
                '[SysAbiExport(ExportName = "unlink", LibraryName = "libkernel")]\n'
                "static int Unlink() => 0;\n"
                '[SysAbiExport(Nid = "4fU5yvOkVG4")]\n'
                "static int UnwindInfo() => 0;\n",
                encoding="utf-8",
            )

            exports = AEROLIB.scan_implemented_exports(source_root)

        self.assertIn(AEROLIB.compute_nid("unlink"), exports)
        self.assertIn("4fU5yvOkVG4", exports)

    def test_report_resolves_catalog_name_and_missing_status(self) -> None:
        unresolved = AEROLIB.Counter({"4fU5yvOkVG4": 3})
        report = AEROLIB.render_audit_report(
            Path("game.log"),
            {"4fU5yvOkVG4": "sceSysmoduleGetModuleInfoForUnwind"},
            {},
            unresolved,
            AEROLIB.Counter(),
            100,
        )

        self.assertIn("sceSysmoduleGetModuleInfoForUnwind", report)
        self.assertIn("| 3 | `4fU5yvOkVG4`", report)
        self.assertIn("Missing", report)

    def test_global_coverage_classifies_all_statuses(self) -> None:
        implementations = {
            "AAAAAAAAAAA": [
                AEROLIB.ExportImplementation(
                    "AAAAAAAAAAA", "implemented", "libKernel", "Exports.cs", 10
                )
            ],
            "BBBBBBBBBBB": [
                AEROLIB.ExportImplementation(
                    "BBBBBBBBBBB", "wrong_name", "libSceTest", "Test.cs", 20
                )
            ],
            "DDDDDDDDDDD": [
                AEROLIB.ExportImplementation(
                    "DDDDDDDDDDD", "local_only", "libKernel", "Local.cs", 30
                )
            ],
        }
        rows = AEROLIB.build_coverage_rows(
            {
                "AAAAAAAAAAA": "implemented",
                "BBBBBBBBBBB": "expected_name",
                "CCCCCCCCCCC": "missing",
            },
            implementations,
        )

        statuses = {row.nid: row.status for row in rows}
        self.assertEqual("implemented", statuses["AAAAAAAAAAA"])
        self.assertEqual("name-mismatch", statuses["BBBBBBBBBBB"])
        self.assertEqual("missing", statuses["CCCCCCCCCCC"])
        self.assertEqual("local-only", statuses["DDDDDDDDDDD"])

    def test_coverage_tsv_contains_traceable_source(self) -> None:
        rows = [
            AEROLIB.CoverageRow(
                "AAAAAAAAAAA",
                "implemented",
                "sceTest",
                "sceTest",
                "libSceTest",
                "TestExports.cs:42",
            )
        ]

        report = AEROLIB.render_coverage_tsv(rows)

        self.assertIn("NID\tStatus\tCatalogName\tLocalName\tLibrary\tSource", report)
        self.assertIn("TestExports.cs:42", report)


if __name__ == "__main__":
    unittest.main()
