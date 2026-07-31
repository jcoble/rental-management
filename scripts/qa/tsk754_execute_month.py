#!/usr/bin/env python3
"""Thin HTTP runner for TSK-754 month slices.

The runner deliberately plans first. It reads the YearSimulation2027 CSVs,
builds deterministic endpoint calls from supported source mappings, and sends
requests only with --execute. It never mutates the database directly.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import mimetypes
import os
import ssl
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from datetime import date
from pathlib import Path
from typing import Any, Iterable


REPO = Path(__file__).resolve().parents[2]
DEFAULT_ROOT = REPO / "Docs" / "Testing" / "YearSimulation2027"
DEFAULT_SCHEDULE = DEFAULT_ROOT / "schedule.csv"
DEFAULT_ORACLE = DEFAULT_ROOT / "financial-oracle.csv"
DEFAULT_JOURNAL = DEFAULT_ROOT / "journal.csv"
DEFAULT_CONTROL = DEFAULT_ROOT / "control-totals.json"
DEFAULT_SCANS = DEFAULT_ROOT / "scan-assets.csv"
DEFAULT_CORPUS = REPO / "output" / "pdf" / "tsk-749-year-simulation-scan-corpus"
DEFAULT_CORPUS_INDEX = DEFAULT_CORPUS / "corpus-index.csv"
DEFAULT_LEDGER = REPO / "output" / "qa" / "tsk-754-execution-ledger.csv"

SUPPORTED_SCAN_TARGETS = {
    "application": "Application",
    "lease": "LeaseAgreement",
    "expense": "Expense",
    "payment": "Payment",
    "workorder": "WorkOrder",
    "work order": "WorkOrder",
    "loan": "Loan",
    "leaseendingnotice": "LeaseEndingNotice",
    "lease ending notice": "LeaseEndingNotice",
    "propertyacquisition": "PropertyAcquisition",
    "property acquisition": "PropertyAcquisition",
}

WORKER_WORKFLOW_MARKERS = (
    ("recurring rent generation", "rent-charge"),
    ("late fee", "late-fee"),
    ("autopay", "autopay"),
    ("loan payments", "debt-service"),
    ("recurring expense", "recurring-expense"),
    ("recurring maintenance", "recurring-maintenance"),
    ("tenant notice", "tenant-notice-candidates"),
    ("notice draft", "notice-draft"),
    ("daily briefing", "daily-briefing"),
)

SCAN_ONLY_WORKFLOW_MARKERS = (
    "opening lease intake batch",
    "application intake and pipeline",
    "create and triage maintenance request",
)


@dataclass(frozen=True)
class ScheduleRow:
    order: int
    run_id: str
    simulated_clock: str
    sequence: str
    phase: str
    role: str
    client: str
    entry_mode: str
    workflow: str
    entity_refs: str
    asset_refs: str
    financial_refs: str
    exact_actions: str
    expected_result: str
    evidence_required: str
    negative_or_retry_check: str
    completion_gate: str

    @property
    def sim_date(self) -> date:
        return date.fromisoformat(self.simulated_clock[:10])


@dataclass(frozen=True)
class FinancialEvent:
    event_id: str
    entry_date: str
    effective_date: str
    event_type: str
    category: str
    property_id: str
    unit_id: str
    lease_id: str
    counterparty: str
    source_mode: str
    source_asset_id: str
    reference: str
    amount_cents: int
    memo: str


@dataclass(frozen=True)
class ScanAsset:
    asset_id: str
    planned_date: str
    filename: str
    document_family: str
    intended_target: str
    property_id: str
    unit_id: str
    lease_id: str
    financial_event_id: str


@dataclass(frozen=True)
class Action:
    run_id: str
    kind: str
    method: str
    path: str
    idempotency_key: str
    json_body: dict[str, Any] | None = None
    form_fields: dict[str, str] | None = None
    files: tuple[Path, ...] = ()
    note: str = ""


@dataclass(frozen=True)
class UnsupportedMapping:
    run_id: str
    workflow: str
    reason: str


@dataclass(frozen=True)
class Plan:
    rows: tuple[ScheduleRow, ...]
    actions: tuple[Action, ...]
    unsupported: tuple[UnsupportedMapping, ...]

    @property
    def is_supported(self) -> bool:
        return not self.unsupported


def read_csv(path: Path) -> list[dict[str, str]]:
    with path.open(newline="") as handle:
        return list(csv.DictReader(handle))


def load_schedule(path: Path) -> list[ScheduleRow]:
    rows: list[ScheduleRow] = []
    for index, row in enumerate(read_csv(path), start=1):
        rows.append(
            ScheduleRow(
                order=index,
                run_id=row["run_id"],
                simulated_clock=row["simulated_clock"],
                sequence=row["sequence"],
                phase=row["phase"],
                role=row["role"],
                client=row["client"],
                entry_mode=row["entry_mode"],
                workflow=row["workflow"],
                entity_refs=row.get("entity_refs", ""),
                asset_refs=row.get("asset_refs", ""),
                financial_refs=row.get("financial_refs", ""),
                exact_actions=row.get("exact_actions", ""),
                expected_result=row.get("expected_result", ""),
                evidence_required=row.get("evidence_required", ""),
                negative_or_retry_check=row.get("negative_or_retry_check", ""),
                completion_gate=row.get("completion_gate", ""),
            )
        )
    return rows


def load_financial_events(path: Path) -> dict[str, FinancialEvent]:
    events: dict[str, FinancialEvent] = {}
    for row in read_csv(path):
        events[row["event_id"]] = FinancialEvent(
            event_id=row["event_id"],
            entry_date=row["entry_date"],
            effective_date=row["effective_date"],
            event_type=row["event_type"],
            category=row["category"],
            property_id=row.get("property_id", ""),
            unit_id=row.get("unit_id", ""),
            lease_id=row.get("lease_id", ""),
            counterparty=row.get("counterparty", ""),
            source_mode=row.get("source_mode", ""),
            source_asset_id=row.get("source_asset_id", ""),
            reference=row.get("reference", ""),
            amount_cents=int(row.get("amount_cents") or "0"),
            memo=row.get("memo", ""),
        )
    return events


def load_scan_assets(path: Path) -> dict[str, ScanAsset]:
    assets: dict[str, ScanAsset] = {}
    for row in read_csv(path):
        assets[row["asset_id"]] = ScanAsset(
            asset_id=row["asset_id"],
            planned_date=row["planned_date"],
            filename=row["filename"],
            document_family=row["document_family"],
            intended_target=row["intended_target"],
            property_id=row.get("property_id", ""),
            unit_id=row.get("unit_id", ""),
            lease_id=row.get("lease_id", ""),
            financial_event_id=row.get("financial_event_id", ""),
        )
    return assets


def load_control_totals(path: Path) -> dict[str, Any]:
    with path.open() as handle:
        return json.load(handle)


def load_id_map(path: Path | None) -> dict[str, dict[str, str]]:
    if path is None:
        return {}
    mapping: dict[str, dict[str, str]] = {}
    for row in read_csv(path):
        ref = (row.get("ref") or row.get("source_ref") or "").strip()
        if not ref:
            raise ValueError(f"{path} contains a row without ref/source_ref")
        mapping[ref] = {key: value.strip() for key, value in row.items() if value is not None}
    return mapping


def load_corpus_index(path: Path) -> dict[str, Path]:
    if not path.exists():
        return {}
    index: dict[str, Path] = {}
    for row in read_csv(path):
        asset_id = (row.get("asset_id") or "").strip()
        relative_path = (row.get("relative_path") or "").strip()
        if asset_id and relative_path:
            index[asset_id] = Path(relative_path)
    return index


def split_refs(value: str) -> tuple[str, ...]:
    return tuple(part.strip() for part in value.split(";") if part.strip())


def normalize(value: str) -> str:
    return " ".join(value.strip().lower().split())


def stable_key(run_id: str, kind: str, subject: str = "") -> str:
    canonical = "|".join(["tsk-754", run_id.strip(), kind.strip(), subject.strip()])
    digest = hashlib.sha256(canonical.encode("utf-8")).hexdigest()[:24]
    suffix = f":{subject.strip()}" if subject.strip() else ""
    return f"tsk754:{run_id}:{kind}{suffix}:{digest}"[:128]


def slice_schedule(
    rows: Iterable[ScheduleRow],
    month: str,
    lane: str | None = None,
    simulated_date: str | None = None,
    run_ids: set[str] | None = None,
) -> list[ScheduleRow]:
    if len(month) != 7:
        raise ValueError("--month must be YYYY-MM")
    selected = [row for row in rows if row.simulated_clock.startswith(month)]
    if simulated_date:
        selected = [row for row in selected if row.simulated_clock == simulated_date]
    if run_ids:
        selected = [row for row in selected if row.run_id in run_ids]
    if lane:
        lane_norm = normalize(lane)
        selected = [
            row
            for row in selected
            if lane_norm
            in {
                normalize(row.phase),
                normalize(row.client),
                normalize(row.entry_mode),
                normalize(row.workflow),
                normalize(f"{row.phase}:{row.entry_mode}"),
            }
        ]
    return sorted(selected, key=lambda row: (row.sim_date, int(row.sequence or "0"), row.order))


def worker_key_for(row: ScheduleRow) -> str | None:
    text = normalize(f"{row.workflow} {row.entry_mode} {row.exact_actions}")
    for marker, key in WORKER_WORKFLOW_MARKERS:
        if marker in text:
            return key
    if "run due" in text or "scheduled finance worker" in text:
        return "run-due"
    return None


def scan_target(asset: ScanAsset) -> str | None:
    target = normalize(asset.intended_target)
    family = normalize(asset.document_family)
    for marker, value in SUPPORTED_SCAN_TARGETS.items():
        if marker in target or marker in family:
            return value
    return None


def dollars(cents: int) -> str:
    whole, fractional = divmod(abs(cents), 100)
    sign = "-" if cents < 0 else ""
    return f"{sign}{whole}.{fractional:02d}"


def first_event(events: dict[str, FinancialEvent], refs: Iterable[str], event_type: str) -> FinancialEvent | None:
    for ref in refs:
        event = events.get(ref)
        if event and event.event_type == event_type:
            return event
    return None


def is_scan_only_workflow(workflow: str) -> bool:
    normalized = normalize(workflow)
    return any(marker in normalized for marker in SCAN_ONLY_WORKFLOW_MARKERS)


def clock_action(row: ScheduleRow) -> Action:
    return Action(
        run_id=row.run_id,
        kind="clock.set",
        method="POST",
        path="/api/v1/dev/clock/set",
        idempotency_key=stable_key(row.run_id, "clock", row.simulated_clock),
        json_body={"date": row.simulated_clock, "timeZoneId": "America/New_York", "mode": "frozen"},
    )


def with_clock(row: ScheduleRow, *row_actions: Action) -> list[Action]:
    return [clock_action(row), *row_actions]


def build_plan(
    rows: Iterable[ScheduleRow],
    events: dict[str, FinancialEvent],
    assets: dict[str, ScanAsset],
    id_map: dict[str, dict[str, str]] | None = None,
    corpus_dir: Path = DEFAULT_CORPUS,
    corpus_index: dict[str, Path] | None = None,
    confirm_scans: bool = False,
) -> Plan:
    id_map = id_map or {}
    corpus_index = corpus_index or {}
    actions: list[Action] = []
    unsupported: list[UnsupportedMapping] = []
    selected = tuple(rows)

    for row in selected:
        workflow = normalize(row.workflow)
        asset_refs = split_refs(row.asset_refs)
        financial_refs = split_refs(row.financial_refs)

        if "post and allocate tenant receipt" in workflow:
            if asset_refs:
                unsupported.append(UnsupportedMapping(
                    row.run_id,
                    row.workflow,
                    "asset-bearing tenant receipt rows require reviewed scan-confirm mapping; refusing generic scan upload",
                ))
                continue
            event = first_event(events, financial_refs, "TenantReceipt")
            mapped_ids = id_map.get(row.run_id, {})
            tenant_account_id = mapped_ids.get("tenant_account_id")
            target_charge_entry_id = mapped_ids.get("target_charge_entry_id")
            if event is None or not tenant_account_id or not target_charge_entry_id:
                unsupported.append(UnsupportedMapping(
                    row.run_id,
                    row.workflow,
                    "tenant receipt requires TenantReceipt oracle row plus tenant_account_id and exact target_charge_entry_id in --id-map",
                ))
                continue
            actions.extend(with_clock(
                row,
                Action(
                    row.run_id,
                    "tenant.receipt",
                    "POST",
                    f"/api/v1/tenant-accounts/{tenant_account_id}/receipts",
                    stable_key(row.run_id, "tenant-receipt", event.event_id),
                    json_body={
                        "amount": dollars(event.amount_cents),
                        "effectiveOn": event.effective_date,
                        "description": event.memo or event.category,
                        "paymentMethodSummary": row.entry_mode,
                        "externalReference": event.reference,
                        "payerName": event.counterparty or None,
                        "targetChargeEntryId": int(target_charge_entry_id),
                    },
                )
            ))
            continue

        if "record, review, allocate, and pay operating expense" in workflow:
            unsupported.append(UnsupportedMapping(
                row.run_id,
                row.workflow,
                "expense creation requires an exact category/status/allocation/attachment source contract; refusing inferred mapping",
            ))
            continue

        if "bank" in workflow or "banking" in workflow:
            unsupported.append(UnsupportedMapping(
                row.run_id,
                row.workflow,
                "bank workflow requires an exact source contract; refusing generic scan upload",
            ))
            continue

        worker_key = worker_key_for(row)
        if worker_key:
            path = "/api/v1/dev/workers/run-due" if worker_key == "run-due" else f"/api/v1/dev/workers/{worker_key}/run-once"
            actions.extend(with_clock(
                row,
                Action(row.run_id, f"worker.{worker_key}", "POST", path, stable_key(row.run_id, "worker", worker_key))
            ))
            continue

        if asset_refs:
            unsupported.append(UnsupportedMapping(
                row.run_id,
                row.workflow,
                "scan schedule rows require a complete reviewed draft result-map plus confirm flow; upload-only execution is disabled",
            ))
            continue

        unsupported.append(UnsupportedMapping(row.run_id, row.workflow, "no supported HTTP mapping in source inputs"))

    return Plan(selected, tuple(actions), tuple(unsupported))


def camel_case(value: str) -> str:
    head, *tail = value.split("_")
    return head + "".join(part[:1].upper() + part[1:] for part in tail)


class HttpRunner:
    def __init__(self, base_url: str, token: str | None = None, ca_cert: Path | None = None) -> None:
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.context = ssl.create_default_context(cafile=str(ca_cert)) if ca_cert else None

    def send(self, action: Action) -> dict[str, Any]:
        url = self.base_url + action.path
        headers = {"Idempotency-Key": action.idempotency_key, "Accept": "application/json"}
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"
        if action.files:
            body, content_type = encode_multipart(action.form_fields or {}, action.files)
            headers["Content-Type"] = content_type
        else:
            body = json.dumps(action.json_body or {}).encode("utf-8") if action.json_body is not None else b"{}"
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(url, data=body, headers=headers, method=action.method)
        try:
            with urllib.request.urlopen(request, timeout=120, context=self.context) as response:
                payload = response.read().decode("utf-8")
                return {"status": response.status, "body": json.loads(payload) if payload else None}
        except urllib.error.HTTPError as ex:
            payload = ex.read().decode("utf-8")
            return {"status": ex.code, "body": json.loads(payload) if payload else payload}


def encode_multipart(fields: dict[str, str], files: Iterable[Path]) -> tuple[bytes, str]:
    boundary = "----tsk754" + hashlib.sha256("|".join(sorted(fields)).encode("utf-8")).hexdigest()[:16]
    chunks: list[bytes] = []
    for name, value in fields.items():
        chunks.extend(
            [
                f"--{boundary}\r\n".encode("utf-8"),
                f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode("utf-8"),
                str(value).encode("utf-8"),
                b"\r\n",
            ]
        )
    for path in files:
        content_type = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
        chunks.extend(
            [
                f"--{boundary}\r\n".encode("utf-8"),
                f'Content-Disposition: form-data; name="files"; filename="{path.name}"\r\n'.encode("utf-8"),
                f"Content-Type: {content_type}\r\n\r\n".encode("utf-8"),
                path.read_bytes(),
                b"\r\n",
            ]
        )
    chunks.append(f"--{boundary}--\r\n".encode("utf-8"))
    return b"".join(chunks), f"multipart/form-data; boundary={boundary}"


def append_completion_ledger(ledger: Path, proof_csv: Path, planned: Iterable[ScheduleRow]) -> int:
    planned_by_id = {row.run_id: row for row in planned}
    existing_completed: set[str] = set()
    if ledger.exists():
        for row in read_csv(ledger):
            result = normalize(row.get("result", ""))
            if row.get("run_id") and (result.startswith("completed") or result.startswith("corrected")):
                existing_completed.add(row["run_id"])
    proof_rows = read_csv(proof_csv)
    passed = [
        row
        for row in unique_passed_rows(proof_rows)
        if row.get("run_id", "") in planned_by_id
        and row.get("run_id", "") not in existing_completed
    ]
    if not passed:
        return 0
    ledger.parent.mkdir(parents=True, exist_ok=True)
    exists = ledger.exists()
    fieldnames = [
        "sim_date",
        "run_id",
        "client",
        "entry_mode",
        "asset_or_reference",
        "result",
        "system_record",
        "amount",
        "allocation_or_balance",
        "evidence",
    ]
    with ledger.open("a", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        if not exists:
            writer.writeheader()
        for proof in passed:
            run = planned_by_id[proof["run_id"]]
            writer.writerow(
                {
                    "sim_date": run.simulated_clock,
                    "run_id": run.run_id,
                    "client": run.client,
                    "entry_mode": run.entry_mode,
                    "asset_or_reference": proof.get("asset_or_reference", run.asset_refs or run.financial_refs),
                    "result": proof.get("result", "Completed by tsk754_execute_month proof"),
                    "system_record": proof.get("system_record", ""),
                    "amount": proof.get("amount", ""),
                    "allocation_or_balance": proof.get("allocation_or_balance", ""),
                    "evidence": proof.get("evidence", ""),
                }
            )
    return len(passed)


def unique_passed_rows(rows: Iterable[dict[str, str]]) -> list[dict[str, str]]:
    seen_run_ids: set[str] = set()
    passed: list[dict[str, str]] = []
    for row in rows:
        run_id = row.get("run_id", "")
        if run_id in seen_run_ids:
            continue
        if normalize(row.get("passed", "")) not in {"true", "yes", "1", "passed", "pass"}:
            continue
        seen_run_ids.add(run_id)
        passed.append(row)
    return passed


def print_plan(plan: Plan) -> None:
    print(f"Rows: {len(plan.rows)}")
    print(f"Actions: {len(plan.actions)}")
    print(f"Unsupported: {len(plan.unsupported)}")
    for action in plan.actions:
        body = action.form_fields if action.form_fields is not None else action.json_body
        print(json.dumps({
            "run_id": action.run_id,
            "kind": action.kind,
            "method": action.method,
            "path": action.path,
            "idempotency_key": action.idempotency_key,
            "body": body,
            "files": [str(path) for path in action.files],
            "note": action.note,
        }, sort_keys=True))
    for item in plan.unsupported:
        print(json.dumps({
            "run_id": item.run_id,
            "workflow": item.workflow,
            "unsupported": item.reason,
        }, sort_keys=True))


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--schedule", type=Path, default=DEFAULT_SCHEDULE)
    parser.add_argument("--financial-oracle", type=Path, default=DEFAULT_ORACLE)
    parser.add_argument("--journal", type=Path, default=DEFAULT_JOURNAL)
    parser.add_argument("--control-totals", type=Path, default=DEFAULT_CONTROL)
    parser.add_argument("--scan-assets", type=Path, default=DEFAULT_SCANS)
    parser.add_argument("--corpus-dir", type=Path, default=DEFAULT_CORPUS)
    parser.add_argument("--corpus-index", type=Path, default=DEFAULT_CORPUS_INDEX)
    parser.add_argument("--id-map", type=Path)
    parser.add_argument("--month", required=True, help="YYYY-MM month slice")
    parser.add_argument("--date", help="Optional exact YYYY-MM-DD schedule date within --month")
    parser.add_argument(
        "--run-id",
        action="append",
        default=[],
        help="Optional exact run id; repeat to select several runs within --month/--date",
    )
    parser.add_argument("--lane", help="Exact phase/client/entry_mode/workflow lane filter")
    parser.add_argument("--base-url", default=os.environ.get("RENTAL_COMMAND_API", "https://localhost:5666"))
    parser.add_argument("--token", default=os.environ.get("RENTAL_COMMAND_TOKEN"))
    parser.add_argument("--execute", action="store_true", help="Send HTTP requests. Default is dry-run plan only.")
    parser.add_argument("--ca-cert", type=Path, help="Optional CA bundle for verified TLS.")
    parser.add_argument("--confirm-scans", action="store_true", help="Reserved for reviewed draft confirmation; fails closed without draft ids.")
    parser.add_argument("--completion-ledger", type=Path, default=DEFAULT_LEDGER)
    parser.add_argument("--proof-csv", type=Path, help="Read-only proof rows; only passed rows are appended to the completion ledger.")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    schedule = load_schedule(args.schedule)
    events = load_financial_events(args.financial_oracle)
    _ = read_csv(args.journal)
    _ = load_control_totals(args.control_totals)
    assets = load_scan_assets(args.scan_assets)
    corpus_index = load_corpus_index(args.corpus_index)
    id_map = load_id_map(args.id_map)
    rows = slice_schedule(
        schedule,
        args.month,
        args.lane,
        simulated_date=args.date,
        run_ids=set(args.run_id),
    )
    plan = build_plan(rows, events, assets, id_map, args.corpus_dir, corpus_index, args.confirm_scans)
    print_plan(plan)

    if plan.unsupported:
        print("Refusing to execute because at least one selected row has no supported mapping.", file=sys.stderr)
        return 2
    if not args.execute:
        return 0 if not plan.unsupported else 2
    if not args.token:
        print("--execute requires --token or RENTAL_COMMAND_TOKEN.", file=sys.stderr)
        return 2

    runner = HttpRunner(args.base_url, args.token, args.ca_cert)
    for action in plan.actions:
        result = runner.send(action)
        print(json.dumps({"run_id": action.run_id, "kind": action.kind, "response": result}, sort_keys=True))
        if not (200 <= int(result["status"]) < 300):
            return 1

    if args.proof_csv:
        appended = append_completion_ledger(args.completion_ledger, args.proof_csv, rows)
        print(f"Completion ledger rows appended from passed proof: {appended}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
