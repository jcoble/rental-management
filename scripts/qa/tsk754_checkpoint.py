#!/usr/bin/env python3
"""Read-only checkpoint and remaining-work helper for TSK-754."""

from __future__ import annotations

import argparse
import csv
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request
from collections import Counter, defaultdict
from dataclasses import dataclass
from datetime import date
from pathlib import Path
from typing import Iterable


REPO = Path(__file__).resolve().parents[2]
DEFAULT_SCHEDULE = REPO / "Docs" / "Testing" / "YearSimulation2027" / "schedule.csv"
DEFAULT_LEDGER = REPO / "output" / "qa" / "tsk-754-execution-ledger.csv"
DEFAULT_BUG_LEDGER = REPO / "output" / "qa" / "tsk-754-bug-ledger.csv"
DEFAULT_SCAN_MANIFEST = REPO / "Docs" / "Testing" / "YearSimulation2027" / "scan-assets.csv"
DEFAULT_PORTFOLIO_PLAN = REPO / "Docs" / "Testing" / "YearSimulation2027" / "portfolio.csv"
DEFAULT_UNIT_PLAN = REPO / "Docs" / "Testing" / "YearSimulation2027" / "units.csv"
DEFAULT_LEASE_PLAN = REPO / "Docs" / "Testing" / "YearSimulation2027" / "leases.csv"
DEFAULT_CORPUS_INDEX = (
    REPO / "output" / "pdf" / "tsk-749-year-simulation-scan-corpus" / "corpus-index.csv"
)
DEFAULT_EVIDENCE_DIR = REPO / "output" / "qa" / "tsk754-evidence"


COMPLETED_PREFIXES = (
    "completed",
    "corrected",
)
TERMINAL_RESULT_EXACT = (
    "rejected as expected",
)
TERMINAL_RESULT_PREFIXES = COMPLETED_PREFIXES + (
    "safely blocked after",
    "blocked by existing",
    "blocked by ys-168 planner correction",
)
NONTERMINAL_RESULT_PREFIXES = (
    "pending",
    "partial",
    "variance",
    "failed",
    "blocked at business date",
    "blocked before payment",
    "blocked by ys-113",
    "web and database completed",
)
BLOCKER_STATUS_PREFIXES = (
    "open",
    "reopened",
    "confirmed",
    "fix in progress",
    "fix committed",
    "planner correction required",
    "planner prerequisite repaired",
)
BLOCKING_STATUS_CONTAINS = (
    "pending",
)
TERMINAL_STATUS_EXACT = (
    "fixed",
    "planner correction recorded",
    "retracted",
)
TERMINAL_STATUS_PREFIXES = (
    "fixed and",
    "fixed by",
    "fixed in",
    "fixed;",
    "fixed locally and live",
    "fixed locally; awaiting commit",
    "fixed locally; planner correction recorded",
    "fixed locally; source contract verified",
)


@dataclass(frozen=True)
class ScheduleRun:
    order: int
    run_id: str
    simulated_clock: str
    sequence: str
    phase: str
    role: str
    client: str
    entry_mode: str
    workflow: str
    asset_refs: str
    financial_refs: str

    @property
    def sim_date(self) -> date:
        return parse_date(self.simulated_clock)


@dataclass(frozen=True)
class LedgerRow:
    order: int
    sim_date_text: str
    run_id: str
    result: str
    client: str
    entry_mode: str
    evidence: str
    asset_or_reference: str = ""
    system_record: str = ""
    amount: str = ""
    allocation_or_balance: str = ""

    @property
    def sim_date(self) -> date:
        return parse_date(self.sim_date_text)

    @property
    def is_complete(self) -> bool:
        return is_terminal_result(self.result)

    def conflict_key(self) -> tuple[str, str, str, str, str]:
        return (
            self.sim_date_text.strip(),
            self.client.strip(),
            self.entry_mode.strip(),
            self.result.strip(),
            self.evidence.strip(),
        )


@dataclass(frozen=True)
class DuplicateReport:
    historical: dict[str, list[LedgerRow]]
    exact_duplicate: dict[str, list[LedgerRow]]
    unordered_authoritative: dict[str, list[LedgerRow]]

    @property
    def blocking(self) -> dict[str, list[LedgerRow]]:
        return self.exact_duplicate | self.unordered_authoritative


@dataclass(frozen=True)
class BugRow:
    bug_id: str
    first_seen_sim_date: str
    severity: str
    status: str
    summary: str

    @property
    def sim_date(self) -> date:
        return parse_date(self.first_seen_sim_date)

    @property
    def is_blocker(self) -> bool:
        return is_blocking_bug_status(self.status)


def parse_date(value: str) -> date:
    return date.fromisoformat(value.strip()[:10])


def normalize(value: str | None) -> str:
    return " ".join((value or "").strip().lower().split())


def is_terminal_result(result: str) -> bool:
    normalized = normalize(result)
    if normalized in TERMINAL_RESULT_EXACT:
        return True
    return any(normalized.startswith(prefix) for prefix in TERMINAL_RESULT_PREFIXES)


def is_known_nonterminal_result(result: str) -> bool:
    normalized = normalize(result)
    return any(normalized.startswith(prefix) for prefix in NONTERMINAL_RESULT_PREFIXES)


def is_unknown_result(result: str) -> bool:
    normalized = normalize(result)
    return bool(normalized) and not is_terminal_result(normalized) and not is_known_nonterminal_result(normalized)


def is_terminal_bug_status(status: str) -> bool:
    normalized = normalize(status)
    if normalized in TERMINAL_STATUS_EXACT:
        return True
    if any(marker in normalized for marker in BLOCKING_STATUS_CONTAINS):
        return False
    return any(normalized.startswith(prefix) for prefix in TERMINAL_STATUS_PREFIXES)


def is_blocking_bug_status(status: str) -> bool:
    normalized = normalize(status)
    if not normalized or is_terminal_bug_status(normalized):
        return False
    if any(marker in normalized for marker in BLOCKING_STATUS_CONTAINS):
        return True
    return any(normalized.startswith(prefix) for prefix in BLOCKER_STATUS_PREFIXES)


def is_unknown_bug_status(status: str) -> bool:
    normalized = normalize(status)
    return bool(normalized) and not is_terminal_bug_status(normalized) and not is_blocking_bug_status(normalized)


def read_csv(path: Path) -> list[dict[str, str]]:
    if not path.exists():
        raise FileNotFoundError(path)
    with path.open(newline="") as handle:
        return list(csv.DictReader(handle))


def sql_quote(value: str | None) -> str:
    return "'" + (value or "").replace("'", "''") + "'"


def cents_to_decimal_text(value: str) -> str:
    cents = int(value or "0")
    sign = "-" if cents < 0 else ""
    cents = abs(cents)
    return f"{sign}{cents // 100}.{cents % 100:02d}"


def values_clause(rows: Iterable[tuple[str, ...]], width: int) -> str:
    materialized = list(rows)
    if not materialized:
        nulls = ", ".join("NULL::text" for _ in range(width))
        return f"SELECT * FROM (VALUES ({nulls})) AS empty_values WHERE false"
    return "VALUES\n  " + ",\n  ".join(
        "(" + ", ".join(sql_quote(value) for value in row) + ")" for row in materialized
    )


def build_opening_control_sql(
    *,
    portfolio_id: int,
    as_of: str,
    portfolio_rows: list[dict[str, str]],
    unit_rows: list[dict[str, str]],
    lease_rows: list[dict[str, str]],
    scan_rows: list[dict[str, str]],
) -> str:
    planned_properties = [
        (
            row["property_id"],
            row["property_name"],
            row["address"],
            row["city"],
            row["state"],
            row["postal_code"],
        )
        for row in portfolio_rows
        if row.get("property_id", "").strip()
    ]
    planned_units = [
        (
            row["unit_id"],
            row["property_id"],
            row["label"],
        )
        for row in unit_rows
        if row.get("unit_id", "").strip()
    ]
    planned_leases = [
        (
            row["lease_id"],
            row["unit_id"],
            row["tenant_email"],
            row["start_date"],
            row["end_date"],
            cents_to_decimal_text(row["monthly_rent_cents"]),
            cents_to_decimal_text(row["deposit_cents"]),
            row["rent_due_day"],
        )
        for row in lease_rows
        if row.get("origin") == "Opening lease scan"
    ]
    planned_opening_assets = [
        (
            row["asset_id"],
            row["filename"],
            row["lease_id"],
        )
        for row in scan_rows
        if row.get("intended_target") == "Lease"
        and row.get("asset_id", "").strip()
        and row.get("asset_id", "").strip() <= "SCN-0036"
        and row.get("lease_id", "").strip()
    ]

    return f"""WITH
params AS (
  SELECT {portfolio_id}::int AS portfolio_id, DATE {sql_quote(as_of)} AS as_of
),
planned_properties(property_key, name, address_line1, city, state, postal_code) AS (
  {values_clause(planned_properties, 6)}
),
planned_units(unit_key, property_key, unit_number) AS (
  {values_clause(planned_units, 3)}
),
planned_leases(lease_key, unit_key, tenant_email, start_on, end_on, monthly_rent, deposit_amount, rent_due_day) AS (
  {values_clause(planned_leases, 8)}
),
    planned_opening_assets(asset_id, filename, lease_key) AS (
      {values_clause(planned_opening_assets, 3)}
),
matched_properties AS (
  SELECT pp.property_key, p."Id" AS property_id
  FROM planned_properties pp
  JOIN params ON true
  JOIN "Properties" p
    ON p."PortfolioId" = params.portfolio_id
   AND p."DeletedAt" IS NULL
   AND lower(p."Name") = lower(pp.name)
   AND lower(p."AddressLine1") = lower(pp.address_line1)
   AND lower(p."City") = lower(pp.city)
   AND lower(p."State") = lower(pp.state)
   AND lower(p."PostalCode") = lower(pp.postal_code)
),
matched_units AS (
  SELECT pu.unit_key, pu.property_key, u."Id" AS unit_id, mp.property_id
  FROM planned_units pu
  JOIN matched_properties mp ON mp.property_key = pu.property_key
  JOIN params ON true
  JOIN "Units" u
    ON u."PortfolioId" = params.portfolio_id
   AND u."DeletedAt" IS NULL
   AND u."PropertyId" = mp.property_id
   AND lower(u."UnitNumber") = lower(pu.unit_number)
),
matched_opening_leases AS (
  SELECT
    pl.lease_key,
    lm."Id" AS lease_management_id,
    la."Id" AS agreement_id,
    ta."Id" AS tenant_account_id
  FROM planned_leases pl
  JOIN matched_units mu ON mu.unit_key = pl.unit_key
  JOIN params ON true
  JOIN "LeaseManagements" lm
    ON lm."PortfolioId" = params.portfolio_id
   AND lm."PropertyId" = mu.property_id
   AND lm."UnitId" = mu.unit_id
   AND lm."CanceledAtUtc" IS NULL
  JOIN "LeaseAgreements" la
    ON la."PortfolioId" = params.portfolio_id
   AND la."LeaseManagementId" = lm."Id"
   AND la."TermStartOn" = pl.start_on::date
   AND la."TermEndOn" = pl.end_on::date
   AND la."BaseRentAmount" = pl.monthly_rent::numeric
   AND la."SecurityDepositObligation" = pl.deposit_amount::numeric
   AND la."RentDueDay" = pl.rent_due_day::smallint
   AND la."FullyExecutedAtUtc" IS NOT NULL
   AND la."VoidedAtUtc" IS NULL
   AND la."DraftCanceledAtUtc" IS NULL
  JOIN "LeaseManagementParties" lmp
    ON lmp."PortfolioId" = params.portfolio_id
   AND lmp."LeaseManagementId" = lm."Id"
   AND lmp."Role" = 'PrimaryTenant'
  JOIN "Tenants" t
    ON t."PortfolioId" = params.portfolio_id
   AND t."Id" = lmp."TenantId"
   AND t."DeletedAt" IS NULL
   AND lower(t."Email") = lower(pl.tenant_email)
  LEFT JOIN "TenantAccounts" ta
    ON ta."PortfolioId" = params.portfolio_id
   AND ta."LeaseManagementId" = lm."Id"
   AND ta."ClosedAtUtc" IS NULL
),
named_opening_attachments AS (
  SELECT
    poa.asset_id,
    poa.filename,
    poa.lease_key,
    draft."Id" AS scan_draft_id,
    sf."Id" AS stored_file_id,
    sf."FileName" AS stored_file_name,
    sf."FilePath" AS stored_file_path,
    'stored-file-scn-token'::text AS match_method
  FROM planned_opening_assets poa
  JOIN matched_opening_leases mol ON mol.lease_key = poa.lease_key
  JOIN params ON true
  JOIN "StoredFiles" sf
    ON sf."PortfolioId" = params.portfolio_id
   AND sf."EntityType" = 'LeaseAgreement'
   AND sf."EntityId" = mol.agreement_id
   AND sf."DeletedAt" IS NULL
   AND (
     lower(coalesce(sf."FileName", '')) LIKE '%' || lower(poa.asset_id) || '%'
     OR lower(coalesce(sf."FilePath", '')) LIKE '%' || lower(poa.asset_id) || '%'
   )
  LEFT JOIN "ScanDrafts" draft
    ON draft."PortfolioId" = params.portfolio_id
   AND draft."Status" = 'Confirmed'
   AND draft."TargetEntityType" = 'LeaseAgreement'
   AND draft."ConfirmedEntityId" = mol.agreement_id
   AND draft."SourceStoredFileId" = sf."Id"
),
generic_scan_candidates AS (
  SELECT
    poa.asset_id,
    poa.filename,
    poa.lease_key,
    draft."Id" AS scan_draft_id,
    sf."Id" AS stored_file_id,
    sf."FileName" AS stored_file_name,
    sf."FilePath" AS stored_file_path,
    COUNT(*) OVER (PARTITION BY poa.asset_id) AS candidate_count
  FROM planned_opening_assets poa
  JOIN matched_opening_leases mol ON mol.lease_key = poa.lease_key
  JOIN params ON true
  JOIN "ScanDrafts" draft
    ON draft."PortfolioId" = params.portfolio_id
   AND draft."Status" = 'Confirmed'
   AND draft."TargetEntityType" = 'LeaseAgreement'
   AND draft."ConfirmedEntityId" = mol.agreement_id
  JOIN "StoredFiles" sf
    ON sf."PortfolioId" = params.portfolio_id
   AND sf."Id" = draft."SourceStoredFileId"
   AND sf."EntityType" = 'LeaseAgreement'
   AND sf."EntityId" = mol.agreement_id
   AND sf."DeletedAt" IS NULL
  WHERE lower(coalesce(sf."FileName", '')) ~ '^[0-9]+\\.(jpg|jpeg)$'
),
generic_opening_attachments AS (
  SELECT
    asset_id,
    filename,
    lease_key,
    scan_draft_id,
    stored_file_id,
    stored_file_name,
    stored_file_path,
    'generic-confirmed-draft'::text AS match_method
  FROM generic_scan_candidates
  WHERE candidate_count = 1
),
opening_attachment_candidates AS (
  SELECT
    asset_id,
    filename,
    lease_key,
    scan_draft_id,
    stored_file_id,
    stored_file_name,
    stored_file_path,
    'stored-file-scn-token'::text AS match_method
  FROM named_opening_attachments
  UNION ALL
  SELECT
    asset_id,
    filename,
    lease_key,
    scan_draft_id,
    stored_file_id,
    stored_file_name,
    stored_file_path,
    match_method
  FROM generic_opening_attachments
  WHERE NOT EXISTS (
    SELECT 1
    FROM named_opening_attachments named
    WHERE named.asset_id = generic_opening_attachments.asset_id
  )
),
opening_attachments AS (
  SELECT *
  FROM opening_attachment_candidates
),
duplicate_opening_assets AS (
  SELECT
    asset_id,
    filename,
    COUNT(DISTINCT scan_draft_id) FILTER (WHERE scan_draft_id IS NOT NULL)::int AS confirmed_draft_count,
    COUNT(DISTINCT stored_file_id)::int AS active_attachment_count,
    COUNT(*)::int AS candidate_count
  FROM opening_attachment_candidates
  GROUP BY asset_id, filename
  HAVING COUNT(*) > 1
),
ambiguous_generic_opening_assets AS (
  SELECT
    asset_id,
    filename,
    lease_key,
    COUNT(*)::int AS candidate_count,
    jsonb_agg(
      jsonb_build_object(
        'scanDraftId', scan_draft_id,
        'storedFileId', stored_file_id,
        'storedFileName', stored_file_name
      )
      ORDER BY stored_file_id
    ) AS candidates
  FROM generic_scan_candidates
  WHERE candidate_count > 1
  GROUP BY asset_id, filename, lease_key
),
generic_opening_fallback_assets AS (
  SELECT asset_id, filename, lease_key, scan_draft_id, stored_file_id, stored_file_name
  FROM generic_opening_attachments
),
missing_plan_leases AS (
  SELECT pl.lease_key, pl.unit_key, pl.tenant_email
  FROM planned_leases pl
  LEFT JOIN matched_opening_leases mol ON mol.lease_key = pl.lease_key
  WHERE mol.lease_key IS NULL
),
missing_opening_assets AS (
  SELECT poa.asset_id, poa.filename, poa.lease_key
  FROM planned_opening_assets poa
  LEFT JOIN opening_attachments attachment ON attachment.asset_id = poa.asset_id
  WHERE attachment.asset_id IS NULL
),
extra_active_properties AS (
  SELECT p."Id", p."Name", p."CreatedAt"
  FROM params
  JOIN "Properties" p
    ON p."PortfolioId" = params.portfolio_id
   AND p."DeletedAt" IS NULL
   AND p."CreatedAt" < (params.as_of + INTERVAL '1 day')
  LEFT JOIN matched_properties mp ON mp.property_id = p."Id"
  WHERE mp.property_id IS NULL
),
future_created_extra_properties AS (
  SELECT p."Id", p."Name", p."CreatedAt"
  FROM params
  JOIN "Properties" p
    ON p."PortfolioId" = params.portfolio_id
   AND p."DeletedAt" IS NULL
   AND p."CreatedAt" >= (params.as_of + INTERVAL '1 day')
  LEFT JOIN matched_properties mp ON mp.property_id = p."Id"
  WHERE mp.property_id IS NULL
)
SELECT jsonb_build_object(
  'portfolioId', (SELECT portfolio_id FROM params),
  'asOf', (SELECT as_of FROM params),
  'expectedProperties', (SELECT COUNT(*) FROM planned_properties),
  'matchedPlanProperties', (SELECT COUNT(DISTINCT property_id) FROM matched_properties),
  'extraActiveProperties', (SELECT COUNT(*) FROM extra_active_properties),
  'expectedUnits', (SELECT COUNT(*) FROM planned_units),
  'matchedPlanUnits', (SELECT COUNT(DISTINCT unit_id) FROM matched_units),
  'expectedOpeningLeases', (SELECT COUNT(*) FROM planned_leases),
  'matchedOpeningLeases', (SELECT COUNT(DISTINCT lease_management_id) FROM matched_opening_leases),
  'matchedOpenTenantAccounts', (SELECT COUNT(DISTINCT tenant_account_id) FROM matched_opening_leases WHERE tenant_account_id IS NOT NULL),
  'expectedOpeningAttachments', (SELECT COUNT(*) FROM planned_opening_assets),
  'matchedOpeningAttachments', (SELECT COUNT(*) FROM opening_attachments),
  'genericOpeningFallbackAssets', COALESCE((SELECT jsonb_agg(to_jsonb(generic) ORDER BY generic.asset_id) FROM generic_opening_fallback_assets generic), '[]'::jsonb),
  'ambiguousGenericOpeningAssets', COALESCE((SELECT jsonb_agg(to_jsonb(ambiguous) ORDER BY ambiguous.asset_id) FROM ambiguous_generic_opening_assets ambiguous), '[]'::jsonb),
  'missingPlanLeases', COALESCE((SELECT jsonb_agg(to_jsonb(missing) ORDER BY missing.lease_key) FROM missing_plan_leases missing), '[]'::jsonb),
  'missingOpeningAssets', COALESCE((SELECT jsonb_agg(to_jsonb(missing) ORDER BY missing.asset_id) FROM missing_opening_assets missing), '[]'::jsonb),
  'duplicateOpeningAssets', COALESCE((SELECT jsonb_agg(to_jsonb(dup) ORDER BY dup.asset_id) FROM duplicate_opening_assets dup), '[]'::jsonb),
  'extraActivePropertySample', COALESCE((SELECT jsonb_agg(to_jsonb(extra) ORDER BY extra."Id") FROM (SELECT * FROM extra_active_properties ORDER BY "Id" LIMIT 20) extra), '[]'::jsonb),
  'futureCreatedExtraProperties', (SELECT COUNT(*) FROM future_created_extra_properties),
  'futureCreatedExtraPropertySample', COALESCE((SELECT jsonb_agg(to_jsonb(extra) ORDER BY extra."Id") FROM (SELECT * FROM future_created_extra_properties ORDER BY "Id" LIMIT 20) extra), '[]'::jsonb)
) AS opening_control;
"""


def load_schedule(path: Path) -> list[ScheduleRun]:
    return [
        ScheduleRun(
            order=index,
            run_id=row["run_id"],
            simulated_clock=row["simulated_clock"],
            sequence=row["sequence"],
            phase=row["phase"],
            role=row["role"],
            client=row["client"],
            entry_mode=row["entry_mode"],
            workflow=row["workflow"],
            asset_refs=row.get("asset_refs", ""),
            financial_refs=row.get("financial_refs", ""),
        )
        for index, row in enumerate(read_csv(path), start=1)
    ]


def load_ledger(path: Path) -> list[LedgerRow]:
    return [
        LedgerRow(
            order=index,
            sim_date_text=row["sim_date"],
            run_id=row["run_id"],
            result=row["result"],
            client=row["client"],
            entry_mode=row["entry_mode"],
            evidence=row.get("evidence", ""),
            asset_or_reference=row.get("asset_or_reference", ""),
            system_record=row.get("system_record", ""),
            amount=row.get("amount", ""),
            allocation_or_balance=row.get("allocation_or_balance", ""),
        )
        for index, row in enumerate(read_csv(path), start=1)
        if row.get("run_id", "").strip()
    ]


def load_bug_ledger(path: Path) -> list[BugRow]:
    if not path.exists():
        return []
    return [
        BugRow(
            bug_id=row["bug_id"],
            first_seen_sim_date=row["first_seen_sim_date"],
            severity=row["severity"],
            status=row["status"],
            summary=row["summary"],
        )
        for row in read_csv(path)
        if row.get("bug_id", "").strip()
    ]


def latest_ledger_by_run_id(rows: Iterable[LedgerRow]) -> dict[str, LedgerRow]:
    latest: dict[str, LedgerRow] = {}
    for row in rows:
        latest[row.run_id] = row
    return latest


def duplicate_groups(rows: Iterable[LedgerRow]) -> dict[str, list[LedgerRow]]:
    groups: dict[str, list[LedgerRow]] = defaultdict(list)
    for row in rows:
        groups[row.run_id].append(row)
    return {run_id: grouped for run_id, grouped in groups.items() if len(grouped) > 1}


def conflicting_duplicate_groups(rows: Iterable[LedgerRow]) -> dict[str, list[LedgerRow]]:
    return {
        run_id: grouped
        for run_id, grouped in duplicate_groups(rows).items()
        if len({row.conflict_key() for row in grouped}) > 1
    }


def duplicate_identity_key(row: LedgerRow) -> tuple[str, ...]:
    return (
        row.run_id.strip(),
        row.sim_date_text.strip(),
        row.client.strip(),
        row.entry_mode.strip(),
        row.asset_or_reference.strip(),
        row.result.strip(),
        row.system_record.strip(),
        row.amount.strip(),
        row.allocation_or_balance.strip(),
        row.evidence.strip(),
    )


def analyze_duplicate_groups(rows: Iterable[LedgerRow]) -> DuplicateReport:
    historical: dict[str, list[LedgerRow]] = {}
    exact_duplicate: dict[str, list[LedgerRow]] = {}
    unordered_authoritative: dict[str, list[LedgerRow]] = {}

    for run_id, grouped in duplicate_groups(rows).items():
        identity_count = len({duplicate_identity_key(row) for row in grouped})
        max_order = max(row.order for row in grouped)
        latest_rows = [row for row in grouped if row.order == max_order]

        if identity_count < len(grouped):
            exact_duplicate[run_id] = grouped
        elif len(latest_rows) != 1:
            unordered_authoritative[run_id] = grouped
        else:
            historical[run_id] = grouped

    return DuplicateReport(
        historical=historical,
        exact_duplicate=exact_duplicate,
        unordered_authoritative=unordered_authoritative,
    )


def in_date_range(run: ScheduleRun, start: date, end: date) -> bool:
    return start <= run.sim_date <= end


def runs_due_before(schedule: Iterable[ScheduleRun], cutoff: date) -> list[ScheduleRun]:
    return [run for run in schedule if run.sim_date < cutoff]


def incomplete_runs(runs: Iterable[ScheduleRun], latest: dict[str, LedgerRow]) -> list[ScheduleRun]:
    missing_or_incomplete = []
    for run in runs:
        row = latest.get(run.run_id)
        if row is None or not row.is_complete:
            missing_or_incomplete.append(run)
    return missing_or_incomplete


def blockers_due_before(bugs: Iterable[BugRow], cutoff: date) -> list[BugRow]:
    return [bug for bug in bugs if bug.sim_date < cutoff and bug.is_blocker]


def ledger_rows_before(rows: Iterable[LedgerRow], cutoff: date) -> list[LedgerRow]:
    return [row for row in rows if row.sim_date < cutoff]


def unknown_result_counts(rows: Iterable[LedgerRow]) -> Counter[str]:
    return Counter(row.result for row in rows if is_unknown_result(row.result))


def unknown_bug_status_counts(rows: Iterable[BugRow]) -> Counter[str]:
    return Counter(row.status for row in rows if is_unknown_bug_status(row.status))


def print_unknown_counter(label: str, counts: Counter[str], limit: int) -> None:
    if not counts:
        return
    print(f"\nUnknown {label}:")
    for value, count in counts.most_common(limit):
        print(f"  {count} | {value}")
    remainder = len(counts) - limit
    if remainder > 0:
        print(f"  ... {remainder} more unknown values not shown")


def print_run_table(runs: Iterable[ScheduleRun], latest: dict[str, LedgerRow]) -> None:
    current_date: date | None = None
    for run in runs:
        if run.sim_date != current_date:
            current_date = run.sim_date
            print(f"\n{current_date.isoformat()}")
        row = latest.get(run.run_id)
        status = row.result if row else "NOT IN LEDGER"
        print(
            f"  {run.run_id} seq={run.sequence} [{status}] "
            f"{run.phase} | {run.client} | {run.workflow}"
        )


def summarize_duplicates(ledger_rows: list[LedgerRow]) -> int:
    duplicates = duplicate_groups(ledger_rows)
    report = analyze_duplicate_groups(ledger_rows)
    print(f"Duplicate run_id groups: {len(duplicates)}")
    print(f"Historical duplicate groups: {len(report.historical)}")
    print(f"Blocking duplicate groups: {len(report.blocking)}")
    print(f"Exact duplicate/idempotency groups: {len(report.exact_duplicate)}")
    print(f"Unordered authoritative groups: {len(report.unordered_authoritative)}")

    for run_id in sorted(report.blocking):
        rows = report.blocking[run_id]
        print(f"\nBLOCKING {run_id}")
        for row in rows:
            print(f"  ledger_row={row.order} date={row.sim_date_text} result={row.result}")

    for run_id in sorted(report.historical):
        rows = report.historical[run_id]
        latest = max(rows, key=lambda row: row.order)
        history_orders = ", ".join(str(row.order) for row in rows if row.order != latest.order)
        print(
            f"\nHISTORY {run_id}: latest ledger_row={latest.order} "
            f"date={latest.sim_date_text} result={latest.result}"
        )
        print(f"  earlier ledger_rows={history_orders}")
    return 1 if report.blocking else 0


def command_remaining(args: argparse.Namespace) -> int:
    schedule = load_schedule(args.schedule)
    ledger_rows = load_ledger(args.ledger)
    latest = latest_ledger_by_run_id(ledger_rows)
    start = parse_date(args.start or args.date)
    end = parse_date(args.end or args.date)
    selected = [run for run in schedule if in_date_range(run, start, end)]
    remaining = incomplete_runs(selected, latest)

    print(
        f"Schedule rows in range: {len(selected)}; remaining by latest run_id ledger row: "
        f"{len(remaining)}"
    )
    print_run_table(remaining if args.only_remaining else selected, latest)
    duplicate_report = analyze_duplicate_groups(ledger_rows)
    if duplicate_report.blocking:
        print(f"\nWarning: {len(duplicate_report.blocking)} blocking duplicate run_id groups exist.")
    elif duplicate_report.historical:
        print(
            f"\nNote: {len(duplicate_report.historical)} historical duplicate run_id groups exist; "
            "latest ledger rows are authoritative."
        )
    return 0


def command_gate(args: argparse.Namespace) -> int:
    schedule = load_schedule(args.schedule)
    ledger_rows = load_ledger(args.ledger)
    latest = latest_ledger_by_run_id(ledger_rows)
    advance_to = parse_date(args.advance_to)
    due = runs_due_before(schedule, advance_to)
    incomplete = incomplete_runs(due, latest)
    duplicate_report = analyze_duplicate_groups(ledger_rows_before(ledger_rows, advance_to))
    blocking_duplicates = duplicate_report.blocking
    earlier_ledger_rows = ledger_rows_before(ledger_rows, advance_to)
    bug_rows = [] if args.ignore_bug_ledger else load_bug_ledger(args.bug_ledger)
    earlier_bug_rows = [bug for bug in bug_rows if bug.sim_date < advance_to]
    bug_blockers = [] if args.ignore_bug_ledger else blockers_due_before(bug_rows, advance_to)
    unknown_results = unknown_result_counts(earlier_ledger_rows)
    unknown_statuses = unknown_bug_status_counts(earlier_bug_rows)

    print(f"Chronology gate before {advance_to.isoformat()}")
    print(f"Earlier scheduled runs: {len(due)}")
    print(f"Incomplete earlier runs: {len(incomplete)}")
    print(f"Historical duplicate run_id groups: {len(duplicate_report.historical)}")
    print(f"Blocking duplicate run_id groups: {len(blocking_duplicates)}")
    print(f"Earlier bug-ledger blockers: {len(bug_blockers)}")
    print(f"Unknown earlier ledger result values: {len(unknown_results)}")
    print(f"Unknown earlier bug status values: {len(unknown_statuses)}")

    if incomplete:
        print_run_table(incomplete[: args.limit], latest)
        if len(incomplete) > args.limit:
            print(f"\n... {len(incomplete) - args.limit} more incomplete runs not shown")

    if blocking_duplicates:
        print("\nBlocking duplicate run_ids:")
        for run_id in sorted(blocking_duplicates)[: args.limit]:
            rows = blocking_duplicates[run_id]
            print(f"  {run_id}: ledger rows {', '.join(str(row.order) for row in rows)}")

    if bug_blockers:
        print("\nBug-ledger blockers:")
        for bug in bug_blockers[: args.limit]:
            print(f"  {bug.bug_id} {bug.first_seen_sim_date} [{bug.status}] {bug.summary}")
        if len(bug_blockers) > args.limit:
            print(f"  ... {len(bug_blockers) - args.limit} more blockers not shown")

    print_unknown_counter("earlier ledger result values", unknown_results, args.limit)
    print_unknown_counter("earlier bug status values", unknown_statuses, args.limit)

    return 1 if incomplete or blocking_duplicates or bug_blockers else 0


def command_duplicates(args: argparse.Namespace) -> int:
    return summarize_duplicates(load_ledger(args.ledger))


def endpoint_status(url: str, timeout: float) -> tuple[bool, str]:
    try:
        request = urllib.request.Request(url, method="GET")
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read(4000).decode("utf-8", errors="replace")
            return True, f"HTTP {response.status} {body[:200].strip()}"
    except (OSError, urllib.error.URLError) as exc:
        return False, str(exc)


def run_command(command: list[str], timeout: float) -> tuple[bool, str]:
    try:
        completed = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            timeout=timeout,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return False, str(exc)
    output = "\n".join(part.strip() for part in (completed.stdout, completed.stderr) if part.strip())
    return completed.returncode == 0, output[:1000]


def corpus_status(manifest_path: Path, index_path: Path) -> tuple[bool, str]:
    manifest = read_csv(manifest_path) if manifest_path.exists() else []
    index = read_csv(index_path) if index_path.exists() else []
    manifest_assets = {row.get("asset_id", "").strip() for row in manifest if row.get("asset_id", "").strip()}
    index_assets = {row.get("asset_id", "").strip() for row in index if row.get("asset_id", "").strip()}
    missing = sorted(manifest_assets - index_assets)
    extra = sorted(index_assets - manifest_assets)
    ok_rows = sum(1 for row in index if normalize(row.get("status")) == "ok")
    ok = bool(manifest_assets) and manifest_assets == index_assets and ok_rows == len(index)
    detail = (
        f"manifest={len(manifest_assets)} corpus_index={len(index_assets)} ok_rows={ok_rows} "
        f"missing={len(missing)} extra={len(extra)}"
    )
    if missing[:5]:
        detail += f" missing_sample={','.join(missing[:5])}"
    if extra[:5]:
        detail += f" extra_sample={','.join(extra[:5])}"
    return ok, detail


def access_status(path: Path, mode: int) -> tuple[bool, str]:
    exists = path.exists()
    ok = exists and os.access(path, mode)
    return ok, f"{path} exists={exists} access={ok}"


def print_check(name: str, ok: bool, detail: str) -> None:
    status = "OK" if ok else "FAIL"
    print(f"{status} {name}: {detail}")


def command_doctor(args: argparse.Namespace) -> int:
    checks: list[tuple[str, bool, str]] = []
    for name, url in (
        ("api_health", args.api_health_url),
        ("web", args.web_url),
        ("sim_clock", args.clock_url),
    ):
        ok, detail = endpoint_status(url, args.timeout)
        checks.append((name, ok, detail))

    ok, detail = corpus_status(args.scan_manifest, args.corpus_index)
    checks.append(("scan_corpus", ok, detail))

    ok, detail = access_status(args.ledger, os.R_OK | os.W_OK)
    checks.append(("ledger_access", ok, detail))

    ok, detail = access_status(args.evidence_dir, os.R_OK | os.W_OK | os.X_OK)
    checks.append(("evidence_dir_access", ok, detail))

    adb_ok, adb_detail = run_command(["adb", "devices"], args.timeout)
    checks.append(("adb_devices", adb_ok and "\tdevice" in adb_detail, adb_detail or "no device output"))

    reverse_ok, reverse_detail = run_command(["adb", "reverse", "--list"], args.timeout)
    reverse_has_api = "tcp:5666" in reverse_detail
    checks.append(("adb_reverse_5666", reverse_ok and reverse_has_api, reverse_detail or "no reverse output"))

    for name, ok, detail in checks:
        print_check(name, ok, detail)
    return 0 if all(ok for _, ok, _ in checks) else 1


def command_summary(args: argparse.Namespace) -> int:
    schedule = load_schedule(args.schedule)
    ledger_rows = load_ledger(args.ledger)
    latest = latest_ledger_by_run_id(ledger_rows)
    bug_rows = load_bug_ledger(args.bug_ledger)
    completed = [run for run in schedule if latest.get(run.run_id) and latest[run.run_id].is_complete]
    remaining = incomplete_runs(schedule, latest)
    duplicate_report = analyze_duplicate_groups(ledger_rows)
    unknown_results = unknown_result_counts(ledger_rows)
    unknown_statuses = unknown_bug_status_counts(bug_rows)
    summary = {
        "schedule_rows": len(schedule),
        "ledger_rows": len(ledger_rows),
        "unique_ledger_run_ids": len(latest),
        "completed_schedule_runs": len(completed),
        "remaining_schedule_runs": len(remaining),
        "duplicate_run_id_groups": len(duplicate_groups(ledger_rows)),
        "historical_duplicate_groups": len(duplicate_report.historical),
        "blocking_duplicate_groups": len(duplicate_report.blocking),
        "exact_duplicate_idempotency_groups": len(duplicate_report.exact_duplicate),
        "unordered_authoritative_duplicate_groups": len(duplicate_report.unordered_authoritative),
        "unknown_ledger_result_values": dict(sorted(unknown_results.items())),
        "unknown_bug_status_values": dict(sorted(unknown_statuses.items())),
        "first_remaining_run_id": remaining[0].run_id if remaining else None,
        "first_remaining_date": remaining[0].simulated_clock if remaining else None,
    }
    print(json.dumps(summary, indent=2, sort_keys=True))
    return 0


def command_opening_control_sql(args: argparse.Namespace) -> int:
    sql = build_opening_control_sql(
        portfolio_id=args.portfolio_id,
        as_of=args.as_of,
        portfolio_rows=read_csv(args.portfolio_plan),
        unit_rows=read_csv(args.unit_plan),
        lease_rows=read_csv(args.lease_plan),
        scan_rows=read_csv(args.scan_manifest),
    )
    print(sql.rstrip())
    return 0


def add_common_paths(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--schedule", type=Path, default=DEFAULT_SCHEDULE)
    parser.add_argument("--ledger", type=Path, default=DEFAULT_LEDGER)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Read-only TSK-754 checkpoint helper. It never mutates DB, clock, schedule, or ledgers."
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    summary = subparsers.add_parser("summary", help="Print schedule/ledger progress counts as JSON.")
    add_common_paths(summary)
    summary.add_argument("--bug-ledger", type=Path, default=DEFAULT_BUG_LEDGER)
    summary.set_defaults(func=command_summary)

    remaining = subparsers.add_parser("remaining", help="Show scheduled runs and remaining work for a date range.")
    add_common_paths(remaining)
    date_group = remaining.add_mutually_exclusive_group(required=True)
    date_group.add_argument("--date", help="Single simulated date, YYYY-MM-DD.")
    date_group.add_argument("--start", help="Range start, YYYY-MM-DD.")
    remaining.add_argument("--end", help="Range end, YYYY-MM-DD. Required with --start.")
    remaining.add_argument("--only-remaining", action="store_true", help="Hide already completed rows.")
    remaining.set_defaults(func=command_remaining)

    gate = subparsers.add_parser("gate", help="Fail if advancing would skip earlier incomplete work or blockers.")
    add_common_paths(gate)
    gate.add_argument("--bug-ledger", type=Path, default=DEFAULT_BUG_LEDGER)
    gate.add_argument("--advance-to", required=True, help="Target simulated date, YYYY-MM-DD.")
    gate.add_argument("--ignore-bug-ledger", action="store_true")
    gate.add_argument("--limit", type=int, default=25)
    gate.set_defaults(func=command_gate)

    duplicates = subparsers.add_parser("duplicates", help="Report duplicate/conflicting run_id rows.")
    add_common_paths(duplicates)
    duplicates.set_defaults(func=command_duplicates)

    doctor = subparsers.add_parser("doctor", help="Read-only local service, phone, corpus, and path checks.")
    add_common_paths(doctor)
    doctor.add_argument("--api-health-url", default="https://localhost:5666/health")
    doctor.add_argument("--web-url", default="https://localhost:5667/")
    doctor.add_argument("--clock-url", default="https://localhost:5666/api/v1/dev/clock")
    doctor.add_argument("--timeout", type=float, default=3.0)
    doctor.add_argument("--scan-manifest", type=Path, default=DEFAULT_SCAN_MANIFEST)
    doctor.add_argument("--corpus-index", type=Path, default=DEFAULT_CORPUS_INDEX)
    doctor.add_argument("--evidence-dir", type=Path, default=DEFAULT_EVIDENCE_DIR)
    doctor.set_defaults(func=command_doctor)

    opening_control = subparsers.add_parser(
        "opening-control-sql",
        help="Emit one DB-side SQL statement for the Jan 15 plan-isolated opening checkpoint.",
    )
    opening_control.add_argument("--portfolio-id", type=int, required=True)
    opening_control.add_argument("--as-of", default="2027-01-15")
    opening_control.add_argument("--portfolio-plan", type=Path, default=DEFAULT_PORTFOLIO_PLAN)
    opening_control.add_argument("--unit-plan", type=Path, default=DEFAULT_UNIT_PLAN)
    opening_control.add_argument("--lease-plan", type=Path, default=DEFAULT_LEASE_PLAN)
    opening_control.add_argument("--scan-manifest", type=Path, default=DEFAULT_SCAN_MANIFEST)
    opening_control.set_defaults(func=command_opening_control_sql)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if getattr(args, "start", None) and not getattr(args, "end", None):
        parser.error("--end is required with --start")
    if getattr(args, "end", None) and not getattr(args, "start", None):
        parser.error("--start is required with --end")
    if getattr(args, "start", None) and getattr(args, "end", None):
        if parse_date(args.start) > parse_date(args.end):
            parser.error("--start must be on or before --end")
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
