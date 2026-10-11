#!/usr/bin/env python3
"""Fail-closed coverage gate for the TSK-781 wholesale atomic-runtime cutover."""

from __future__ import annotations

import argparse
import csv
import re
from collections import Counter
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "output/qa/tsk781-atomic-cutover-manifest.csv"
OUTPUT = ROOT / "output/qa/tsk781-atomic-runtime-coverage.csv"
DATA = ROOT / "RentalCommand.Data"

FORBIDDEN_DIRECT_TOKENS = (
    "IAtomicWriteAttempt",
    "IAtomicPersistenceSession",
    "IAtomicUnitOfWork",
    "IAtomicCommandHandler<",
    ".HandleAsync(",
    "GetRequiredService",
    "IServiceProvider",
)


@dataclass(frozen=True)
class Pair:
    command: str
    result: str

    @property
    def command_short(self) -> str:
        return self.command.rsplit(".", 1)[-1]

    @property
    def result_short(self) -> str:
        return self.result.rsplit(".", 1)[-1]


def registrations() -> list[dict[str, str]]:
    with MANIFEST.open(newline="") as file:
        return [
            row
            for row in csv.DictReader(file)
            if row["category"] == "api_engine_registration"
        ]


def source_files() -> list[tuple[Path, str]]:
    return [
        (path, path.read_text(encoding="utf-8"))
        for path in DATA.rglob("*.cs")
        if "AtomicRuntime" in path.name
        or "AtomicAttempt" in path.name
        or "AtomicCommandAttempt" in path.name
        or "Atomic/Runtime" in path.as_posix()
    ]


def mentions_pair(source: str, pair: Pair) -> bool:
    command = re.escape(pair.command_short)
    result = re.escape(pair.result_short)
    generic = re.compile(
        rf"(?:AtomicRuntimeJsonAttempt|[A-Za-z0-9_]*AtomicRuntimeAttempt)"
        rf"\s*<\s*{command}\s*,\s*{result}\s*>",
        re.MULTILINE,
    )
    factory = re.compile(
        rf"typeof\s*\(\s*TCommand\s*\)\s*==\s*typeof\s*\(\s*{command}\s*\)"
        rf"[\s\S]{{0,500}}?"
        rf"typeof\s*\(\s*TResult\s*\)\s*==\s*typeof\s*\(\s*{result}\s*\)",
        re.MULTILINE,
    )
    broad_attempt = (
        pair.command_short in source
        and pair.result_short in source
        and "AtomicRuntimeAttempt" in source
    )
    return generic.search(source) is not None or factory.search(source) is not None or broad_attempt


def is_factory_mapping(source: str, pair: Pair) -> bool:
    if "CreateAttempt<TCommand, TResult>" not in source and "TryCreate<TCommand, TResult>" not in source:
        return False
    command = re.escape(pair.command_short)
    result = re.escape(pair.result_short)
    command_type = re.search(
        rf"typeof\s*\(\s*TCommand\s*\)[\s\S]{{0,80}}?"
        rf"typeof\s*\(\s*{command}\s*\)",
        source,
    )
    result_type = re.search(
        rf"typeof\s*\(\s*TResult\s*\)[\s\S]{{0,80}}?"
        rf"typeof\s*\(\s*{result}\s*\)",
        source,
    )
    return command_type is not None and result_type is not None


def classify(
    pair: Pair,
    files: list[tuple[Path, str]],
) -> tuple[str, str, str]:
    candidates = [(path, source) for path, source in files if mentions_pair(source, pair)]
    direct = [
        (path, source)
        for path, source in candidates
        if "/Generated/" not in path.as_posix()
        and not any(token in source for token in FORBIDDEN_DIRECT_TOKENS)
    ]
    mappings = [
        path
        for path, source in candidates
        if is_factory_mapping(source, pair)
    ]
    direct_paths = ";".join(
        sorted(path.relative_to(ROOT).as_posix() for path, _ in direct)
    )
    mapping_paths = ";".join(
        sorted(path.relative_to(ROOT).as_posix() for path in mappings)
    )
    if not direct:
        status = "missing_direct_atomic"
    elif not mappings:
        status = "direct_atomic_unmapped"
    else:
        status = "direct_atomic_mapped"
    return status, direct_paths, mapping_paths


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--require-complete",
        action="store_true",
        help="Exit non-zero unless every unique command/result pair is direct and mapped.",
    )
    args = parser.parse_args()

    rows = registrations()
    if len(rows) != 202:
        raise SystemExit(f"Expected 202 registration rows, found {len(rows)}")
    pairs = sorted(
        {Pair(row["symbol"], row["result"]) for row in rows},
        key=lambda pair: (pair.command, pair.result),
    )
    if len(pairs) != 191:
        raise SystemExit(f"Expected 191 unique command/result pairs, found {len(pairs)}")

    files = source_files()
    registration_counts = Counter(
        Pair(row["symbol"], row["result"])
        for row in rows
    )
    caller_counts = Counter()
    handlers: dict[Pair, set[str]] = {}
    for row in rows:
        pair = Pair(row["symbol"], row["result"])
        caller_counts[pair] += int(row["caller_count"])
        handlers.setdefault(pair, set()).add(row["handler"])
    output_rows: list[dict[str, str | int]] = []
    counts: Counter[str] = Counter()
    for pair in pairs:
        status, direct_paths, mapping_paths = classify(pair, files)
        counts[status] += 1
        if caller_counts[pair] == 0:
            disposition = "verify_then_delete_registration_and_legacy_surface"
        elif status == "direct_atomic_mapped":
            disposition = "wire_canonical_atomic_attempt"
        elif status == "direct_atomic_unmapped":
            disposition = "add_canonical_factory_mapping"
        else:
            disposition = "implement_direct_atomic_attempt"
        output_rows.append(
            {
                "command": pair.command,
                "result": pair.result,
                "registration_count": registration_counts[pair],
                "observed_caller_count": caller_counts[pair],
                "current_handlers": ";".join(sorted(handlers[pair])),
                "status": status,
                "planned_disposition": disposition,
                "direct_source_paths": direct_paths,
                "factory_mapping_paths": mapping_paths,
            }
        )

    with OUTPUT.open("w", newline="", encoding="utf-8") as file:
        writer = csv.DictWriter(file, fieldnames=list(output_rows[0]))
        writer.writeheader()
        writer.writerows(output_rows)

    print(f"registrations={len(rows)}")
    print(f"unique_command_result_pairs={len(pairs)}")
    for status in (
        "direct_atomic_mapped",
        "direct_atomic_unmapped",
        "missing_direct_atomic",
    ):
        print(f"{status}={counts[status]}")
    print(f"coverage_csv={OUTPUT.relative_to(ROOT)}")

    return (
        1
        if args.require_complete and counts["direct_atomic_mapped"] != len(pairs)
        else 0
    )


if __name__ == "__main__":
    raise SystemExit(main())
