#!/usr/bin/env python3

# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

from __future__ import annotations

import argparse
import base64
from collections import Counter
from dataclasses import dataclass
import hashlib
import re
import sys
from pathlib import Path


NID_SUFFIX = bytes.fromhex("518d64a635ded8c1e6b039b1c3e55230")
NID_PATTERN = re.compile(r"^[A-Za-z0-9+-]{11}$")
DEFAULT_NAMES_FILE = Path(__file__).resolve().with_name("ps5_names.txt")
DEFAULT_EXPORT_FILE = Path(__file__).resolve().parents[1] / "artifacts" / "aerolib.txt"
DEFAULT_COVERAGE_FILE = Path(__file__).resolve().parents[1] / "artifacts" / "abi-coverage.tsv"
DEFAULT_SOURCE_ROOT = Path(__file__).resolve().parents[1] / "src"
UNRESOLVED_IMPORT_PATTERN = re.compile(
    r"Import#\d+\s+unresolved:\s+nid=([A-Za-z0-9+\-]{11})"
)
FAILED_IMPORT_PATTERN = re.compile(
    r"Import#\d+\s+result:\s+(.+?)\s+\(([A-Za-z0-9+\-]{11})\)"
)
EXPORT_ATTRIBUTE_PATTERN = re.compile(r"\[SysAbiExport\((.*?)\)\]", re.DOTALL)
ATTRIBUTE_STRING_PATTERN = re.compile(
    r'\b(Nid|ExportName|LibraryName)\s*=\s*"([^"]*)"'
)


@dataclass(frozen=True)
class ExportImplementation:
    nid: str
    export_name: str
    library_name: str
    source: str
    line: int

    def label(self) -> str:
        name = self.export_name or "(explicit NID)"
        return f"{name} [{self.library_name}] — {self.source}:{self.line}"


@dataclass(frozen=True)
class CoverageRow:
    nid: str
    status: str
    catalog_name: str
    local_names: str
    libraries: str
    sources: str


def compute_nid(export_name: str) -> str:
    digest = hashlib.sha1(export_name.encode("utf-8") + NID_SUFFIX).digest()
    encoded = base64.b64encode(digest[:8][::-1]).decode("ascii")
    return encoded.rstrip("=").replace("/", "-")


def read_names(path: Path) -> list[str]:
    try:
        return [
            line.strip()
            for line in path.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
    except OSError as error:
        raise SystemExit(f"Unable to read catalog '{path}': {error}") from error


def write_pair(nid: str, export_name: str) -> None:
    print(f"{nid}\t{export_name}")


def lookup(args: argparse.Namespace) -> int:
    value = args.value.strip()
    if NID_PATTERN.fullmatch(value):
        for export_name in read_names(args.names):
            if compute_nid(export_name) == value:
                write_pair(value, export_name)
                return 0

        print(f"NID not found in catalog: {value}", file=sys.stderr)
        return 1

    names = set(read_names(args.names))
    write_pair(compute_nid(value), value)
    if value not in names:
        print("Warning: export name is not present in the catalog.", file=sys.stderr)
    return 0


def search(args: argparse.Namespace) -> int:
    names = read_names(args.names)
    if args.regex:
        try:
            pattern = re.compile(args.query, 0 if args.case_sensitive else re.IGNORECASE)
        except re.error as error:
            print(f"Invalid regular expression: {error}", file=sys.stderr)
            return 2

        matches = (name for name in names if pattern.search(name))
    elif args.case_sensitive:
        matches = (name for name in names if args.query in name)
    else:
        query = args.query.casefold()
        matches = (name for name in names if query in name.casefold())

    count = 0
    for export_name in matches:
        write_pair(compute_nid(export_name), export_name)
        count += 1
        if args.limit and count >= args.limit:
            break

    if count == 0:
        print(f"No catalog names matched: {args.query}", file=sys.stderr)
        return 1
    return 0


def export_catalog(args: argparse.Namespace) -> int:
    pairs = [(compute_nid(name), name) for name in read_names(args.names)]
    if args.sort == "nid":
        pairs.sort(key=lambda pair: (pair[0], pair[1]))
    elif args.sort == "name":
        pairs.sort(key=lambda pair: pair[1])

    args.output.parent.mkdir(parents=True, exist_ok=True)
    try:
        with args.output.open("w", encoding="utf-8", newline="\n") as output:
            output.write("# NID\tExportName\n")
            for nid, export_name in pairs:
                output.write(f"{nid}\t{export_name}\n")
    except OSError as error:
        print(f"Unable to write catalog '{args.output}': {error}", file=sys.stderr)
        return 1

    print(f"Wrote {len(pairs)} entries to {args.output}")
    return 0


def build_name_catalog(path: Path) -> dict[str, str]:
    return {compute_nid(name): name for name in read_names(path)}


def scan_export_implementations(
    source_root: Path,
) -> dict[str, list[ExportImplementation]]:
    exports: dict[str, list[ExportImplementation]] = {}
    try:
        source_files = source_root.rglob("*.cs")
        for source_file in source_files:
            text = source_file.read_text(encoding="utf-8")
            for match in EXPORT_ATTRIBUTE_PATTERN.finditer(text):
                fields = dict(ATTRIBUTE_STRING_PATTERN.findall(match.group(1)))
                export_name = fields.get("ExportName", "")
                nid = fields.get("Nid", "")
                if not nid and export_name:
                    nid = compute_nid(export_name)
                if not NID_PATTERN.fullmatch(nid):
                    continue

                line = text.count("\n", 0, match.start()) + 1
                library = fields.get("LibraryName", "") or "libKernel"
                try:
                    display_path = source_file.relative_to(source_root)
                except ValueError:
                    display_path = source_file
                implementation = ExportImplementation(
                    nid=nid,
                    export_name=export_name,
                    library_name=library,
                    source=str(display_path),
                    line=line,
                )
                exports.setdefault(nid, []).append(implementation)
    except OSError as error:
        raise SystemExit(
            f"Unable to scan source tree '{source_root}': {error}"
        ) from error
    return exports


def scan_implemented_exports(source_root: Path) -> dict[str, list[str]]:
    return {
        nid: [implementation.label() for implementation in implementations]
        for nid, implementations in scan_export_implementations(source_root).items()
    }


def join_unique(values: list[str]) -> str:
    return "; ".join(dict.fromkeys(value for value in values if value))


def build_coverage_rows(
    catalog: dict[str, str],
    implementations: dict[str, list[ExportImplementation]],
) -> list[CoverageRow]:
    rows: list[CoverageRow] = []
    for nid in set(catalog) | set(implementations):
        catalog_name = catalog.get(nid, "")
        local = implementations.get(nid, [])
        local_names = [item.export_name for item in local]
        if not catalog_name:
            status = "local-only"
        elif not local:
            status = "missing"
        elif any(name and name != catalog_name for name in local_names):
            status = "name-mismatch"
        else:
            status = "implemented"

        rows.append(
            CoverageRow(
                nid=nid,
                status=status,
                catalog_name=catalog_name,
                local_names=join_unique(local_names),
                libraries=join_unique([item.library_name for item in local]),
                sources=join_unique(
                    [f"{item.source}:{item.line}" for item in local]
                ),
            )
        )
    return rows


def coverage_summary(rows: list[CoverageRow]) -> Counter[str]:
    return Counter(row.status for row in rows)


def sanitize_tsv(value: str) -> str:
    return value.replace("\t", " ").replace("\r", " ").replace("\n", " ")


def render_coverage_tsv(rows: list[CoverageRow]) -> str:
    lines = ["NID\tStatus\tCatalogName\tLocalName\tLibrary\tSource"]
    for row in rows:
        values = (
            row.nid,
            row.status,
            row.catalog_name,
            row.local_names,
            row.libraries,
            row.sources,
        )
        lines.append("\t".join(sanitize_tsv(value) for value in values))
    return "\n".join(lines) + "\n"


def render_coverage_markdown(
    rows: list[CoverageRow],
    summary_rows: list[CoverageRow] | None = None,
) -> str:
    counts = coverage_summary(summary_rows if summary_rows is not None else rows)
    implemented = counts["implemented"] + counts["name-mismatch"]
    catalog_total = implemented + counts["missing"]
    percentage = (implemented / catalog_total * 100) if catalog_total else 0.0
    lines = [
        "# PS5 ABI Coverage",
        "",
        f"- Catalog NIDs: **{catalog_total}**",
        f"- Implemented catalog NIDs: **{implemented}** ({percentage:.2f}%)",
        f"- Missing: **{counts['missing']}**",
        f"- Name mismatches: **{counts['name-mismatch']}**",
        f"- Local-only NIDs: **{counts['local-only']}**",
        "",
        "| NID | Status | Catalog name | Local name | Library | Source |",
        "| --- | --- | --- | --- | --- | --- |",
    ]
    for row in rows:
        values = (
            row.nid,
            row.status,
            row.catalog_name,
            row.local_names,
            row.libraries,
            row.sources,
        )
        lines.append("| " + " | ".join(markdown_cell(value) for value in values) + " |")
    lines.append("")
    return "\n".join(lines)


def coverage(args: argparse.Namespace) -> int:
    catalog = build_name_catalog(args.names)
    implementations = scan_export_implementations(args.source_root)
    all_rows = build_coverage_rows(catalog, implementations)
    rows = all_rows
    if args.status != "all":
        rows = [row for row in rows if row.status == args.status]

    if args.sort == "name":
        rows.sort(key=lambda row: (row.catalog_name or row.local_names, row.nid))
    elif args.sort == "status":
        rows.sort(key=lambda row: (row.status, row.catalog_name or row.local_names, row.nid))
    else:
        rows.sort(key=lambda row: row.nid)

    report = (
        render_coverage_markdown(rows, all_rows)
        if args.format == "markdown"
        else render_coverage_tsv(rows)
    )
    try:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(report, encoding="utf-8", newline="\n")
    except OSError as error:
        print(f"Unable to write coverage report '{args.output}': {error}", file=sys.stderr)
        return 1

    counts = coverage_summary(all_rows)
    catalog_implemented = counts["implemented"] + counts["name-mismatch"]
    catalog_total = catalog_implemented + counts["missing"]
    percentage = (catalog_implemented / catalog_total * 100) if catalog_total else 0.0
    print(
        f"Wrote {len(rows)} rows to {args.output}; "
        f"catalog coverage {catalog_implemented}/{catalog_total} ({percentage:.2f}%), "
        f"mismatches {counts['name-mismatch']}, local-only {counts['local-only']}"
    )
    return 0


def parse_import_diagnostics(
    path: Path,
) -> tuple[Counter[str], Counter[tuple[str, str]]]:
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError as error:
        raise SystemExit(f"Unable to read log '{path}': {error}") from error

    unresolved = Counter(UNRESOLVED_IMPORT_PATTERN.findall(text))
    failed = Counter(
        (nid, result.strip())
        for result, nid in FAILED_IMPORT_PATTERN.findall(text)
    )
    return unresolved, failed


def markdown_cell(value: str) -> str:
    return value.replace("|", "\\|").replace("\n", " ")


def render_audit_report(
    log_path: Path,
    catalog: dict[str, str],
    implemented: dict[str, list[str]],
    unresolved: Counter[str],
    failed: Counter[tuple[str, str]],
    limit: int,
) -> str:
    unresolved_rows = unresolved.most_common(limit or None)
    failed_rows = failed.most_common(limit or None)
    lines = [
        "# PS5 ABI Runtime Audit",
        "",
        f"Log: `{log_path}`",
        "",
        f"- Unique unresolved NIDs: **{len(unresolved)}**",
        f"- Unresolved calls: **{sum(unresolved.values())}**",
        f"- Unique non-success results: **{len(failed)}**",
        f"- Non-success calls: **{sum(failed.values())}**",
        "",
        "## Unresolved imports",
        "",
        "| Calls | NID | Catalog name | Local implementation |",
        "| ---: | --- | --- | --- |",
    ]
    if unresolved_rows:
        for nid, count in unresolved_rows:
            locations = "<br>".join(implemented.get(nid, [])) or "Missing"
            lines.append(
                f"| {count} | `{nid}` | "
                f"{markdown_cell(catalog.get(nid, 'Unknown'))} | "
                f"{markdown_cell(locations)} |"
            )
    else:
        lines.append("| 0 | — | — | None |")

    lines.extend(
        [
            "",
            "## Imports returning errors",
            "",
            "These imports are registered, but their behavior or guest arguments may still need work.",
            "",
            "| Calls | NID | Catalog name | Result | Local implementation |",
            "| ---: | --- | --- | --- | --- |",
        ]
    )
    if failed_rows:
        for (nid, result), count in failed_rows:
            locations = "<br>".join(implemented.get(nid, [])) or "Not found"
            lines.append(
                f"| {count} | `{nid}` | "
                f"{markdown_cell(catalog.get(nid, 'Unknown'))} | "
                f"{markdown_cell(result)} | {markdown_cell(locations)} |"
            )
    else:
        lines.append("| 0 | — | — | — | None |")

    lines.extend(
        [
            "",
            "This report is generated only from Touché PX5 source, its local name catalog, "
            "and runtime diagnostics. It does not embed third-party SDK source code.",
            "",
        ]
    )
    return "\n".join(lines)


def audit_log(args: argparse.Namespace) -> int:
    catalog = build_name_catalog(args.names)
    implemented = scan_implemented_exports(args.source_root)
    unresolved, failed = parse_import_diagnostics(args.log)
    report = render_audit_report(
        args.log,
        catalog,
        implemented,
        unresolved,
        failed,
        args.limit,
    )

    if args.output:
        try:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(report, encoding="utf-8", newline="\n")
        except OSError as error:
            print(f"Unable to write audit report '{args.output}': {error}", file=sys.stderr)
            return 1
        print(f"Wrote ABI audit to {args.output}")
    else:
        print(report)
    return 0


def create_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Inspect the SharpEmu PS5 export-name/NID catalog.",
        epilog=(
            "Examples:\n"
            "  python scripts/aerolib_catalog.py lookup Zxa0VhQVTsk\n"
            "  python scripts/aerolib_catalog.py lookup sceKernelWaitSema\n"
            "  python scripts/aerolib_catalog.py search VideoOut --limit 20\n"
            "  python scripts/aerolib_catalog.py export\n"
            "  python scripts/aerolib_catalog.py coverage\n"
            "  python scripts/aerolib_catalog.py audit-log emulator.log"
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--names",
        type=Path,
        default=DEFAULT_NAMES_FILE,
        help=f"source name list (default: {DEFAULT_NAMES_FILE})",
    )

    subparsers = parser.add_subparsers(dest="command", required=True)

    lookup_parser = subparsers.add_parser(
        "lookup", help="resolve a NID or calculate the NID for an export name"
    )
    lookup_parser.add_argument("value", help="11-character NID or exact export name")
    lookup_parser.set_defaults(handler=lookup)

    search_parser = subparsers.add_parser(
        "search", help="find export names and print matching NID/name pairs"
    )
    search_parser.add_argument("query", help="name substring or regular expression")
    search_parser.add_argument(
        "--limit", type=int, default=50, help="maximum matches; 0 means unlimited"
    )
    search_parser.add_argument(
        "--case-sensitive", action="store_true", help="match case exactly"
    )
    search_parser.add_argument(
        "--regex", action="store_true", help="treat the query as a regular expression"
    )
    search_parser.set_defaults(handler=search)

    export_parser = subparsers.add_parser(
        "export", help="write every NID/name pair to a tab-separated text file"
    )
    export_parser.add_argument(
        "output",
        type=Path,
        nargs="?",
        default=DEFAULT_EXPORT_FILE,
        help=f"output file (default: {DEFAULT_EXPORT_FILE})",
    )
    export_parser.add_argument(
        "--sort",
        choices=("source", "nid", "name"),
        default="nid",
        help="output ordering (default: nid)",
    )
    export_parser.set_defaults(handler=export_catalog)

    coverage_parser = subparsers.add_parser(
        "coverage",
        help="write global catalog-to-implementation ABI coverage",
    )
    coverage_parser.add_argument(
        "output",
        type=Path,
        nargs="?",
        default=DEFAULT_COVERAGE_FILE,
        help=f"output file (default: {DEFAULT_COVERAGE_FILE})",
    )
    coverage_parser.add_argument(
        "--source-root",
        type=Path,
        default=DEFAULT_SOURCE_ROOT,
        help=f"C# source tree (default: {DEFAULT_SOURCE_ROOT})",
    )
    coverage_parser.add_argument(
        "--status",
        choices=("all", "implemented", "missing", "name-mismatch", "local-only"),
        default="all",
        help="include only one status (default: all)",
    )
    coverage_parser.add_argument(
        "--sort",
        choices=("nid", "name", "status"),
        default="status",
        help="row ordering (default: status)",
    )
    coverage_parser.add_argument(
        "--format",
        choices=("tsv", "markdown"),
        default="tsv",
        help="output format (default: tsv)",
    )
    coverage_parser.set_defaults(handler=coverage)

    audit_parser = subparsers.add_parser(
        "audit-log",
        help="rank unresolved and failing imports found in an emulator log",
    )
    audit_parser.add_argument("log", type=Path, help="Touché PX5 diagnostic log")
    audit_parser.add_argument(
        "--source-root",
        type=Path,
        default=DEFAULT_SOURCE_ROOT,
        help=f"C# source tree (default: {DEFAULT_SOURCE_ROOT})",
    )
    audit_parser.add_argument(
        "--output",
        type=Path,
        help="optional Markdown output path; stdout is used when omitted",
    )
    audit_parser.add_argument(
        "--limit",
        type=int,
        default=100,
        help="maximum rows per section; 0 means unlimited",
    )
    audit_parser.set_defaults(handler=audit_log)

    return parser


def main() -> int:
    parser = create_parser()
    args = parser.parse_args()
    return args.handler(args)


if __name__ == "__main__":
    raise SystemExit(main())
