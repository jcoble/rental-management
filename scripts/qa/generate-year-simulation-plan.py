#!/usr/bin/env python3
"""Generate the TSK-749 year-long Rental Command operating simulation.

This generator intentionally produces a readable HTML runbook and machine-readable
control files. The companion render-year-simulation-scan-corpus.py command turns
scan-assets.csv into the uploadable PDF/JPEG corpus and validation indexes.
"""

from __future__ import annotations

import calendar
import csv
import html
import json
import re
from collections import defaultdict
from dataclasses import asdict, dataclass
from datetime import date, datetime, timedelta
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path
from typing import Iterable


ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "Docs" / "Testing" / "YearSimulation2027"
YEAR = 2027
START = date(YEAR, 1, 1)
END = date(YEAR, 12, 31)


FIRST_NAMES = [
    "Avery", "Blake", "Casey", "Dana", "Elliot", "Finley", "Gray", "Harper",
    "Indigo", "Jordan", "Kai", "Logan", "Morgan", "Noel", "Oakley", "Parker",
    "Quinn", "Riley", "Sage", "Taylor", "Uma", "Val", "Winter", "Xavier",
    "Yara", "Zion", "Amara", "Bennett", "Carmen", "Darius", "Elena", "Felix",
    "Gia", "Hector", "Iris", "Jonah", "Keira", "Luca", "Maya", "Nolan",
    "Opal", "Paolo", "Rina", "Silas", "Talia", "Uri", "Vera", "Wes",
]
LAST_NAMES = [
    "Brooks", "Chen", "Diaz", "Ellis", "Foster", "Garcia", "Hayes", "Ibrahim",
    "Johnson", "Kim", "Lewis", "Miller", "Nguyen", "Ortiz", "Patel", "Reed",
    "Singh", "Thomas", "Vega", "Williams", "Adams", "Baker", "Clark", "Davis",
    "Evans", "Flores", "Green", "Hill", "Irwin", "Jones", "King", "Lopez",
    "Moore", "Nelson", "Owens", "Price", "Quincy", "Ross", "Scott", "Turner",
]
STREETS = [
    "Arbor", "Briar", "Cedar", "Dover", "Elm", "Franklin", "Grove", "Hawthorne",
    "Ivy", "Juniper", "Kingston", "Lakeview", "Maple", "North", "Oak", "Park",
    "Quarry", "River", "Summit", "Terrace", "Union", "Valley", "Walnut", "York",
    "Zenith", "Ash", "Birch", "Chestnut", "Dogwood", "Evergreen",
]
PROPERTY_NAMES = [
    "Arbor House", "Briar Cottage", "Cedar Bend", "Dover House", "Elm Haven",
    "Franklin Place", "Grove House", "Hawthorne Home", "Ivy House", "Juniper Place",
    "Kingston House", "Lakeview Home", "Maple House", "North Street Home", "Oak House",
    "Parkside Home", "Quarry House", "River House", "Summit Home", "Terrace House",
    "Union Duplex", "Valley Duplex", "Walnut Duplex", "York Duplex", "Zenith Duplex",
    "Ash Duplex", "Birch Duplex", "Chestnut Duplex", "Dogwood Duplex", "Evergreen Duplex",
]
OWNER_NAMES = [
    "Blue Door Residential LLC",
    "Coble Family Rentals LLC",
    "Northstar Property Partners LLC",
    "Riverbend Homes LLC",
    "Juniper Street Holdings LLC",
]
VENDORS = [
    ("V001", "Apex Plumbing", "Plumbing"),
    ("V002", "ComfortZone HVAC", "HVAC"),
    ("V003", "Metro Electric", "Electrical"),
    ("V004", "Green Thumb Landscaping", "Landscaping"),
    ("V005", "Summit Roofing", "Roofing"),
    ("V006", "Prime Pest Control", "Pest control"),
    ("V007", "Fresh Start Turnovers", "Cleaning"),
    ("V008", "Hardware House", "Supplies"),
    ("V009", "SafeHome Locksmith", "Locks"),
    ("V010", "ClearFlow Restoration", "Water mitigation"),
    ("V011", "Buckeye Legal Services", "Legal"),
    ("V012", "Precision Property Inspections", "Inspection"),
]


def cents(value: Decimal | int | str | float) -> int:
    return int((Decimal(str(value)) * 100).quantize(Decimal("1"), rounding=ROUND_HALF_UP))


def dollars(value: int) -> str:
    sign = "-" if value < 0 else ""
    value = abs(value)
    return f"{sign}${value // 100:,.0f}.{value % 100:02d}"


def iso(value: date | None) -> str:
    return value.isoformat() if value else ""


def add_months_day_one(value: date, months: int) -> date:
    serial = value.year * 12 + value.month - 1 + months
    return date(serial // 12, serial % 12 + 1, 1)


def month_end(value: date) -> date:
    return date(value.year, value.month, calendar.monthrange(value.year, value.month)[1])


def next_business_day(value: date) -> date:
    while value.weekday() >= 5:
        value += timedelta(days=1)
    return value


def safe_day(year: int, month: int, day: int) -> date:
    return date(year, month, min(day, calendar.monthrange(year, month)[1]))


def csv_write(path: Path, rows: list[dict], fieldnames: list[str] | None = None) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not rows:
        raise ValueError(f"Refusing to write empty CSV: {path}")
    names = fieldnames or list(rows[0].keys())
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=names, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


@dataclass
class Property:
    property_id: str
    property_name: str
    rental_structure: str
    address: str
    city: str
    state: str
    postal_code: str
    owner_entity_id: str
    owner_entity_name: str
    ownership_pct: str
    acquisition_date: str
    original_cost_cents: int
    land_value_cents: int
    building_basis_cents: int
    loan_id: str
    opening_loan_balance_cents: int
    monthly_principal_cents: int
    monthly_interest_cents: int


@dataclass
class Unit:
    unit_id: str
    property_id: str
    label: str
    bedrooms: int
    bathrooms: str
    square_feet: int
    market_rent_cents: int
    opening_state: str


@dataclass
class Lease:
    lease_id: str
    unit_id: str
    tenant_id: str
    tenant_name: str
    tenant_email: str
    start_date: date
    end_date: date
    monthly_rent_cents: int
    deposit_cents: int
    origin: str
    lifecycle_scenario: str


@dataclass
class Asset:
    asset_id: str
    planned_date: str
    filename: str
    document_family: str
    intended_target: str
    intake_expectation: str
    format: str
    pages_or_images: str
    capture_profile: str
    property_id: str
    unit_id: str
    lease_id: str
    financial_event_id: str
    expected_fields: str
    edge_case: str
    paired_manual_action: str
    confidentiality: str = "Synthetic QA only; no real people, accounts, signatures, or properties"


@dataclass
class Schedule:
    run_id: str
    simulated_clock: str
    sequence: int
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


@dataclass
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
    ar_delta_cents: int
    operating_cash_delta_cents: int
    deposit_cash_delta_cents: int
    reserve_cash_delta_cents: int
    deposit_liability_delta_cents: int
    loan_liability_delta_cents: int
    income_cents: int
    expense_cents: int
    owner_equity_delta_cents: int
    owner_distribution_cents: int
    capital_asset_delta_cents: int
    accumulated_depreciation_delta_cents: int
    memo: str


properties: list[Property] = []
units: list[Unit] = []
leases: list[Lease] = []
assets: list[Asset] = []
schedules: list[Schedule] = []
financial_events: list[FinancialEvent] = []
journal: list[dict] = []
coverage: list[dict] = []
screen_inventory: list[dict] = []
field_inventory: list[dict] = []
role_journeys: list[dict] = []
notification_scenarios: list[dict] = []
crud_lifecycles: list[dict] = []
event_sequence_by_date: defaultdict[str, int] = defaultdict(int)


def build_portfolio() -> None:
    unit_counter = 0
    for idx in range(1, 31):
        pid = f"P{idx:03d}"
        structure = "SingleRental" if idx <= 20 else "MultiRental"
        owner_idx = (idx - 1) % len(OWNER_NAMES)
        cost = cents(172_500 + idx * 7_250 + (45_000 if structure == "MultiRental" else 0))
        land = int((Decimal(cost) * Decimal("0.20")).quantize(Decimal("1"), rounding=ROUND_HALF_UP))
        building = cost - land
        financed = idx % 3 != 0
        loan = int((Decimal(cost) * Decimal("0.70")).quantize(Decimal("1"), rounding=ROUND_HALF_UP)) if financed else 0
        properties.append(
            Property(
                property_id=pid,
                property_name=PROPERTY_NAMES[idx - 1],
                rental_structure=structure,
                address=f"{100 + idx * 17} {STREETS[idx - 1]} Street",
                city="Columbus",
                state="OH",
                postal_code=f"43{200 + idx:03d}",
                owner_entity_id=f"O{owner_idx + 1:02d}",
                owner_entity_name=OWNER_NAMES[owner_idx],
                ownership_pct="100.00",
                acquisition_date=f"{2016 + (idx % 10)}-{1 + (idx % 12):02d}-15",
                original_cost_cents=cost,
                land_value_cents=land,
                building_basis_cents=building,
                loan_id=f"LN{idx:03d}" if financed else "",
                opening_loan_balance_cents=loan,
                monthly_principal_cents=cents(420 + idx * 11) if financed else 0,
                monthly_interest_cents=cents(610 + idx * 13) if financed else 0,
            )
        )
        labels = ["Main"] if structure == "SingleRental" else ["A", "B"]
        for label in labels:
            unit_counter += 1
            rent = cents(1050 + ((unit_counter * 7) % 12) * 75 + (100 if structure == "MultiRental" else 0))
            units.append(
                Unit(
                    unit_id=f"U{unit_counter:03d}",
                    property_id=pid,
                    label=label,
                    bedrooms=2 + (unit_counter % 3),
                    bathrooms="1.0" if unit_counter % 4 else "2.0",
                    square_feet=850 + (unit_counter % 8) * 115,
                    market_rent_cents=rent,
                    opening_state="Occupied" if unit_counter <= 36 else "Vacant",
                )
            )


def build_leases() -> None:
    override_ends = {
        "U003": date(2027, 10, 31),
        "U005": date(2027, 2, 28),
        "U008": date(2027, 8, 31),
        "U014": date(2027, 5, 31),
        "U017": date(2027, 6, 20),
        "U022": date(2027, 8, 31),
        "U034": date(2027, 11, 30),
    }
    scenarios = {
        "U003": "owner sale; clean move-out; no replacement",
        "U005": "ordinary non-renewal; clean move-out",
        "U008": "tenant-requested unit transfer to U040",
        "U014": "non-renewal; turnover damage withholding",
        "U017": "delinquency, notice, eviction, collections",
        "U022": "renewal declined; month-long vacancy",
        "U034": "winter move-out; mid-month replacement",
    }
    for idx, unit in enumerate(units[:36], start=1):
        start_month = 1 + (idx % 6)
        start = date(2026, start_month, 1)
        end = override_ends.get(unit.unit_id, date(2027, 12, 31))
        tenant_name = f"{FIRST_NAMES[(idx * 3) % len(FIRST_NAMES)]} {LAST_NAMES[(idx * 5) % len(LAST_NAMES)]}"
        leases.append(
            Lease(
                lease_id=f"L{idx:03d}",
                unit_id=unit.unit_id,
                tenant_id=f"T{idx:03d}",
                tenant_name=tenant_name,
                tenant_email=f"tenant.{idx:03d}@example.local",
                start_date=start,
                end_date=end,
                monthly_rent_cents=unit.market_rent_cents,
                deposit_cents=unit.market_rent_cents,
                origin="Opening lease scan",
                lifecycle_scenario=scenarios.get(unit.unit_id, "continuing resident through year-end"),
            )
        )

    additions = [
        ("L037", "U005", "T037", date(2027, 4, 1), date(2028, 3, 31), "post-turnover replacement"),
        ("L038", "U014", "T038", date(2027, 7, 1), date(2028, 6, 30), "post-turnover replacement"),
        ("L039", "U017", "T039", date(2027, 8, 1), date(2028, 7, 31), "post-eviction replacement"),
        ("L040", "U022", "T040", date(2027, 10, 1), date(2028, 9, 30), "post-vacancy replacement"),
        ("L041", "U040", "T008", date(2027, 9, 1), date(2028, 8, 31), "unit transfer destination"),
        ("L042", "U034", "T042", date(2027, 12, 15), date(2028, 12, 14), "mid-month winter replacement"),
        ("L043", "U037", "T043", date(2027, 2, 1), date(2028, 1, 31), "opening vacancy leased"),
        ("L044", "U038", "T044", date(2027, 3, 15), date(2028, 3, 14), "opening vacancy leased mid-month"),
        ("L045", "U039", "T045", date(2027, 6, 1), date(2028, 5, 31), "opening vacancy leased"),
    ]
    for lease_id, unit_id, tenant_id, start, end, scenario in additions:
        idx = int(tenant_id[1:])
        unit = unit_by_id(unit_id)
        tenant_name = f"{FIRST_NAMES[(idx * 3) % len(FIRST_NAMES)]} {LAST_NAMES[(idx * 5) % len(LAST_NAMES)]}"
        leases.append(
            Lease(
                lease_id=lease_id,
                unit_id=unit_id,
                tenant_id=tenant_id,
                tenant_name=tenant_name,
                tenant_email=f"tenant.{idx:03d}@example.local",
                start_date=start,
                end_date=end,
                monthly_rent_cents=unit.market_rent_cents,
                deposit_cents=unit.market_rent_cents,
                origin="Application + lease scan",
                lifecycle_scenario=scenario,
            )
        )


def unit_by_id(unit_id: str) -> Unit:
    return next(item for item in units if item.unit_id == unit_id)


def property_by_unit(unit_id: str) -> Property:
    unit = unit_by_id(unit_id)
    return next(item for item in properties if item.property_id == unit.property_id)


def lease_by_id(lease_id: str) -> Lease:
    return next(item for item in leases if item.lease_id == lease_id)


def add_asset(
    planned_date: date,
    family: str,
    target: str,
    expectation: str,
    fmt: str,
    pages: str,
    profile: str,
    expected_fields: str,
    edge_case: str = "",
    paired_manual_action: str = "",
    property_id: str = "",
    unit_id: str = "",
    lease_id: str = "",
    financial_event_id: str = "",
    slug: str = "",
) -> str:
    asset_id = f"SCN-{len(assets) + 1:04d}"
    suffix = "pdf" if fmt == "PDF" else ("jpg" if "JPEG" in fmt else "png")
    safe_slug = (slug or family).lower().replace(" ", "-").replace("/", "-")
    assets.append(
        Asset(
            asset_id=asset_id,
            planned_date=iso(planned_date),
            filename=f"{asset_id.lower()}-{safe_slug}.{suffix}",
            document_family=family,
            intended_target=target,
            intake_expectation=expectation,
            format=fmt,
            pages_or_images=pages,
            capture_profile=profile,
            property_id=property_id,
            unit_id=unit_id,
            lease_id=lease_id,
            financial_event_id=financial_event_id,
            expected_fields=expected_fields,
            edge_case=edge_case,
            paired_manual_action=paired_manual_action,
        )
    )
    return asset_id


def add_schedule(
    clock: date,
    phase: str,
    role: str,
    client: str,
    entry_mode: str,
    workflow: str,
    entity_refs: str,
    asset_refs: Iterable[str] | str,
    financial_refs: Iterable[str] | str,
    exact_actions: str,
    expected_result: str,
    evidence_required: str,
    negative_or_retry_check: str = "",
    completion_gate: str = "All expected records and amounts match; no unexplained error or stale state.",
) -> str:
    key = iso(clock)
    event_sequence_by_date[key] += 1
    seq = event_sequence_by_date[key]
    run_id = f"RUN-{clock.strftime('%Y%m%d')}-{seq:02d}"
    schedules.append(
        Schedule(
            run_id=run_id,
            simulated_clock=key,
            sequence=seq,
            phase=phase,
            role=role,
            client=client,
            entry_mode=entry_mode,
            workflow=workflow,
            entity_refs=entity_refs,
            asset_refs="; ".join(asset_refs) if not isinstance(asset_refs, str) else asset_refs,
            financial_refs="; ".join(financial_refs) if not isinstance(financial_refs, str) else financial_refs,
            exact_actions=exact_actions,
            expected_result=expected_result,
            evidence_required=evidence_required,
            negative_or_retry_check=negative_or_retry_check,
            completion_gate=completion_gate,
        )
    )
    return run_id


def add_financial_event(
    entry_date: date,
    effective_date: date,
    event_type: str,
    category: str,
    amount_cents: int,
    lines: list[tuple[str, str, int]],
    property_id: str = "",
    unit_id: str = "",
    lease_id: str = "",
    counterparty: str = "",
    source_mode: str = "System",
    source_asset_id: str = "",
    reference: str = "",
    deltas: dict[str, int] | None = None,
    memo: str = "",
) -> str:
    event_id = f"FIN-{len(financial_events) + 1:05d}"
    delta = defaultdict(int)
    delta.update(deltas or {})
    financial_events.append(
        FinancialEvent(
            event_id=event_id,
            entry_date=iso(entry_date),
            effective_date=iso(effective_date),
            event_type=event_type,
            category=category,
            property_id=property_id,
            unit_id=unit_id,
            lease_id=lease_id,
            counterparty=counterparty,
            source_mode=source_mode,
            source_asset_id=source_asset_id,
            reference=reference,
            amount_cents=amount_cents,
            ar_delta_cents=delta["ar"],
            operating_cash_delta_cents=delta["operating_cash"],
            deposit_cash_delta_cents=delta["deposit_cash"],
            reserve_cash_delta_cents=delta["reserve_cash"],
            deposit_liability_delta_cents=delta["deposit_liability"],
            loan_liability_delta_cents=delta["loan_liability"],
            income_cents=delta["income"],
            expense_cents=delta["expense"],
            owner_equity_delta_cents=delta["owner_equity"],
            owner_distribution_cents=delta["owner_distribution"],
            capital_asset_delta_cents=delta["capital_asset"],
            accumulated_depreciation_delta_cents=delta["accumulated_depreciation"],
            memo=memo,
        )
    )
    debit_total = sum(amount for side, _, amount in lines if side == "Debit")
    credit_total = sum(amount for side, _, amount in lines if side == "Credit")
    if debit_total != credit_total:
        raise ValueError(f"Unbalanced event {event_id}: {debit_total} != {credit_total}")
    for line_no, (side, account, line_amount) in enumerate(lines, start=1):
        journal.append(
            {
                "event_id": event_id,
                "line_no": line_no,
                "entry_date": iso(entry_date),
                "effective_date": iso(effective_date),
                "property_id": property_id,
                "unit_id": unit_id,
                "lease_id": lease_id,
                "account": account,
                "debit_cents": line_amount if side == "Debit" else 0,
                "credit_cents": line_amount if side == "Credit" else 0,
                "memo": memo or f"{event_type}: {reference}",
            }
        )
    return event_id


def build_opening_setup() -> None:
    # Opening balance sheet and opening tenant deposit control.
    operating = cents(150_000)
    reserve = cents(50_000)
    total_deposits = sum(lease.deposit_cents for lease in leases if lease.start_date < START <= lease.end_date)
    total_property_basis = sum(p.original_cost_cents for p in properties)
    total_loans = sum(p.opening_loan_balance_cents for p in properties)
    total_assets = operating + reserve + total_deposits + total_property_basis
    owner_equity = total_assets - total_deposits - total_loans
    opening_lines = [
        ("Debit", "1000 Operating Cash", operating),
        ("Debit", "1010 Security Deposit Trust Cash", total_deposits),
        ("Debit", "1020 Reserve Cash", reserve),
        ("Debit", "1500 Rental Property Basis", total_property_basis),
        ("Credit", "2000 Security Deposit Liability", total_deposits),
        ("Credit", "2200 Mortgage Loans Payable", total_loans),
        ("Credit", "3000 Owner Equity", owner_equity),
    ]
    opening_id = add_financial_event(
        START,
        START,
        "OpeningBalance",
        "Opening balances",
        total_assets,
        opening_lines,
        counterparty="Blue Door Property Management",
        source_mode="Manual setup + scanned source documents",
        reference="OPEN-2027",
        deltas={
            "operating_cash": operating,
            "deposit_cash": total_deposits,
            "reserve_cash": reserve,
            "deposit_liability": total_deposits,
            "loan_liability": total_loans,
            "owner_equity": owner_equity,
            "capital_asset": total_property_basis,
        },
        memo="Opening control balances for the independent financial oracle",
    )

    lease_assets = []
    for lease in leases[:36]:
        prop = property_by_unit(lease.unit_id)
        asset = add_asset(
            date(2027, 1, 2) + timedelta(days=(int(lease.lease_id[1:]) - 1) // 12),
            "Residential lease agreement",
            "Lease",
            "Direct scan draft; confirms property, unit, tenant, LeaseManagement, agreement, and account",
            "PDF" if int(lease.lease_id[1:]) % 2 else "Camera JPEG",
            "9 pages" if int(lease.lease_id[1:]) % 2 else "9 page photos composited into one long JPEG",
            ["clean exported PDF", "phone camera slight skew", "low contrast photocopy"][int(lease.lease_id[1:]) % 3],
            "landlord; tenant; premises; property name; unit; start/end; monthly rent; deposit; due day; signatures",
            "one co-tenant on every sixth lease; due day 31 on L031; handwritten initials on every fifth lease",
            "Re-enter a different opening lease through the equivalent manual lease form on the other client.",
            prop.property_id,
            lease.unit_id,
            lease.lease_id,
            slug=f"opening-lease-{lease.lease_id}",
        )
        lease_assets.append(asset)

    for idx, prop in enumerate(properties, start=1):
        deed_asset = add_asset(
            date(2027, 1, 8) + timedelta(days=(idx - 1) // 10),
            "Deed / settlement statement",
            "Property document",
            "Contextual attachment; probe scan suggestion for Property and log a gap if no typed draft is offered",
            "PDF",
            "4 pages",
            "clean PDF with county stamp and legal-description noise",
            "owner; street address; parcel reference; acquisition date; purchase price",
            "legal description should not overwrite mailing address",
            "Create or edit property acquisition and ownership fields manually on web/mobile.",
            prop.property_id,
            slug=f"deed-{prop.property_id}",
        )
        loan_asset = ""
        if prop.loan_id:
            loan_asset = add_asset(
                date(2027, 1, 12) + timedelta(days=(idx - 1) // 8),
                "Mortgage statement",
                "Loan",
                "Typed scan candidate; if unsupported attach to Property and enter loan manually",
                "PDF" if idx % 2 else "Camera JPEG",
                "3 pages",
                "lender statement with detachable payment coupon",
                "lender; original balance; current balance; rate; term; due date; escrow; principal; interest",
                "account number is synthetic and must remain masked",
                "Enter the loan once on web and a different loan once on mobile; compare amortization.",
                prop.property_id,
                slug=f"mortgage-{prop.property_id}",
            )
        add_schedule(
            date(2027, 1, 8) + timedelta(days=(idx - 1) // 10),
            "Opening setup",
            "Workspace Administrator",
            "Web" if idx % 2 else "Mobile",
            "Scan attachment + manual confirmation",
            "Property ownership, basis, and loan setup",
            f"{prop.property_id}; {prop.loan_id}",
            [x for x in [deed_asset, loan_asset] if x],
            opening_id,
            "Open the property created by lease intake; attach the deed, verify owner/basis fields, then create or verify the loan and its current balance. Use the other client for the paired property/loan edit.",
            "One property and its canonical unit remain linked; owner, cost basis, land/building split, and loan terms match the oracle without duplicate records.",
            "Before/after screenshots; document links; property and loan detail; audit row; generated amortization sample.",
            "Try the same masked mortgage statement twice and prove idempotent attachment/draft behavior.",
        )

    # Four opening vacancies require deliberate manual property/unit setup.
    vacancy_units = units[36:]
    for idx, unit in enumerate(vacancy_units, start=1):
        prop = property_by_unit(unit.unit_id)
        add_schedule(
            date(2027, 1, 5) + timedelta(days=idx - 1),
            "Opening setup",
            "Property Manager",
            "Web" if idx % 2 else "Mobile",
            "Manual",
            "Create the initially vacant unit and verify property structure",
            f"{prop.property_id}; {unit.unit_id}",
            "",
            "",
            "Create the missing duplex Property/Unit records with address, structure, unit label, beds, baths, square feet, market rent, and Vacant state. Immediately edit one field from the other client.",
            "P029/P030 exist as MultiRental properties with two separately rentable vacant units and no fake current lease.",
            "List + detail screenshots on both clients; server-paged list result; audit row.",
            "Attempt a duplicate unit label and verify the whole command rolls back.",
        )

    # Opening lease scan batches.
    for batch_index in range(3):
        batch_assets = lease_assets[batch_index * 12 : (batch_index + 1) * 12]
        day = date(2027, 1, 2) + timedelta(days=batch_index)
        add_schedule(
            day,
            "Opening setup",
            "Workspace Administrator",
            "Web" if batch_index != 1 else "Mobile",
            "Scan-first",
            f"Opening lease intake batch {batch_index + 1}",
            f"L{batch_index * 12 + 1:03d}-L{(batch_index + 1) * 12:03d}",
            batch_assets,
            "",
            "Set the simulation clock; upload each lease; wait for extraction; review every required field; correct only deliberately degraded fields; confirm sequentially; follow created Property, Unit, Tenant, Lease, Agreement, and Tenant Account links.",
            "Exactly 12 distinct LeaseManagement relationships are created. Shared property/unit identities dedupe correctly. No record is created before confirmation.",
            "Batch counts; each draft status; field-confidence evidence; created links; DB-side list counts; audit/outbox evidence.",
            "Re-upload one file, reject one intentionally, then retry it; prove no duplicate lease or half-created aggregate.",
        )

    add_schedule(
        date(2027, 1, 15),
        "Opening setup",
        "Workspace Administrator",
        "Web + Mobile",
        "Reconciliation",
        "Opening portfolio control checkpoint",
        "30 properties; 40 units; 36 active opening leases; 36 tenant accounts",
        lease_assets,
        opening_id,
        "Reconcile dashboard, Rentals lists, Owner views, deposit register, property basis, loans, occupied/vacant units, lease expirations, and opening bank balances against the Portfolio and Oracle sheets.",
        "Counts, ownership, opening cash, loan liability, deposit cash/liability, and occupied/vacant states equal the independent controls.",
        "One evidence pack containing both clients, filtered reports, and exported opening reports.",
        "Use role-restricted staff and tenant accounts to prove cross-role/cross-portfolio isolation.",
        "No unexplained variance; all rejected/duplicate scans have durable status and created nothing.",
    )


def active_leases_for_month(month_start: date) -> list[Lease]:
    end = month_end(month_start)
    return [lease for lease in leases if lease.start_date <= end and lease.end_date >= month_start]


def prorated_rent(lease: Lease, month_start: date) -> int:
    if lease.start_date.year == month_start.year and lease.start_date.month == month_start.month and lease.start_date.day > 1:
        days = calendar.monthrange(month_start.year, month_start.month)[1]
        occupied = days - lease.start_date.day + 1
        return int(
            (Decimal(lease.monthly_rent_cents) * Decimal(occupied) / Decimal(days)).quantize(
                Decimal("1"), rounding=ROUND_HALF_UP
            )
        )
    return lease.monthly_rent_cents


def add_charge(lease: Lease, month_start: date, amount: int) -> str:
    prop = property_by_unit(lease.unit_id)
    return add_financial_event(
        month_start,
        max(month_start, lease.start_date),
        "TenantCharge",
        "Rent",
        amount,
        [("Debit", "1100 Tenant Accounts Receivable", amount), ("Credit", "4000 Rental Income", amount)],
        prop.property_id,
        lease.unit_id,
        lease.lease_id,
        lease.tenant_name,
        "System scheduled charge",
        reference=f"RENT-{month_start.strftime('%Y%m')}-{lease.lease_id}",
        deltas={"ar": amount, "income": amount},
        memo=f"{month_start.strftime('%B %Y')} rent",
    )


def add_payment(
    lease: Lease,
    payment_date: date,
    amount: int,
    charge_id: str,
    method_index: int,
    suffix: str = "",
) -> str:
    prop = property_by_unit(lease.unit_id)
    modes = ["Scanned check", "Scanned money order", "Manual ACH", "Portal card"]
    mode = modes[method_index % len(modes)]
    asset = ""
    if mode.startswith("Scanned"):
        asset = add_asset(
            payment_date,
            "Rent check" if "check" in mode.lower() else "Money order",
            "Payment",
            "Direct scan draft matched to tenant account and charge",
            "Camera JPEG" if method_index % 2 else "PDF",
            "1 image",
            "phone camera; amount box, written amount, memo, and endorsement visible",
            "payer; amount; date; check/money-order number; memo; property; unit; lease match",
            "handwritten memo and similar tenant names" if method_index % 5 == 0 else "",
            "Use a different receipt on the other client to exercise the manual payment form.",
            prop.property_id,
            lease.unit_id,
            lease.lease_id,
            slug=f"rent-payment-{lease.lease_id}-{payment_date.strftime('%Y%m%d')}",
        )
    payment_id = add_financial_event(
        payment_date,
        payment_date,
        "TenantReceipt",
        "Rent receipt",
        amount,
        [("Debit", "1000 Operating Cash", amount), ("Credit", "1100 Tenant Accounts Receivable", amount)],
        prop.property_id,
        lease.unit_id,
        lease.lease_id,
        lease.tenant_name,
        mode,
        asset,
        reference=f"PMT-{payment_date.strftime('%Y%m%d')}-{lease.lease_id}{suffix}",
        deltas={"ar": -amount, "operating_cash": amount},
        memo=f"Receipt allocated to {charge_id}",
    )
    if asset:
        next(item for item in assets if item.asset_id == asset).financial_event_id = payment_id
    add_schedule(
        payment_date,
        "Monthly operations",
        "Property Manager",
        "Mobile" if method_index % 2 else "Web",
        mode,
        "Post and allocate tenant receipt",
        f"{prop.property_id}; {lease.unit_id}; {lease.lease_id}",
        asset,
        [charge_id, payment_id],
        "Open Scan for paper payment or the tenant-account receipt form for electronic payment; verify tenant/lease match, amount, date, method, and reference; confirm once; open ledger detail and allocation.",
        "Receipt is append-only, allocated once to the correct rent charge, and visible consistently in Unit, Lease, Tenant Account, Accounting, and portal views.",
        "Scan review or form; success result; exact ledger rows; payment detail; accounting delta; portal receipt.",
        "Retry the idempotency key once during the quarter assigned in the coverage matrix; no duplicate cash or allocation.",
    )
    return payment_id


def build_monthly_rent_and_payments() -> None:
    for month in range(1, 13):
        month_start = date(YEAR, month, 1)
        active = active_leases_for_month(month_start)
        charge_ids = []
        for lease in active:
            amount = prorated_rent(lease, month_start)
            charge_id = add_charge(lease, month_start, amount)
            charge_ids.append(charge_id)

            lease_number = int(lease.lease_id[1:])
            if lease.unit_id == "U017" and month == 6:
                partial = cents(300)
                add_payment(lease, date(YEAR, month, 15), partial, charge_id, lease_number, "-PART")
                continue

            if lease.unit_id == "U022" and month == 3:
                first_payment = add_payment(lease, date(YEAR, month, 3), amount, charge_id, lease_number, "-NSF1")
                prop = property_by_unit(lease.unit_id)
                reversal = add_financial_event(
                    date(YEAR, month, 5),
                    date(YEAR, month, 5),
                    "PaymentReversal",
                    "NSF reversal",
                    amount,
                    [("Debit", "1100 Tenant Accounts Receivable", amount), ("Credit", "1000 Operating Cash", amount)],
                    prop.property_id,
                    lease.unit_id,
                    lease.lease_id,
                    lease.tenant_name,
                    "Bank return",
                    reference=f"NSF-{first_payment}",
                    deltas={"ar": amount, "operating_cash": -amount},
                    memo="Returned check reverses the original receipt; original row remains",
                )
                fee = cents(35)
                fee_id = add_financial_event(
                    date(YEAR, month, 5),
                    date(YEAR, month, 5),
                    "TenantCharge",
                    "NSF fee",
                    fee,
                    [("Debit", "1100 Tenant Accounts Receivable", fee), ("Credit", "4020 Other Tenant Income", fee)],
                    prop.property_id,
                    lease.unit_id,
                    lease.lease_id,
                    lease.tenant_name,
                    "Manual approved fee",
                    reference=f"NSF-FEE-{lease.lease_id}-202703",
                    deltas={"ar": fee, "income": fee},
                    memo="Synthetic lease-authorized NSF fee",
                )
                add_payment(lease, date(YEAR, month, 10), amount + fee, fee_id, lease_number + 1, "-RETRY")
                add_schedule(
                    date(YEAR, month, 5),
                    "Delinquency and corrections",
                    "Workspace Administrator",
                    "Web + Mobile",
                    "Bank event + manual charge",
                    "NSF reversal and fee",
                    f"{lease.lease_id}; {lease.unit_id}",
                    "",
                    [reversal, fee_id],
                    "Reverse the failed receipt without editing/deleting it; add the authorized NSF fee; inspect balance before the replacement payment.",
                    "Cash, AR, original payment, reversal, fee, and later replacement payment remain separately visible and net correctly.",
                    "Ledger sequence on both clients; audit; bank match state; accounting summary before/after.",
                    "Attempt to reverse the same payment twice and prove idempotent rejection.",
                )
                continue

            if lease_number % 11 == 0:
                first = int(Decimal(amount) * Decimal("0.60"))
                second = amount - first
                add_payment(lease, safe_day(YEAR, month, 3), first, charge_id, lease_number, "-A")
                fee = cents(50)
                prop = property_by_unit(lease.unit_id)
                fee_id = add_financial_event(
                    safe_day(YEAR, month, 6),
                    safe_day(YEAR, month, 6),
                    "TenantCharge",
                    "Late fee",
                    fee,
                    [("Debit", "1100 Tenant Accounts Receivable", fee), ("Credit", "4010 Late Fee Income", fee)],
                    prop.property_id,
                    lease.unit_id,
                    lease.lease_id,
                    lease.tenant_name,
                    "System automation",
                    reference=f"LATE-{YEAR}{month:02d}-{lease.lease_id}",
                    deltas={"ar": fee, "income": fee},
                    memo="Synthetic lease-authorized late fee after partial payment",
                )
                add_payment(lease, safe_day(YEAR, month, 12), second + fee, fee_id, lease_number + 2, "-B")
            elif lease_number % 13 == 0:
                fee = cents(50)
                prop = property_by_unit(lease.unit_id)
                fee_id = add_financial_event(
                    safe_day(YEAR, month, 6),
                    safe_day(YEAR, month, 6),
                    "TenantCharge",
                    "Late fee",
                    fee,
                    [("Debit", "1100 Tenant Accounts Receivable", fee), ("Credit", "4010 Late Fee Income", fee)],
                    prop.property_id,
                    lease.unit_id,
                    lease.lease_id,
                    lease.tenant_name,
                    "System automation",
                    reference=f"LATE-{YEAR}{month:02d}-{lease.lease_id}",
                    deltas={"ar": fee, "income": fee},
                    memo="Synthetic lease-authorized late fee",
                )
                add_payment(lease, safe_day(YEAR, month, 8), amount + fee, fee_id, lease_number + 3)
            else:
                add_payment(lease, safe_day(YEAR, month, 3 + (lease_number % 3)), amount, charge_id, lease_number)

        add_schedule(
            month_start,
            "Monthly operations",
            "Workspace Administrator",
            "Web + Mobile",
            "System automation + reconciliation",
            f"{month_start.strftime('%B')} recurring rent generation",
            f"{len(active)} active tenant accounts",
            "",
            charge_ids,
            "Advance the simulation clock to 08:00 local; run or wait for scheduled finance worker; compare generated charges to active governing agreements, start dates, proration, and unique billing-period keys.",
            f"Exactly {len(active)} rent charges are posted once; mid-month starts prorate; ended leases do not receive future charges.",
            "Worker status; charge list; month rent-roll export; sampled Unit/Lease/portal balances; SQL query evidence.",
            "Run the finance worker a second time and prove no duplicate charge; inject one failure and prove charge/audit/outbox atomic rollback.",
        )


def expense_account(category: str) -> str:
    return {
        "Repairs": "5000 Repairs and Maintenance",
        "Utilities": "5010 Utilities",
        "Insurance": "5020 Insurance",
        "Property tax": "5030 Property Taxes",
        "Mortgage interest": "5040 Mortgage Interest",
        "Professional": "5050 Professional Services",
        "Advertising": "5060 Advertising",
        "Turnover": "5070 Cleaning and Turnover",
        "Screening": "5080 Screening",
        "Bank fee": "5090 Bank Fees",
        "Legal": "5110 Legal",
        "Supplies": "5120 Supplies",
    }[category]


def add_expense(
    day: date,
    prop: Property,
    unit: Unit | None,
    amount: int,
    category: str,
    counterparty: str,
    index: int,
    work_order: str = "",
) -> str:
    is_scan = index % 5 != 0
    mode = "Scanned receipt/invoice" if is_scan else ("Manual web form" if index % 2 else "Manual mobile form")
    asset = ""
    if is_scan:
        asset = add_asset(
            day,
            "Vendor invoice / receipt",
            "Expense",
            "Direct scan draft with property/unit/work-order context",
            "PDF" if index % 3 else "Camera JPEG",
            "1-3 pages",
            ["clean emailed invoice", "crumpled thermal receipt", "phone photo with shadow"][index % 3],
            "vendor; invoice number; date; subtotal; tax; total; category; property; unit; line items; payment method",
            "split tax/line items; duplicate invoice number" if index % 17 == 0 else "",
            "Create a different expense of the same category through the equivalent manual form on the other client.",
            prop.property_id,
            unit.unit_id if unit else "",
            slug=f"expense-{day.strftime('%Y%m%d')}-{index:03d}",
        )
    event_id = add_financial_event(
        day,
        day,
        "OperatingExpense",
        category,
        amount,
        [("Debit", expense_account(category), amount), ("Credit", "1000 Operating Cash", amount)],
        prop.property_id,
        unit.unit_id if unit else "",
        "",
        counterparty,
        mode,
        asset,
        reference=f"EXP-{day.strftime('%Y%m%d')}-{index:03d}",
        deltas={"operating_cash": -amount, "expense": amount},
        memo=f"{counterparty}; {work_order}".strip("; "),
    )
    if asset:
        next(item for item in assets if item.asset_id == asset).financial_event_id = event_id
    add_schedule(
        day,
        "Weekly operations",
        "Property Manager",
        "Mobile" if index % 2 else "Web",
        mode,
        "Record, review, allocate, and pay operating expense",
        f"{prop.property_id}; {unit.unit_id if unit else 'Property scope'}; {work_order}",
        asset,
        event_id,
        "Capture or upload the source; verify vendor, date, scope, category, line items, tax, amount, and work-order link; confirm; open detail; verify attachment; compare Money and Property/Unit views.",
        "One expense exists at its real operational scope, cash falls once, Schedule E category and allocations agree, and no per-unit copies are created.",
        "Source preview; review/form; expense detail; line items; Property/Unit ledger; audit; accounting/report delta.",
        "On marked duplicate-invoice cases, re-upload before confirm and prove the product prevents or clearly warns about duplication.",
    )
    return event_id


def build_weekly_operations() -> None:
    expense_amounts = [cents(x) for x in ("89.45", "147.80", "225.00", "318.65", "475.00", "690.25", "940.00")]
    issues = [
        "slow kitchen drain", "HVAC filter and no-cool call", "loose outlet", "yard cleanup",
        "roof flashing leak", "pest treatment", "turnover touch-up", "lock rekey",
    ]
    vendors_by_service = VENDORS[:10]
    current = START
    week = 0
    while current <= END:
        if current.weekday() == 1:  # Tuesday
            week += 1
            unit = units[(week * 7) % len(units)]
            prop = property_by_unit(unit.unit_id)
            vendor = vendors_by_service[week % len(vendors_by_service)]
            request_day = current - timedelta(days=1)
            work_asset = add_asset(
                request_day,
                "Maintenance request with supporting photo",
                "WorkOrder",
                "Direct scan draft",
                "Camera JPEG",
                "2 images",
                "handwritten tenant note plus issue photo",
                "requester; property; unit; issue; priority; permission to enter; preferred window; notes",
                "handwriting and one irrelevant photo" if week % 9 == 0 else "",
                "Create a different work order manually from the other client and compare field validation.",
                prop.property_id,
                unit.unit_id,
                slug=f"work-order-week-{week:02d}",
            )
            work_ref = f"WO-{YEAR}-{week:03d}"
            add_schedule(
                request_day,
                "Weekly operations",
                "Property Manager",
                "Mobile" if week % 2 else "Web",
                "Scan-first",
                "Create and triage maintenance request",
                f"{prop.property_id}; {unit.unit_id}; {work_ref}",
                work_asset,
                "",
                f"Capture the tenant note/photo; review {issues[week % len(issues)]}; confirm; set responsibility; dispatch {vendor[1]}; create an appointment and verify the tenant notification.",
                "Work order, status timeline, responsibility, vendor dispatch, appointment, notification, and Unit context are linked once.",
                "Scan review; work order detail/timeline; dispatch; appointment; notification; tenant portal view.",
                "Cancel one dispatch each quarter, reassign once, and prove status/history remains append-only.",
            )
            category = "Turnover" if week % 13 == 0 else ("Supplies" if vendor[2] == "Supplies" else "Repairs")
            add_expense(
                current,
                prop,
                unit,
                expense_amounts[week % len(expense_amounts)],
                category,
                vendor[1],
                week,
                work_ref,
            )
        current += timedelta(days=1)


def build_recurring_and_owner_finance() -> None:
    duplex_properties = properties[20:]
    financed = [prop for prop in properties if prop.loan_id]
    for month in range(1, 13):
        month_start = date(YEAR, month, 1)
        # Shared utilities for every duplex.
        for idx, prop in enumerate(duplex_properties, start=1):
            day = safe_day(YEAR, month, 15)
            amount = cents(118 + ((month * 11 + idx * 7) % 90))
            add_expense(day, prop, None, amount, "Utilities", "City Water & Sewer", 1000 + month * 20 + idx)

        # Loan payments: principal reduces liability, interest is Schedule E expense.
        loan_refs = []
        for idx, prop in enumerate(financed, start=1):
            day = safe_day(YEAR, month, 20)
            principal = prop.monthly_principal_cents
            interest = max(cents(350), prop.monthly_interest_cents - cents((month - 1) * 8))
            total = principal + interest
            event_id = add_financial_event(
                day,
                day,
                "LoanPayment",
                "Mortgage principal and interest",
                total,
                [
                    ("Debit", "2200 Mortgage Loans Payable", principal),
                    ("Debit", "5040 Mortgage Interest", interest),
                    ("Credit", "1000 Operating Cash", total),
                ],
                prop.property_id,
                counterparty="First QA Mortgage",
                source_mode="Recurring loan payment + scanned statement",
                reference=f"{prop.loan_id}-{YEAR}{month:02d}",
                deltas={"operating_cash": -total, "loan_liability": -principal, "expense": interest},
                memo=f"{prop.loan_id} scheduled payment",
            )
            loan_refs.append(event_id)
            statement = add_asset(
                day,
                "Monthly mortgage statement",
                "Loan payment / Property document",
                "Attach and match to existing loan; probe typed extraction of principal/interest",
                "PDF",
                "3 pages",
                "clean lender PDF",
                "loan; due date; payment; principal; interest; escrow; ending principal balance",
                "principal/interest changes monthly",
                "Verify or enter a different loan payment manually from the other client.",
                prop.property_id,
                financial_event_id=event_id,
                slug=f"loan-payment-{prop.loan_id}-{YEAR}{month:02d}",
            )
            financial_events[-1].source_asset_id = statement
        add_schedule(
            safe_day(YEAR, month, 20),
            "Monthly close",
            "Workspace Administrator",
            "Web + Mobile",
            "Recurring automation + statement match",
            f"{month_start.strftime('%B')} loan payments",
            "; ".join(prop.loan_id for prop in financed),
            [event.source_asset_id for event in financial_events if event.event_id in loan_refs],
            loan_refs,
            "Verify the recurring loan payments; attach/match each statement; compare principal, interest, cash, loan balance, amortization, Property Money view, and Schedule E interest.",
            "Every payment splits principal and interest once; principal reduces liability, interest is expense, and statement balance matches.",
            "Loan detail samples on both clients; statement attachment; amortization; Property and year-end reports.",
            "Attempt a duplicate recurrence run; no second payment or balance movement.",
        )

        # Owner distributions by entity.
        distribution_refs = []
        for owner_idx, owner in enumerate(OWNER_NAMES, start=1):
            day = safe_day(YEAR, month, 25)
            amount = cents(2500 + owner_idx * 350 + (month % 3) * 200)
            event_id = add_financial_event(
                day,
                day,
                "OwnerDistribution",
                "Owner distribution",
                amount,
                [("Debit", "3100 Owner Distributions", amount), ("Credit", "1000 Operating Cash", amount)],
                counterparty=owner,
                source_mode="Manual approval and bank export",
                reference=f"DIST-{YEAR}{month:02d}-O{owner_idx:02d}",
                deltas={"operating_cash": -amount, "owner_distribution": amount},
                memo=f"Monthly distribution to {owner}",
            )
            distribution_refs.append(event_id)
        add_schedule(
            safe_day(YEAR, month, 25),
            "Monthly close",
            "Owner + Workspace Administrator",
            "Web + Mobile owner portal",
            "Manual approval",
            f"{month_start.strftime('%B')} owner statements and distributions",
            "O01-O05",
            "",
            distribution_refs,
            "Generate owner statements; review income/expense allocations; approve distributions; record the bank export; open statements from owner portal on web and mobile.",
            "Five owner statements agree with Property/accounting totals and five distributions reduce cash/equity distributions without becoming expenses.",
            "Statements; approvals; owner portal PDFs; distribution rows; bank references; audit.",
            "Reject one draft distribution before approval each quarter and prove no cash event is posted.",
        )

        # Month-end bank statement and close.
        close_day = month_end(month_start)
        bank_asset = add_asset(
            close_day,
            "Operating bank statement",
            "Banking import",
            "Bank statement/import candidate; manually import structured transactions if PDF scan is unsupported",
            "PDF",
            "8-12 pages",
            "clean bank PDF with synthetic account ending 4242",
            "statement period; opening balance; deposits; withdrawals; fees; ending balance; transaction references",
            "one pending item in March and one duplicate feed row in September",
            "Use Banking review/match on web and mobile; manually enter one unmatched bank fee.",
            slug=f"bank-statement-{YEAR}-{month:02d}",
        )
        fee_id = ""
        if month in (3, 6, 9, 12):
            fee = cents(18)
            fee_id = add_financial_event(
                close_day,
                close_day,
                "BankFee",
                "Bank fee",
                fee,
                [("Debit", "5090 Bank Fees", fee), ("Credit", "1000 Operating Cash", fee)],
                counterparty="First QA Bank",
                source_mode="Bank statement match",
                source_asset_id=bank_asset,
                reference=f"BANKFEE-{YEAR}{month:02d}",
                deltas={"operating_cash": -fee, "expense": fee},
                memo="Quarterly account analysis fee",
            )
        add_schedule(
            close_day,
            "Monthly close",
            "Workspace Administrator",
            "Web + Mobile",
            "Scan/import + reconciliation",
            f"{month_start.strftime('%B')} bank reconciliation and close",
            "Operating account 4242; deposit trust account 1818; reserve account 7070",
            bank_asset,
            fee_id,
            "Set clock to month-end; import/attach the statement; match receipts, expenses, loan payments, distributions, transfers, and fees; review unmatched/duplicate rows; run every monthly report and compare to Monthly Controls.",
            "Bank ending cash, AR, deposit trust/liability, loan balance, income, expense, and net operating result match the oracle with zero unexplained variance.",
            "Bank reconciliation; accounting summary; P&L; cash flow; rent roll/ledger; deposits; delinquency; owner statements; audit export.",
            "Try one duplicate bank transaction and one date-filter boundary; prove DB-side totals and idempotent matching.",
            "All monthly checks are OK or a reproducible bug is captured before advancing the clock.",
        )

    # Annual insurance and quarterly property tax batches.
    for idx, prop in enumerate(properties, start=1):
        insurance_day = safe_day(YEAR, 1 + ((idx - 1) % 12), 18)
        insurance = cents(980 + idx * 17)
        add_expense(insurance_day, prop, None, insurance, "Insurance", "Buckeye Mutual Insurance", 2000 + idx)
        for quarter, month in enumerate((3, 6, 9, 12), start=1):
            tax_day = safe_day(YEAR, month, 28)
            tax = cents(510 + idx * 13 + quarter * 7)
            add_expense(tax_day, prop, None, tax, "Property tax", "Franklin County Treasurer", 3000 + quarter * 100 + idx)


def build_lease_lifecycle() -> None:
    replacement_ids = {"L037", "L038", "L039", "L040", "L042", "L043", "L044", "L045"}
    for idx, lease in enumerate([item for item in leases if item.lease_id in replacement_ids], start=1):
        prop = property_by_unit(lease.unit_id)
        application_day = max(date(2027, 1, 2), lease.start_date - timedelta(days=35 if lease.start_date.day == 1 else 28))
        approve_day = application_day + timedelta(days=5)
        issue_day = application_day + timedelta(days=9)
        deposit_day = lease.start_date - timedelta(days=7)
        move_in_day = lease.start_date
        app_asset = add_asset(
            application_day,
            "Rental application packet",
            "Application",
            "Direct scan draft",
            "PDF" if idx % 2 else "Camera JPEG",
            "6 pages",
            "completed paper form with pay-stub and ID placeholders",
            "applicant; contact; desired unit; income; employer; household; pets; references; consent",
            "co-applicant on every third case; one blank optional field",
            "Enter a separate applicant manually on the other client, reject it with a non-discriminatory synthetic reason, and preserve audit.",
            prop.property_id,
            lease.unit_id,
            lease.lease_id,
            slug=f"application-{lease.lease_id}",
        )
        add_schedule(
            application_day,
            "Leasing",
            "Leasing Agent",
            "Mobile" if idx % 2 else "Web",
            "Scan-first",
            "Application intake and pipeline",
            f"{prop.property_id}; {lease.unit_id}; {lease.lease_id}; {lease.tenant_id}",
            app_asset,
            "",
            "Upload/capture the application; review extracted applicant and unit context; confirm; inspect pipeline; request screening in synthetic/test-only mode; record a fair, documented decision.",
            "Application, applicant tenant prospect, unit link, status history, screening placeholder, and communication are created once without exposing sensitive values.",
            "Scan draft; Application detail; pipeline; status audit; applicant communication; role/access proof.",
            "Upload a duplicate application and a wrong-unit application; no duplicate person or cross-property link.",
        )
        lease_asset = add_asset(
            issue_day,
            "Residential lease agreement",
            "Lease",
            "Direct scan draft or native agreement upload",
            "PDF",
            "11 pages",
            "clean lease PDF with e-sign placeholders",
            "agreement version; parties; unit; term; rent; deposit; due day; late fee; utilities; signatures",
            "future start; co-signer on L039; mid-month proration on L042",
            "Build a separate draft manually from the other client and cancel it before issuance.",
            prop.property_id,
            lease.unit_id,
            lease.lease_id,
            slug=f"new-lease-{lease.lease_id}",
        )
        add_schedule(
            approve_day,
            "Leasing",
            "Leasing Agent",
            "Web + Mobile",
            "Manual lifecycle command",
            "Approve application and prepare move-in",
            f"{lease.lease_id}; {lease.unit_id}; {lease.tenant_id}",
            "",
            "",
            "Approve the selected application; run Prepare move-in; confirm parties, planned possession, rent, deposit, Tenant Account, and initial Agreement draft; invite portal access under policy.",
            "One planned LeaseManagement, party set, account, and agreement draft commit atomically; Unit is move-in scheduled, not occupied.",
            "Application decision; Unit state; parties; account; draft; portal invitation; atomic audit.",
            "Inject failure at the former save boundary and prove no partial relationship/account/agreement remains.",
        )
        add_schedule(
            issue_day,
            "Leasing",
            "Leasing Agent",
            "Web" if idx % 2 else "Mobile",
            "Scan/document + e-sign",
            "Issue and execute lease agreement",
            f"{lease.lease_id}; {lease.unit_id}",
            lease_asset,
            "",
            "Confirm the scanned/native agreement against the prepared draft; issue a signature packet; view as landlord and tenant; sign; finalize immutable PDF/hash; verify Agreement is Upcoming until effective date.",
            "Issued version is immutable, one signature packet is active, audit events are complete, and the effective-status projection follows the simulated date.",
            "Draft/issued/final PDFs; hashes; signer states; signature audit; portal view; status before/after effective date.",
            "Void one deliberately incorrect issued packet, issue a replacement, and prove the voided artifact remains while no competing active packet exists.",
        )
        # Deposit collection.
        dep_asset = add_asset(
            deposit_day,
            "Security deposit receipt",
            "Security deposit / Payment",
            "Payment draft plus deposit-register confirmation; log typed-deposit intake gap if not routed automatically",
            "Camera JPEG",
            "1 image",
            "money order receipt photographed on phone",
            "payer; amount; date; receipt number; property; unit; lease; deposit purpose",
            "deposit must not become rent income",
            "Post a different deposit manually on the other client and verify trust/liability treatment.",
            prop.property_id,
            lease.unit_id,
            lease.lease_id,
            slug=f"security-deposit-{lease.lease_id}",
        )
        deposit_id = add_financial_event(
            deposit_day,
            deposit_day,
            "SecurityDepositCollection",
            "Security deposit",
            lease.deposit_cents,
            [
                ("Debit", "1010 Security Deposit Trust Cash", lease.deposit_cents),
                ("Credit", "2000 Security Deposit Liability", lease.deposit_cents),
            ],
            prop.property_id,
            lease.unit_id,
            lease.lease_id,
            lease.tenant_name,
            "Scanned money order + deposit confirmation",
            dep_asset,
            reference=f"DEP-{lease.lease_id}",
            deltas={"deposit_cash": lease.deposit_cents, "deposit_liability": lease.deposit_cents},
            memo="Deposit held in synthetic trust account; not rental income",
        )
        next(item for item in assets if item.asset_id == dep_asset).financial_event_id = deposit_id
        add_schedule(
            deposit_day,
            "Leasing",
            "Property Manager",
            "Mobile" if idx % 2 else "Web",
            "Scan + manual classification",
            "Collect and classify security deposit",
            f"{lease.lease_id}; {lease.unit_id}",
            dep_asset,
            deposit_id,
            "Capture the receipt; verify payer/amount; classify to the security-deposit account rather than rent; confirm trust account, liability, lease provenance, and receipt.",
            "Deposit trust cash and liability rise equally; tenant rent AR and rental income do not change.",
            "Scan review; deposit register; tenant account provenance; trust cash; accounting checks; receipt.",
            "Try to classify the deposit as rent, cancel, then classify correctly; no abandoned financial row remains.",
        )
        add_schedule(
            move_in_day,
            "Leasing",
            "Property Manager + Tenant",
            "Web + Mobile",
            "Manual + photo evidence",
            "Move-in inspection and possession",
            f"{lease.lease_id}; {lease.unit_id}",
            "",
            "",
            "Run the move-in inspection on mobile with room/item results and photos; review on web; record key handoff and possession; verify occupancy opens only now; tenant confirms portal, lease, payment, maintenance, messages, profile, security, and notifications.",
            "Inspection completes, possession opens once, Unit becomes occupied, portal access is scoped, and no lease-status shortcut substitutes for possession truth.",
            "Inspection report/photos; possession event; occupancy reports; both clients; tenant portal walkthrough.",
            "Attempt possession a second time and use an invalid date; database invariant prevents overlap/duplication.",
        )

    # Move-outs, deposit dispositions, and turnover.
    outgoing = [lease for lease in leases[:36] if lease.end_date < END]
    damage_by_unit = {
        "U003": 0,
        "U005": 0,
        "U008": cents(125),
        "U014": cents(425),
        "U017": cents(650),
        "U022": cents(200),
        "U034": cents(310),
    }
    for idx, lease in enumerate(outgoing, start=1):
        prop = property_by_unit(lease.unit_id)
        notice_day = lease.end_date - timedelta(days=45)
        inspection_day = lease.end_date
        disposition_day = lease.end_date + timedelta(days=5)
        damage = damage_by_unit[lease.unit_id]
        notice_asset = add_asset(
            notice_day,
            "Move-out / non-renewal notice",
            "Notice / Lease document",
            "Document attachment and notice-draft comparison",
            "PDF" if idx % 2 else "Camera JPEG",
            "2 pages",
            "signed tenant notice",
            "tenant; unit; notice date; planned move-out; forwarding-address placeholder; signature",
            "date on source differs from upload date",
            "Create a separate notice from template manually on the other client.",
            prop.property_id,
            lease.unit_id,
            lease.lease_id,
            slug=f"move-out-notice-{lease.lease_id}",
        )
        add_schedule(
            notice_day,
            "Lease ending",
            "Property Manager",
            "Web" if idx % 2 else "Mobile",
            "Scan attachment + manual lifecycle",
            "Record notice and plan lease ending",
            f"{lease.lease_id}; {lease.unit_id}",
            notice_asset,
            "",
            "Attach/capture the source notice; create the matching notice draft/history; record planned move-out and ending disposition; schedule inspection and turnover work.",
            "Notice source, effective dates, appointment, ending facts, and Unit next action agree without ending occupancy early.",
            "Notice source/preview; lease timeline; appointment; Unit summary; notification/audit.",
            "Use an upload date after the document date and prove both dates remain distinct.",
        )
        damage_asset = add_asset(
            inspection_day,
            "Move-out inspection report",
            "Inspection / Property document",
            "Photo/report attachment; typed inspection items are confirmed manually",
            "PDF",
            "10 pages",
            "generated report with room photos",
            "inspection date; inspector; unit; checklist results; notes; photos; damage estimate",
            "normal wear vs chargeable damage",
            "Run the actual checklist manually on mobile and review/edit-before-complete on web.",
            prop.property_id,
            lease.unit_id,
            lease.lease_id,
            slug=f"move-out-inspection-{lease.lease_id}",
        )
        add_schedule(
            inspection_day,
            "Lease ending",
            "Property Manager + Maintenance Technician",
            "Mobile + Web",
            "Manual inspection + scanned report",
            "Return possession, inspect, and open turnover",
            f"{lease.lease_id}; {lease.unit_id}",
            damage_asset,
            "",
            "Complete the move-out inspection on mobile; attach/report on web; record keys and possession returned; open turnover tasks; separate normal wear from chargeable damage.",
            "Possession closes once, Unit becomes Turnover/Vacant as appropriate, inspection freezes after completion, and linked work orders appear.",
            "Inspection checklist/report/photos; possession event; turnover workspace; occupancy and lease-expiration reports.",
            "Try editing a completed inspection and returning possession twice; both are rejected without partial changes.",
        )
        # Damage charge, application of deposit, and return.
        event_refs: list[str] = []
        if damage:
            charge_id = add_financial_event(
                disposition_day,
                inspection_day,
                "TenantCharge",
                "Damage reimbursement",
                damage,
                [("Debit", "1100 Tenant Accounts Receivable", damage), ("Credit", "4020 Other Tenant Income", damage)],
                prop.property_id,
                lease.unit_id,
                lease.lease_id,
                lease.tenant_name,
                "Manual from completed inspection",
                damage_asset,
                reference=f"DAMAGE-{lease.lease_id}",
                deltas={"ar": damage, "income": damage},
                memo="Chargeable damage supported by completed inspection",
            )
            event_refs.append(charge_id)
        # U017 still has unpaid June rent. Expected AR before deposit is rent - partial + damage.
        outstanding = damage
        if lease.unit_id == "U017":
            outstanding += lease.monthly_rent_cents - cents(300)
        applied = min(lease.deposit_cents, outstanding)
        refund = lease.deposit_cents - applied
        if applied:
            apply_id = add_financial_event(
                disposition_day,
                disposition_day,
                "SecurityDepositApplication",
                "Deposit applied to tenant balance",
                applied,
                [
                    ("Debit", "2000 Security Deposit Liability", applied),
                    ("Credit", "1100 Tenant Accounts Receivable", applied),
                    ("Debit", "1000 Operating Cash", applied),
                    ("Credit", "1010 Security Deposit Trust Cash", applied),
                ],
                prop.property_id,
                lease.unit_id,
                lease.lease_id,
                lease.tenant_name,
                "Manual disposition",
                reference=f"DEP-APPLY-{lease.lease_id}",
                deltas={
                    "ar": -applied,
                    "operating_cash": applied,
                    "deposit_cash": -applied,
                    "deposit_liability": -applied,
                },
                memo="Deposit applied with trust-to-operating cash transfer",
            )
            event_refs.append(apply_id)
        if refund:
            refund_id = add_financial_event(
                disposition_day,
                disposition_day,
                "SecurityDepositRefund",
                "Deposit refund",
                refund,
                [
                    ("Debit", "2000 Security Deposit Liability", refund),
                    ("Credit", "1010 Security Deposit Trust Cash", refund),
                ],
                prop.property_id,
                lease.unit_id,
                lease.lease_id,
                lease.tenant_name,
                "Manual disposition",
                reference=f"DEP-REFUND-{lease.lease_id}",
                deltas={"deposit_cash": -refund, "deposit_liability": -refund},
                memo="Refund of remaining security deposit liability",
            )
            event_refs.append(refund_id)
        add_schedule(
            disposition_day,
            "Lease ending",
            "Workspace Administrator",
            "Web + Mobile",
            "Manual financial disposition",
            "Finalize security deposit disposition",
            f"{lease.lease_id}; {lease.unit_id}",
            damage_asset,
            event_refs,
            "Create supported damage charge if applicable; apply deposit to the tenant balance; transfer applied cash to operating; refund the remainder; deliver itemized statement; compare ledger and deposit register.",
            f"Deposit liability falls by {dollars(lease.deposit_cents)}; tenant AR falls by {dollars(applied)}; refund is {dollars(refund)}; income changes only for supported charges.",
            "Inspection support; charge; application; cash transfer; refund; itemized statement; portal/account history; accounting checks.",
            "Inject a failure before final save and prove charge/application/refund/audit rows all roll back together.",
        )

    # Eviction/legal branch.
    eviction_lease = next(item for item in leases if item.unit_id == "U017" and item.lease_id == "L017")
    for day, workflow in [
        (date(2027, 6, 6), "Generate past-due notice draft"),
        (date(2027, 6, 10), "Serve synthetic notice and record event"),
        (date(2027, 6, 14), "Open eviction case and add respondent"),
        (date(2027, 6, 18), "Record hearing and judgment events"),
    ]:
        prop = property_by_unit(eviction_lease.unit_id)
        legal_asset = add_asset(
            day,
            "Synthetic legal notice / court filing",
            "Notice or Eviction case document",
            "Attachment plus manual typed legal-event confirmation",
            "PDF",
            "2-5 pages",
            "synthetic court-style form with prominent QA watermark",
            "case reference; parties; unit; event date; status; next date; amount",
            "not legal advice; synthetic acceptance artifact only",
            "Create the corresponding notice/case/event manually on the other client.",
            prop.property_id,
            eviction_lease.unit_id,
            eviction_lease.lease_id,
            slug=f"eviction-{day.strftime('%Y%m%d')}",
        )
        add_schedule(
            day,
            "Delinquency and legal",
            "Workspace Administrator",
            "Web + Mobile",
            "Scanned document + manual legal workflow",
            workflow,
            f"{eviction_lease.lease_id}; {eviction_lease.unit_id}",
            legal_asset,
            "",
            "Use only the synthetic template and dates in this plan; attach source, create/update the notice or eviction case, record the durable event, verify respondent and next action, and inspect tenant/Unit/lease context.",
            "Legal workflow state and source history advance without changing rent, occupancy, or possession facts by implication.",
            "Document preview; notice/case detail; event timeline; Unit/lease badges; audit; restricted-role check.",
            "Try a duplicate event and an event dated before case opening; validation is explicit and atomic.",
        )
    add_expense(date(2027, 6, 18), property_by_unit("U017"), unit_by_id("U017"), cents(900), "Legal", "Buckeye Legal Services", 4017, "EVICT-L017")


def build_capital_assets_disposition_and_adjustments() -> None:
    # Capital improvement with invoice and capital-asset form.
    prop = properties[11]
    unit = next(item for item in units if item.property_id == prop.property_id)
    day = date(2027, 5, 18)
    asset = add_asset(
        day,
        "Capital improvement invoice",
        "Expense / Capital asset",
        "Expense scan draft followed by capitalization decision",
        "PDF",
        "4 pages",
        "contractor invoice with labor/material split and warranty",
        "vendor; property; unit; placed-in-service date; description; amount; warranty; line items",
        "must not remain both expense and capital asset",
        "Enter a separate small asset manually on the other client.",
        prop.property_id,
        unit.unit_id,
        slug="water-heater-capital-improvement",
    )
    amount = cents(6200)
    event_id = add_financial_event(
        day,
        day,
        "CapitalAssetPurchase",
        "Building improvement",
        amount,
        [("Debit", "1510 Capital Improvements", amount), ("Credit", "1000 Operating Cash", amount)],
        prop.property_id,
        unit.unit_id,
        counterparty="ComfortZone HVAC",
        source_mode="Scanned invoice + manual capitalization",
        source_asset_id=asset,
        reference="CAP-2027-001",
        deltas={"operating_cash": -amount, "capital_asset": amount},
        memo="Water-heater system placed in service; 27.5-year residential recovery class",
    )
    next(item for item in assets if item.asset_id == asset).financial_event_id = event_id
    add_schedule(
        day,
        "Capital and tax",
        "Workspace Administrator",
        "Web + Mobile",
        "Scan + manual capitalization",
        "Capitalize major improvement",
        f"{prop.property_id}; {unit.unit_id}",
        asset,
        event_id,
        "Scan the invoice; review line items; choose capital treatment; create Capital Asset with basis, placed-in-service date, recovery class, method, and property/unit link; verify it is not also operating expense.",
        "Cash falls once, capital basis rises, Schedule E repair expense does not rise, and depreciation schedule starts from placed-in-service date.",
        "Scan review; capital asset detail; property Money view; tax report; accounting journal; duplicate prevention.",
        "Try to confirm as both Expense and Capital Asset; cancel one path and prove no double counting.",
    )

    # Property disposition at year end.
    prop = properties[2]
    day = date(2027, 11, 15)
    gross = cents(265000)
    selling_costs = cents(15900)
    remaining_loan = prop.opening_loan_balance_cents - prop.monthly_principal_cents * 10
    basis = prop.original_cost_cents
    net_cash = gross - selling_costs - remaining_loan
    gross_gain_before_selling_costs = gross - basis
    asset = add_asset(
        day,
        "Closing disclosure / settlement statement",
        "Property disposition",
        "Typed disposition candidate; attach and enter disposition manually if unsupported",
        "PDF",
        "7 pages",
        "clean closing packet",
        "property; sale date; gross proceeds; selling costs; loan payoff; net proceeds; buyer; closing reference",
        "sale price is not net cash and loan payoff is not an expense",
        "Enter/verify the disposition on both clients using different read/edit actions.",
        prop.property_id,
        slug=f"property-sale-{prop.property_id}",
    )
    event_id = add_financial_event(
        day,
        day,
        "PropertyDisposition",
        "Property sale",
        gross,
        [
            ("Debit", "1000 Operating Cash", net_cash),
            ("Debit", "2200 Mortgage Loans Payable", remaining_loan),
            ("Debit", "5130 Selling Costs", selling_costs),
            ("Credit", "1500 Rental Property Basis", basis),
            ("Credit", "4030 Gross Gain on Property Sale", gross_gain_before_selling_costs),
        ],
        prop.property_id,
        counterparty="Synthetic Buyer LLC",
        source_mode="Scanned closing packet + manual disposition",
        source_asset_id=asset,
        reference="SALE-P003-20271115",
        deltas={
            "operating_cash": net_cash,
            "loan_liability": -remaining_loan,
            "income": gross_gain_before_selling_costs,
            "expense": selling_costs,
            "capital_asset": -basis,
        },
        memo="Synthetic property disposition; gain is simplified book gain for reconciliation",
    )
    next(item for item in assets if item.asset_id == asset).financial_event_id = event_id
    add_schedule(
        day,
        "Capital and tax",
        "Workspace Administrator",
        "Web + Mobile",
        "Scan attachment + manual disposition",
        "Dispose of one property after leases are closed",
        prop.property_id,
        asset,
        event_id,
        "Verify all Unit/lease/tenant obligations are resolved; attach closing packet; enter gross proceeds, selling costs, loan payoff, and disposition date; confirm guarded atomic command; review reports and historical access.",
        "Property becomes disposed without deleting history; cash, loan payoff, basis removal, selling cost, and book gain match the oracle.",
        "Precondition proof; closing document; disposition detail; historical reports; cash/loan/basis/gain checks; audit.",
        "Attempt disposition while an active lease remains in a negative test copy; command is blocked without partial financial rows.",
    )

    # Year-end depreciation.
    day = date(2027, 12, 31)
    depreciation_refs = []
    for prop in properties:
        if prop.property_id == "P003":
            # Simplified: still take ten months for the disposed property.
            annual = int((Decimal(prop.building_basis_cents) / Decimal("27.5") * Decimal(10) / Decimal(12)).quantize(Decimal("1")))
        else:
            annual = int((Decimal(prop.building_basis_cents) / Decimal("27.5")).quantize(Decimal("1")))
        event_id = add_financial_event(
            day,
            day,
            "Depreciation",
            "Residential rental depreciation",
            annual,
            [("Debit", "5100 Depreciation Expense", annual), ("Credit", "1590 Accumulated Depreciation", annual)],
            prop.property_id,
            counterparty="System calculation",
            source_mode="System DB-side report",
            reference=f"DEPR-{prop.property_id}-2027",
            deltas={"expense": annual, "accumulated_depreciation": annual},
            memo="Independent straight-line control; final system comparison must use configured recovery rules",
        )
        depreciation_refs.append(event_id)
    add_schedule(
        day,
        "Year-end close",
        "Workspace Administrator",
        "Web",
        "System calculation + reconciliation",
        "Run Schedule E, depreciation, 1099, and year-end packet",
        "P001-P030; O01-O05; V001-V012",
        "",
        depreciation_refs,
        "Freeze the clock at year-end; run depreciation and tax reports; review capital assets/disposition; generate vendor 1099 review, owner year-end packets, P&L, cash flow, GL, rent roll, deposits, delinquency, and audit export.",
        "Every report ties to the Oracle/Monthly Controls and journal; depreciation is DB-side; vendor/owner totals are consistent across filters.",
        "Complete export pack; workbook Checks sheet; report screenshots; SQL translation evidence; role/access proof.",
        "Repeat with date boundaries 2027-01-01 through 2027-12-31 and a single-property filter; no off-by-one or client-side aggregation.",
        "Zero unexplained variance and all Checks are OK before declaring the simulation complete.",
    )


def evenly_spaced_weekdays(count: int, start: date = date(YEAR, 1, 11), end: date = date(YEAR, 12, 30)) -> list[date]:
    weekdays: list[date] = []
    current = start
    while current <= end:
        if current.weekday() < 5:
            weekdays.append(current)
        current += timedelta(days=1)
    if count <= 1:
        return [weekdays[0]]
    return [weekdays[round(index * (len(weekdays) - 1) / (count - 1))] for index in range(count)]


def route_from_page(path: Path) -> str:
    relative = path.relative_to(ROOT / "web" / "src" / "routes")
    parts = [part for part in relative.parts[:-1] if not (part.startswith("(") and part.endswith(")"))]
    return "/" + "/".join(parts) if parts else "/"


def role_for_surface(client: str, surface: str) -> str:
    lowered = surface.lower()
    if "superadmin" in lowered:
        return "Superadmin"
    if re.search(r"(^|/)portal(/|$)", lowered) or "tenant_" in lowered or "/tenant_" in lowered:
        return "Tenant"
    if re.search(r"(^|/)owner(/|$)", lowered) or "owner_portal" in lowered or "owner_landing" in lowered:
        return "Owner"
    if (
        "/apply" in lowered
        or "/sign" in lowered
        or re.search(r"(^|/)(docs|privacy|features|welcome)(/|$)", lowered)
        or any(token in lowered for token in ["login", "register", "password", "verify_email"])
    ):
        return "Applicant / public user"
    if "/leasing" in lowered or "/applications" in lowered or "/leases" in lowered or "leasing_" in lowered or "application_" in lowered:
        return "Leasing Agent"
    if "/my-work" in lowered or "/my-schedule" in lowered or "technician_" in lowered:
        return "Maintenance Technician"
    if "/maintenance" in lowered or "work_order" in lowered or "inspection" in lowered or "recurring_maintenance" in lowered:
        return "Property Manager"
    if "/admin" in lowered or "/settings" in lowered or "/audit" in lowered or "team_" in lowered or "settings_" in lowered:
        return "Workspace Administrator"
    if any(token in lowered for token in ["/accounting", "/banking", "/tax", "/reports", "money_", "payment_", "deposit"]):
        return "Property Manager"
    return "Property Manager"


def allowed_roles_for_surface(client: str, surface: str) -> list[str]:
    all_authenticated = [
        "Workspace Administrator",
        "Property Manager",
        "Leasing Agent",
        "Maintenance Technician",
        "Owner",
        "Tenant",
    ]
    if client == "Web":
        path = surface.lower()
        if path == "/docs" or path.startswith("/docs/"):
            return all_authenticated + ["Applicant / public user"]
        if path == "/settings/notifications/my-alerts":
            return all_authenticated
        if path == "/settings/security":
            return [
                "Workspace Administrator",
                "Property Manager",
                "Leasing Agent",
                "Maintenance Technician",
            ]
        if path == "/lease-templates":
            return ["Workspace Administrator", "Property Manager", "Leasing Agent"]
        if path == "/messages" or path.startswith("/messages/"):
            return [
                "Workspace Administrator",
                "Property Manager",
                "Leasing Agent",
                "Maintenance Technician",
            ]
        if path == "/notices" or path.startswith("/notices/"):
            return ["Workspace Administrator", "Property Manager", "Leasing Agent"]
        if path == "/scan" or path.startswith("/scan/"):
            return [
                "Workspace Administrator",
                "Property Manager",
                "Leasing Agent",
                "Maintenance Technician",
            ]
        if path == "/profile":
            return ["Leasing Agent", "Maintenance Technician"]
        if path.startswith("/portal"):
            return ["Tenant"]
        if path == "/owner" or path.startswith("/owner/"):
            return ["Owner"]
        if path.startswith("/leasing"):
            return ["Leasing Agent"]
        if path.startswith("/my-work") or path.startswith("/my-schedule") or path.startswith("/assignment-inbox"):
            return ["Maintenance Technician"]
        if path in {"/login", "/register", "/forgot-password", "/reset-password", "/verify-email", "/apply/[token]", "/privacy", "/features", "/welcome", "/sign/[token]"}:
            return ["Applicant / public user"]
        if path.startswith("/superadmin"):
            return ["Superadmin"]
        admin_only_prefixes = (
            "/settings/notifications/team-routing",
            "/settings/notifications/tenant-notices",
            "/settings/accounting",
            "/settings/integrations",
            "/settings",
            "/onboarding",
            "/get-started",
            "/plaid",
            "/admin/users",
        )
        if path.startswith(admin_only_prefixes):
            return ["Workspace Administrator"]
        if path.startswith("/applications"):
            return ["Workspace Administrator"]
        if path.startswith("/admin/audit"):
            return ["Workspace Administrator", "Property Manager"]
    else:
        lowered = surface.lower()
        if "/features/auth/" in f"/{lowered}":
            return ["Applicant / public user"]
        if "features/home/" in lowered or "features/notifications/" in lowered:
            return all_authenticated
        if "features/settings/my_alerts" in lowered or lowered.endswith("features/settings/settings_screen.dart"):
            return all_authenticated
        if "features/settings/team_routing" in lowered or "features/settings/tenant_notices" in lowered:
            return ["Workspace Administrator"]
        if "features/settings/change_password" in lowered:
            return all_authenticated
        if "features/owner_portal/" in lowered or "owner_landing" in lowered:
            return ["Owner"]
        if "features/portal/" in lowered or "tenant_appointments" in lowered:
            return ["Tenant"]
        if "features/leasing/" in lowered:
            return ["Leasing Agent"]
        if "features/technician/" in lowered:
            return ["Maintenance Technician"]
        if "features/scan/" in lowered:
            return [
                "Workspace Administrator",
                "Property Manager",
                "Leasing Agent",
                "Maintenance Technician",
            ]
        if "features/messages/" in lowered:
            return all_authenticated
        if "features/onboarding/" in lowered:
            return ["Workspace Administrator"]
    primary = role_for_surface(client, surface)
    if primary == "Property Manager":
        return ["Workspace Administrator", "Property Manager"]
    return [primary]


def build_screen_and_field_inventory() -> None:
    """Derive an auditable inventory from every current web page and mobile interaction surface."""
    web_pages = sorted((ROOT / "web" / "src" / "routes").rglob("+page.svelte"))
    mobile_files = sorted(
        path
        for path in (ROOT / "mobile" / "lib").rglob("*.dart")
        if any(
            token in path.name
            for token in ("screen.dart", "sheet.dart", "tab.dart", "flow.dart")
        )
    )
    all_screens: list[tuple[str, str, Path]] = []
    for path in web_pages:
        all_screens.append(("Web", route_from_page(path), path))
    for path in mobile_files:
        surface = str(path.relative_to(ROOT / "mobile" / "lib"))
        all_screens.append(("Mobile", surface, path))

    screen_dates = evenly_spaced_weekdays(len(all_screens), date(YEAR, 1, 18))
    for index, ((client, surface, path), planned_day) in enumerate(zip(all_screens, screen_dates), start=1):
        role = role_for_surface(client, surface)
        allowed_roles = allowed_roles_for_surface(client, surface)
        source_file = str(path.relative_to(ROOT))
        screen_id = f"SCR-{index:03d}"
        screen_inventory.append(
            {
                "screen_id": screen_id,
                "client": client,
                "surface": surface,
                "source_file": source_file,
                "primary_role": role,
                "allowed_roles": "; ".join(allowed_roles),
                "planned_date": iso(planned_day),
                "allowed_proof": "Open through normal navigation; use every visible action; save; reload; verify persisted values and linked history.",
                "denied_proof": "Attempt with one adjacent unauthorized persona; fail closed without data disclosure or mutation.",
                "responsive_or_device_proof": "Desktop browser + phone viewport" if client == "Web" else "Real Android target; phone and rotated layout where meaningful",
            }
        )
        for allowed_role in allowed_roles:
            journey_id = f"ROLE-{len(role_journeys) + 1:03d}"
            role_journeys.append(
                {
                    "journey_id": journey_id,
                    "planned_date": iso(planned_day),
                    "persona": allowed_role,
                    "client": client,
                    "screen_id": screen_id,
                    "surface": surface,
                    "expected_access": "Allowed",
                    "actions": "Navigate from the persona shell; exercise every visible tab, filter, list action, detail action, empty/populated/error/loading state, and return path.",
                    "proof": "Navigation entry; loaded state; action result; persisted read-back; audit/history; no data outside assigned relationship/property scope.",
                }
            )
            add_schedule(
                planned_day,
                "Role and screen certification",
                allowed_role,
                client,
                "Normal persona navigation",
                f"Use every permitted action and state on {surface}",
                f"{screen_id}; {journey_id}",
                "",
                "",
                "Sign in as the assigned real-world persona; reach the surface through normal navigation; exercise every visible tab, action, list control, filter, sort, page, detail link, back path, empty/populated/loading/error state, and supported create/edit transition. Reload and re-open from the opposite client when the entity is shared.",
                "The persona can use every allowed action with only its assigned properties/relationships. Hidden or disabled actions explain why; an adjacent unauthorized persona is denied without metadata leakage.",
                "Persona/experience; navigation entry; desktop or device screenshots; result state; persisted read-back; audit/history; authorized and denied network result.",
                "Open the direct URL or deep link with an adjacent unauthorized persona and with stale scope; both fail closed and land safely without mutation.",
            )

    web_control = re.compile(r"<(input|select|textarea)\b([^>]*)>", re.IGNORECASE | re.DOTALL)
    attr = lambda name, text: next(
        (
            match.group(1) or match.group(2) or match.group(3) or ""
            for match in [
                re.search(
                    rf"(?:{re.escape(name)})\s*=\s*(?:\"([^\"]*)\"|'([^']*)'|\{{([^}}]*)\}})",
                    text,
                    re.IGNORECASE,
                )
            ]
            if match
        ),
        "",
    )
    screen_by_source = {row["source_file"]: row for row in screen_inventory}
    seen_fields: set[tuple[str, str, str]] = set()

    for path in sorted((ROOT / "web" / "src").rglob("*.svelte")):
        source_file = str(path.relative_to(ROOT))
        source = path.read_text(encoding="utf-8")
        for ordinal, match in enumerate(web_control.finditer(source), start=1):
            control_type, attributes = match.groups()
            bind_value = ""
            bind_match = re.search(r"bind:(?:value|checked|group)\s*=\s*\{([^}]+)\}", attributes)
            if bind_match:
                bind_value = bind_match.group(1).strip()
            field_key = (
                attr("name", attributes)
                or attr("id", attributes)
                or bind_value
                or attr("placeholder", attributes)
                or f"{control_type}-{ordinal}"
            )
            label_window = source[max(0, match.start() - 500) : match.start()]
            labels = re.findall(r"<label[^>]*>(.*?)</label>", label_window, re.IGNORECASE | re.DOTALL)
            label = re.sub(r"<[^>]+>|\{[^}]+\}", " ", labels[-1]).strip() if labels else ""
            label = re.sub(r"\s+", " ", label) or attr("aria-label", attributes) or attr("placeholder", attributes)
            key = ("Web", source_file, field_key)
            if key in seen_fields:
                continue
            seen_fields.add(key)
            screen = screen_by_source.get(source_file)
            surface = screen["surface"] if screen else source_file
            field_inventory.append(
                {
                    "field_id": "",
                    "client": "Web",
                    "screen_or_component": surface,
                    "source_file": source_file,
                    "source_line": source.count("\n", 0, match.start()) + 1,
                    "control_type": attr("type", attributes) or control_type.lower(),
                    "field_key": field_key,
                    "label_or_hint": label,
                    "role": role_for_surface("Web", surface),
                    "planned_date": "",
                    "value_set": "valid non-default; boundary/format value; blank/null when optional; invalid value when constrained",
                    "required_proof": "Save succeeds; reload shows the exact normalized value; other client reads it where supported; edit persists; validation rejects invalid input without partial mutation.",
                }
            )

    mobile_control = re.compile(
        r"\b(TextFormField|TextField|DropdownButtonFormField(?:<[^>]+>)?|SwitchListTile|CheckboxListTile|RadioListTile|SegmentedButton(?:<[^>]+>)?)\s*\(",
        re.MULTILINE,
    )
    for path in sorted((ROOT / "mobile" / "lib").rglob("*.dart")):
        source_file = str(path.relative_to(ROOT))
        source = path.read_text(encoding="utf-8")
        for ordinal, match in enumerate(mobile_control.finditer(source), start=1):
            chunk = source[match.start() : min(len(source), match.start() + 1800)]
            control_type = match.group(1).split("<", 1)[0]
            def dart_value(pattern: str) -> str:
                found = re.search(pattern, chunk)
                return (found.group(1) or found.group(2) or "").strip() if found else ""

            label = dart_value(r"(?:labelText|hintText|semanticLabel)\s*:\s*(?:'([^']*)'|\"([^\"]*)\")")
            controller = dart_value(r"controller\s*:\s*(?:([A-Za-z0-9_.$]+)|'([^']*)')")
            on_change = dart_value(r"(?:onChanged|onSaved)\s*:\s*(?:([A-Za-z0-9_.$]+)|'([^']*)')")
            field_key = controller or on_change or label or f"{control_type}-{ordinal}"
            key = ("Mobile", source_file, field_key)
            if key in seen_fields:
                continue
            seen_fields.add(key)
            surface = source_file.removeprefix("mobile/lib/")
            field_inventory.append(
                {
                    "field_id": "",
                    "client": "Mobile",
                    "screen_or_component": surface,
                    "source_file": source_file,
                    "source_line": source.count("\n", 0, match.start()) + 1,
                    "control_type": control_type,
                    "field_key": field_key,
                    "label_or_hint": label,
                    "role": role_for_surface("Mobile", surface),
                    "planned_date": "",
                    "value_set": "valid non-default; boundary/format value; blank/null when optional; invalid value when constrained",
                    "required_proof": "Save succeeds; close/reopen shows the exact normalized value; web reads it where supported; edit persists; validation rejects invalid input without partial mutation.",
                }
            )

    field_dates = evenly_spaced_weekdays(len(field_inventory), date(YEAR, 1, 20))
    for index, (row, planned_day) in enumerate(zip(field_inventory, field_dates), start=1):
        row["field_id"] = f"FLD-{index:04d}"
        row["planned_date"] = iso(planned_day)

    fields_by_screen_day: defaultdict[tuple[str, str, str, str], list[dict]] = defaultdict(list)
    for row in field_inventory:
        fields_by_screen_day[
            (row["planned_date"], row["client"], row["role"], row["screen_or_component"])
        ].append(row)
    for (planned_date, client, role, surface), rows in fields_by_screen_day.items():
        ids = "; ".join(row["field_id"] for row in rows)
        keys = "; ".join(row["field_key"] for row in rows[:12])
        if len(rows) > 12:
            keys += f"; plus {len(rows) - 12} more in field-inventory.csv"
        add_schedule(
            date.fromisoformat(planned_date),
            "Field certification",
            role,
            client,
            "Manual field-by-field",
            f"Fill, save, reload, edit, and validate every declared field on {surface}",
            ids,
            "",
            "",
            f"Use the source-derived Field Inventory. Enter non-default realistic values in every control ({keys}); submit; reload or relaunch; compare every value; edit each mutable field; verify the paired client; then run required, format, length, range, boundary, optional-null, and stale-submit checks.",
            f"All {len(rows)} inventoried controls persist exact normalized values, invalid values stay unsaved with clear errors, and hidden/unauthorized fields cannot be altered.",
            "Before/after form screenshots; FLD IDs; API response; reloaded UI; paired-client read-back; database/audit evidence; negative validation result.",
            "Use a duplicate submit and one simulated network interruption; one atomic mutation or none, never partial state.",
        )


def build_role_notification_and_crud_calendar() -> None:
    """Add real-person journeys, full lifecycle operations, and notification delivery proofs."""
    lifecycle_entities = [
        ("Property", "Workspace Administrator", "create; read; edit every field; attach; deactivate/delete-safe; restore or prove irreversible guard"),
        ("Unit", "Property Manager", "create; read; edit every field; duplicate-label rejection; archive/delete-safe"),
        ("Owner", "Workspace Administrator", "create; edit; assign properties; portal invite; revoke; deactivate-safe"),
        ("Tenant", "Property Manager", "create; edit; household roles; invite; revoke; re-invite; historical retention"),
        ("Vendor", "Property Manager", "create; edit; W-9 request; dispatch; rate; deactivate-safe"),
        ("Portfolio", "Workspace Administrator", "create; edit; assign; switch; scope-denial; archive-safe"),
        ("Application", "Leasing Agent", "public submit; scan; edit; status changes; withdraw/reject; duplicate protection"),
        ("Rental listing", "Leasing Agent", "create; edit; publish packet; unpublish; occupied-unit guard"),
        ("Lease template", "Workspace Administrator", "upload; map every field; preview; default; supersede; version retention"),
        ("Lease relationship", "Leasing Agent", "prepare; edit; party roles; issue; sign; renew; transfer; end; cancel planned"),
        ("Agreement", "Leasing Agent", "draft; edit; issue; view; sign; finalize; void; replace; immutable history"),
        ("Addendum", "Leasing Agent", "create; edit; issue; sign; financial effect; supersede"),
        ("Tenant charge", "Property Manager", "create; read; correction/reversal; allocation; no posted-row delete"),
        ("Payment", "Property Manager", "scan/manual create; allocate; reverse; refund; receipt; duplicate protection"),
        ("Expense", "Property Manager", "scan/manual create; edit draft; attach; categorize; void/delete-safe; duplicate invoice"),
        ("Security deposit", "Property Manager", "collect; hold; transfer; apply; refund; reconcile; immutable ledger"),
        ("Bank transaction", "Property Manager", "import; edit allowed metadata; match; unmatch; park; ignore; duplicate import"),
        ("Recurring expense", "Property Manager", "create; edit; generate; pause; resume; retire; idempotent clock advance"),
        ("Loan", "Workspace Administrator", "create; edit; statement attach; amortize; payoff; delete guard"),
        ("Capital asset", "Workspace Administrator", "create; edit; depreciate; dispose; prevent double expense"),
        ("Owner statement", "Property Manager", "generate; review; owner approve/reject; distribute; regenerate/version"),
        ("Work order", "Property Manager", "scan/manual create; edit; triage; assign; accept/decline; start; complete; reopen/cancel"),
        ("Technician assignment", "Maintenance Technician", "accept; labor start/stop; material/receipt; note/photo; complete"),
        ("Recurring maintenance", "Property Manager", "create; edit; generate; pause; resume; retire; out-of-service handling"),
        ("Appointment", "Leasing Agent", "create; edit; invite; remind; confirm; reschedule; cancel; no-show; timezone"),
        ("Inspection", "Property Manager", "create; edit; assign; run every item; photos; complete; freeze; report"),
        ("Turnover", "Property Manager", "open; edit tasks/date; create work; mark ready; close; historical read"),
        ("Tenant notice", "Property Manager", "draft; edit; preview; approve; send; cancel; policy and history"),
        ("Eviction case", "Workspace Administrator", "open; edit; respondent; event; status; close; history; occupancy independence"),
        ("Conversation/message", "Property Manager", "create; participant scope; text; attachment; unread/read; archive"),
        ("Notification preference", "Every authenticated persona", "toggle each channel/event; save; reload; respect preference; required-alert override"),
        ("Team member/assignment", "Workspace Administrator", "invite; activate; job; property scope; accept/decline; edit; revoke/deactivate"),
        ("AI provider", "Workspace Administrator", "configure; invalid credential; test; disconnect; re-auth; secret masking"),
        ("Profile/security", "Every authenticated persona", "edit every profile field; password change; session revoke; re-login"),
        ("Import batch", "Workspace Administrator", "upload; map; validate; commit; duplicate; cancel/reject; audit"),
    ]
    lifecycle_dates = evenly_spaced_weekdays(len(lifecycle_entities), date(YEAR, 1, 25))
    for index, ((entity, role, lifecycle), planned_day) in enumerate(zip(lifecycle_entities, lifecycle_dates), start=1):
        row = {
            "lifecycle_id": f"CRUD-{index:03d}",
            "planned_date": iso(planned_day),
            "entity": entity,
            "role": role,
            "lifecycle": lifecycle,
            "web_proof": "Required",
            "mobile_proof": "Required or verified product gap",
            "scan_or_attachment_proof": "Required when a source document, image, audio, or import exists",
            "delete_policy": "Use a disposable synthetic record; confirm/cancel first, then confirm. Financial/audit/history rows use reversal/void/archive, never destructive deletion.",
            "acceptance": "Each transition persists after reload, appears cross-client, emits audit/history and correct notifications, respects authorization, and stays atomic.",
        }
        crud_lifecycles.append(row)
        add_schedule(
            planned_day,
            "Lifecycle certification",
            role,
            "Web + Mobile",
            "Manual + scan/attachment where supported",
            f"Complete the full create/read/edit/lifecycle/delete-safe pass for {entity}",
            row["lifecycle_id"],
            "",
            "",
            f"Use a dedicated disposable QA record and execute: {lifecycle}. Fill every field referenced by field-inventory.csv. Verify confirmation copy, cancel path, final path, reload, other-client state, history, and role denial.",
            "Every supported transition succeeds exactly once; forbidden destructive actions are guarded; reversible deletion/deactivation works; financial and historical truth is preserved.",
            "Entity before/after; both clients; all FLD IDs; audit/history; notifications; authorized and unauthorized personas; API/DB state.",
            "Cancel the first destructive confirmation, then repeat and confirm; retry the mutation once; no double transition or orphan.",
        )

    scenarios = [
        ("Rent charge posted", "Tenant", "in-app + push/email preference", "tenant balance deep link"),
        ("Rent due in 5 days", "Tenant", "scheduled reminder", "portal payment screen"),
        ("Rent due tomorrow", "Tenant", "scheduled reminder", "portal payment screen"),
        ("Rent received", "Tenant", "receipt notification", "payment receipt"),
        ("Partial payment remaining", "Tenant + Property Manager", "in-app reminder + staff routing", "tenant account"),
        ("Payment failed/NSF", "Tenant + Property Manager", "urgent alert", "payment/reversal detail"),
        ("Late fee posted", "Tenant", "policy-controlled notice", "tenant ledger"),
        ("Past due threshold", "Property Manager", "team route + inbox", "past-due report"),
        ("Security deposit collected", "Tenant + Property Manager", "receipt", "deposit detail"),
        ("Deposit disposition ready", "Tenant + Property Manager", "statement delivery", "deposit disposition"),
        ("Application received", "Leasing Agent", "team routing", "application detail"),
        ("Application status changed", "Applicant", "email/test delivery", "public status/message"),
        ("Showing scheduled", "Applicant + Leasing Agent", "confirmation", "appointment"),
        ("Appointment 24-hour reminder", "Tenant/applicant/staff", "scheduled multi-recipient", "appointment"),
        ("Appointment rescheduled", "All participants", "change notification", "appointment"),
        ("Appointment cancelled", "All participants", "cancellation notification", "appointment history"),
        ("Lease draft ready", "Leasing Agent", "work queue", "lease detail"),
        ("Lease issued for signature", "Tenant/signers", "email + in-app", "signing link"),
        ("Lease signed by one party", "Remaining signers + Leasing Agent", "progress notification", "agreement"),
        ("Lease finalized", "Tenant + Property Manager", "completion", "downloadable lease"),
        ("Lease expires in 90/60/30 days", "Property Manager + Tenant", "three scheduled reminders", "renewal action"),
        ("Move-in upcoming", "Tenant + Property Manager", "checklist reminder", "appointment/lease"),
        ("Move-out notice received", "Property Manager", "team route", "lease ending"),
        ("Move-out inspection reminder", "Tenant + Technician", "scheduled reminder", "inspection"),
        ("Maintenance request created", "Tenant + Property Manager", "receipt + team route", "work order"),
        ("Work order assigned", "Technician/vendor", "push + assignment inbox", "assignment"),
        ("Work order accepted/declined", "Property Manager", "status notification", "work order"),
        ("Technician en route", "Tenant", "in-app/push", "work order"),
        ("Work order completed", "Tenant + Property Manager", "completion + rating request", "work order"),
        ("Recurring maintenance generated", "Property Manager", "team route", "work order"),
        ("Inspection assigned", "Technician", "push + schedule", "inspection"),
        ("Inspection completed", "Property Manager + Tenant when shareable", "report-ready", "inspection report"),
        ("New conversation message", "Tenant/Owner/Staff", "in-app + push", "conversation"),
        ("Message attachment", "Recipient", "notification with safe deep link", "conversation attachment"),
        ("Owner statement ready", "Owner", "in-app + email", "statement"),
        ("Owner approval requested", "Owner", "approval alert", "approval"),
        ("Owner approval accepted/rejected", "Property Manager", "status notification", "approval"),
        ("Bank import/match needs review", "Property Manager", "staff alert", "banking review"),
        ("Expense scan extraction complete/failed", "Uploader", "worker result", "scan draft"),
        ("Lease scan extraction complete/failed", "Uploader", "worker result", "scan draft"),
        ("Import batch complete/failed", "Workspace Administrator", "worker result", "import detail"),
        ("Team invitation", "Invited staff", "email/test delivery", "activation"),
        ("Assignment offered/accepted/declined", "Staff + manager", "in-app + push", "assignment inbox"),
        ("Role/scope changed", "Affected staff", "security alert + access refresh", "safe landing"),
        ("Password changed", "Account owner", "security alert", "security page"),
        ("Session revoked", "Account owner", "security alert", "login"),
        ("Provider delivery failure/retry", "Workspace Administrator", "admin alert", "notification delivery log"),
        ("Tenant notice generated/sent", "Tenant + notice operator", "policy delivery", "notice history"),
        ("Legal case event due", "Property Manager", "scheduled staff alert", "case timeline"),
        ("Year-end reports ready", "Owner + Property Manager", "packet-ready", "report/statement"),
    ]
    scenario_dates = evenly_spaced_weekdays(len(scenarios), date(YEAR, 1, 22))
    for index, ((event, recipients, channels, destination), planned_day) in enumerate(zip(scenarios, scenario_dates), start=1):
        notification_scenarios.append(
            {
                "notification_id": f"NTF-{index:03d}",
                "planned_date": iso(planned_day),
                "trigger_event": event,
                "recipients": recipients,
                "channels_or_route": channels,
                "deep_link_destination": destination,
                "preference_test": "Enable then disable each optional channel; required/security delivery follows policy; unrelated roles receive nothing.",
                "delivery_test": "Verify generated row/outbox, local/test provider outcome, retry/dead-letter behavior, unread badge, mark read, deep link, and cross-device state.",
                "privacy_test": "Title/body/lock-screen preview contains no excess tenant, money, legal, credential, or cross-property data.",
            }
        )
        add_schedule(
            planned_day,
            "Notification certification",
            recipients,
            "Web + Mobile",
            "System event + preference/manual verification",
            f"Trigger and prove notification: {event}",
            f"NTF-{index:03d}",
            "",
            "",
            f"Set each recipient's preferences; trigger the real business event; inspect routing and recipient scope; verify unread badge, list item, safe preview, {destination} deep link, mark-read sync, configured channel outcome, provider failure/retry, and duplicate suppression.",
            "Exactly the intended recipients receive one correctly routed notification; disabled optional channels stay silent; deep link opens authorized context; read state synchronizes.",
            "Trigger record; routing decision; outbox/delivery status; web/mobile inbox; lock-screen-safe copy; deep-link target; audit/provider retry evidence.",
            "Re-run the worker and deliver a duplicate provider callback; no duplicate user-visible event. Attempt the deep link as an unauthorized adjacent role.",
        )


def build_nonfinancial_surface_calendar() -> None:
    surfaces = [
        ("Authentication", "Register, verify email, login, logout", "/register; /verify-email; /login; /logout", "Auth screens", "No", "Create a second staff identity; verify validation, refresh, logout, back navigation, and app-scoped cookies/tokens."),
        ("Authentication", "Forgot/reset password and change password", "/forgot-password; /reset-password; /settings/security", "Forgot/reset/change password screens", "No", "Request reset for known/unknown user; use valid/invalid token; change password; revoke other sessions."),
        ("Setup", "Choose live setup and Getting Started", "/choose-setup; /setting-up; /get-started; /onboarding", "Onboarding choice/live setup/getting started", "Lease", "Resume saved setup, scan one lease, complete one manual setup task, and verify go-live state."),
        ("Rentals", "Property create/edit/detail/documents/history", "/properties; /properties/[id]", "Properties list/form/detail", "Attachment / candidate", "Create, edit, deactivate-safe, filter, sort, page, attach documents, and inspect history."),
        ("Rentals", "Unit create/edit/Command Center", "/units; /units/[id]", "Units list/form/Command Center", "Lease context", "Create/edit unit, traverse Summary/Listing/Tenant & lease/Money/Maintenance/Documents & history, and restore navigation."),
        ("Rentals", "Owner entity create/edit/detail/portal access", "/owners; /owners/[id]; /owner", "Owners list/form/detail/owner landing", "Attachment / candidate", "Create/edit owner, assign properties, invite owner, inspect statements, approvals, messages, security, and scoped properties."),
        ("Rentals", "Tenant create/edit/detail/access lifecycle", "/tenants; /tenants/[id]", "Tenants list/detail", "Lease/Application", "Create/edit tenant, household membership, portal invite/revoke/reinvite, security link, current/historical lease."),
        ("Rentals", "Vendor create/edit/W-9/detail", "/vendors; /vendors/[id]", "Vendors list/form/detail", "Attachment / candidate", "Create/edit vendor including address/tax/website, request W-9, filter, dispatch, rate, deactivate-safe."),
        ("Leasing", "Application public form/intake/detail/pipeline", "/apply/[token]; /applications; /applications/[id]; /leasing/pipeline", "Applications list/detail/leasing", "Application", "Submit public and scanned applications; manually create/status one on each client; verify unit scope and fair-housing guard."),
        ("Leasing", "Rental listing and Zillow manual handoff", "/leasing/rentals; /units/[id]?tab=listing", "Unit Listing tab", "Attachment only", "Create/edit listing, generate packet, save external URLs, copy content, and verify occupied-unit guard."),
        ("Leasing", "Lease template upload/designer/default", "/lease-templates", "Gap probe: no full mobile designer expected", "Lease/template PDF", "Upload template, map fields, preview, activate default, and prove issued packets freeze template version."),
        ("Leasing", "Prepare move-in and lease agreement draft", "/leases; /leases/[id]; Unit Tenant & lease", "Lease sheets/detail", "Lease", "Prepare planned relationship atomically; edit draft; add co-tenant/guarantor/occupant; cancel a planned relationship."),
        ("Leasing", "Issue, sign, void, replace agreement", "/sign/[token]; /leases/[id]", "Lease detail and public signer flow", "Lease PDF", "Issue, view, sign, finalize, void mistaken packet, replace, and verify immutable artifacts/status."),
        ("Leasing", "Addendum and financial effect", "/leases/[id]", "Addendum action sheets", "Addendum attachment / candidate", "Create pet addendum with recurring charge, issue/sign, verify effective billing, then supersede on renewal."),
        ("Leasing", "Renewal, month-to-month, successor agreement", "/units/[id]; /leases/[id]", "Lease detail/action sheets", "Lease/addendum", "Create future renewal, preserve current governing agreement, test month-to-month and successor timing."),
        ("Leasing", "Ending disposition, possession return, transfer", "/units/[id]; /leases/[id]", "Ending/return/transfer sheets", "Notice/inspection", "Record notice, plan move-out, return possession, transfer U008 to U040, and preserve historical accounts."),
        ("Money", "Tenant charge, receipt, allocation, correction", "/leases/[id]; /tenant-accounts/...; /accounting", "Lease ledger/payment screens", "Payment", "Create charge/receipt on both clients, allocate, reverse/refund/adjust without editing posted rows, and reconcile."),
        ("Money", "Expense create/edit/scan/receipt detail", "/accounting; /accounting/expenses/[id]", "Money/expense form/detail/receipt viewer", "Expense", "Scan and manually create expenses on both clients; edit draft facts; verify line items, attachment, allocation, and Schedule E."),
        ("Money", "Security deposit register/disposition", "/deposits; /deposits/[id]", "Deposits list/detail", "Payment/receipt candidate", "Collect, hold, apply, transfer, refund, and reconcile trust cash/liability without treating it as income."),
        ("Money", "Banking connect/import/review/match", "/banking; /plaid/auth", "Banking screen", "Bank statement/import", "Exercise not-configured state, manual connection/import, matching, duplicates, parked rows, and reconciliation."),
        ("Money", "Recurring expenses and scheduled finance", "/accounting; property Money", "Money/property screens", "Invoice", "Create/edit/pause recurring expense; advance clock; prove one generated expense per period and idempotent rerun."),
        ("Money", "Property loan and amortization", "/properties/[id]", "Property loan form/detail", "Mortgage statement", "Create/edit loan on both clients, match statements, verify amortization, principal/interest, and payoff."),
        ("Money", "Capital asset, depreciation, disposition", "/properties/[id]; /tax; /accounting/year-end", "Property capital asset/disposition", "Invoice/closing packet", "Capitalize, depreciate, dispose, and prove no duplicate expense/basis and guarded preconditions."),
        ("Money", "Owner statements/distributions/approvals", "/owner/statements; /owner/approvals; /owners-report", "Owner landing/reports", "Statement attachment", "Generate statement, owner review/approval, distribution, rejection, PDF open, and cross-owner isolation."),
        ("Operations", "Work order create/edit/timeline/responsibility", "/maintenance; /maintenance/[id]; /my-work", "Work orders/detail/technician", "WorkOrder", "Scan and manually create work orders on both clients; edit, assign, status, notes, responsibility, and timeline."),
        ("Operations", "Vendor dispatch, completion, rating", "/maintenance/[id]; /vendors/[id]", "Dispatch/rating sheets", "Invoice/dispatch response", "Dispatch, accept/decline, complete from inbound response, rate vendor, and preserve history."),
        ("Operations", "Technician labor/material entries", "/my-work/[id]", "Technician assignment detail", "Receipt/photo attachment", "Start/stop labor, add material/receipt, complete assignment, and prove totals/role scoping."),
        ("Operations", "Recurring maintenance", "/maintenance/recurring", "Recurring maintenance screens", "Service agreement", "Create/edit/pause schedule; advance clock; generate work order once; handle out-of-service unit."),
        ("Operations", "Appointment create/edit/confirm/cancel", "/appointments; /appointments/[id]; /my-schedule", "Appointments and tenant appointments", "Attachment only", "Create on web/mobile, tenant confirm/cancel, staff reschedule, filters, reminders, and timezone boundaries."),
        ("Operations", "Inspection create/run/complete/report", "/maintenance/inspections/[id]", "Inspection list/new/run", "Inspection report/photos", "Create on both clients, run checklist/photos, edit before complete, freeze after complete, and open report."),
        ("Operations", "Turnover workspace and make-ready", "/units/[id]", "Unit Turnover tab", "Inspection/invoices", "Open turnover, create tasks/work orders, track ready date, close, and verify vacancy/marketing independence."),
        ("Operations", "Notices and tenant notice policies", "/notices; /settings/notifications/tenant-notices", "Notices/settings", "Notice source", "Create/edit preview/send synthetic notice; policy preferences; automation candidate; no blank/legal surprise."),
        ("Operations", "Eviction case and durable events", "Lease/Unit legal surfaces", "Gap probe or mobile legal surface", "Legal document", "Open case, add respondent/event, advance status, preserve source, and prove legal state does not mutate occupancy."),
        ("Communications", "Conversations and messages", "/messages; /messages/[id]; /owner/messages; /portal/messages", "Messages list/detail", "Attachment", "Create landlord-tenant, owner, and team conversations; send text/attachment; unread/read; deep link; role isolation."),
        ("Communications", "Notifications, preferences, routing, alerts", "/portal/notifications; /settings/notifications/*", "Notifications inbox/settings/routing/alerts", "No", "Change preferences, route by team/property/event, mark read, deep link, retry provider failure, and verify safe local delivery."),
        ("Communications", "Voice intake and assistant actions", "/ai", "Tell Me / voice / AI tab", "Audio capture", "Record maintenance/payment/expense intents, review proposed action, confirm/cancel, and verify no save before confirmation."),
        ("Reporting", "Dashboard, analytics, daily briefing, Q&A", "/; /analytics; /ai", "Home/Insights/Briefing/Q&A", "No", "Check empty/populated/current/past-due/vacancy states, ask grounded questions, compare counts and money to DB-side reports."),
        ("Reporting", "Accounting and all operational reports", "/accounting; /reports; /reports/[report]", "Money/owner reports; web-first full reports", "No", "Run P&L, GL, cash flow, rent roll, ledger, delinquency, occupancy, expirations, deposits, 1099, work orders, and filters."),
        ("Reporting", "Tax and year-end packet", "/tax; /accounting/year-end", "Owner reports / PDF open", "Tax statements/invoices", "Run Schedule E, depreciation, vendor 1099 review, owner packets, and date/property/category filters."),
        ("Administration", "Team invite, roles, scoped assignments", "/settings; /activate-team; /assignment-inbox", "Team/settings/assignment inbox", "Invitation attachment only", "Invite each role, activate, assign property scope, accept/decline work, change/revoke access, and prove authorization."),
        ("Administration", "Profile/company/account/integrations/AI", "/profile; /settings; /settings/accounting; /settings/integrations/ai", "Settings/AI provider", "Provider statement candidate", "Edit profile/company, provider settings, accounting connection states, invalid credentials, disconnect, and re-auth."),
        ("Administration", "Audit, activity, admin users, engine health", "/audit; /admin/audit; /admin/users; /superadmin/engine", "Activity/Admin surfaces or explicit gap proof", "No", "Filter/search/page audit DB-side; change roles; deactivate user; inspect worker/provider status; prove access guards."),
        ("Tenant portal", "Tenant home/account/payments/lease", "/portal; /portal/account; /portal/payments; /portal/lease", "Tenant portal screens", "Payment/lease", "View balance/ledger/receipt, pay in test mode, view/sign/download lease, profile/account, and multiple/historical lease behavior."),
        ("Tenant portal", "Tenant maintenance/messages/appointments/preferences/security", "/portal/maintenance; /portal/messages; /portal/appointments; /portal/notifications; /portal/security", "Tenant portal screens", "WorkOrder/attachment", "Submit request with photo, message, confirm appointment, change preferences/password, logout all sessions, and prove tenant-only scope."),
        ("Owner portal", "Owner landing/properties/statements/approvals/messages/security", "/owner/*", "Owner portal screens", "Statement attachment", "Walk every owner portal surface on both clients; approve/reject; open PDF; message; change password; prove property scope."),
        ("Public/help", "Docs, privacy, public apply/sign, deep links", "/docs; /privacy; /apply/[token]; /sign/[token]", "Public/auth/deep-link routes", "Application/lease", "Open help/privacy, invalid/expired public tokens, valid apply/sign links, Android app links, and browser/mobile back behavior."),
    ]

    proof_dates = []
    current = date(YEAR, 1, 11)
    while current <= END:
        if current.weekday() < 5:
            proof_dates.append(current)
        current += timedelta(days=1)
    total_slots = len(surfaces) * 4
    assigned_dates = [
        proof_dates[round(index * (len(proof_dates) - 1) / (total_slots - 1))]
        for index in range(total_slots)
    ]

    for idx, (domain, surface, web_route, mobile_surface, scan_path, action) in enumerate(surfaces):
        base = idx * 4
        dates = assigned_dates[base : base + 4]
        mobile_gap = "Gap" in mobile_surface or "web-first" in mobile_surface.lower()
        coverage.append(
            {
                "coverage_id": f"COV-{idx + 1:03d}",
                "domain": domain,
                "surface": surface,
                "web_route": web_route,
                "mobile_surface": mobile_surface,
                "scan_or_attachment_path": scan_path,
                "manual_web_date": iso(dates[0]),
                "manual_mobile_date": iso(dates[1]),
                "scan_web_date": iso(dates[2]) if scan_path != "No" else "N/A",
                "scan_mobile_date": iso(dates[3]) if scan_path != "No" else "N/A",
                "mobile_gap_policy": "Prove current gap and capture bug during execution; do not waive" if mobile_gap else "Expected supported; failure is a bug",
                "acceptance_action": action,
                "required_proof": "Real UI state, submitted result, persisted read-back, other-client read-back, audit/history, role guard, and money tie-out when applicable.",
            }
        )
        variants = [
            (dates[0], "Web", "Manual", "Manual web pass"),
            (dates[1], "Mobile", "Manual", "Manual mobile pass"),
        ]
        if scan_path != "No":
            variants.extend(
                [
                    (dates[2], "Web", "Scan/attachment", "Scan web pass"),
                    (dates[3], "Mobile", "Scan/attachment", "Scan mobile pass"),
                ]
            )
        for proof_day, client, mode, label in variants:
            add_schedule(
                proof_day,
                "Surface coverage",
                "Role appropriate to surface",
                client,
                mode,
                f"{label}: {surface}",
                f"COV-{idx + 1:03d}",
                "",
                "",
                action,
                "The supported workflow completes and persists correctly; if the capability is absent, the exact gap is proven and queued rather than silently skipped.",
                "Real UI screenshot/state; submitted values; persisted read-back; cross-client read-back; audit/role/access evidence.",
                "Use one invalid value, stale-state retry, or duplicate submission relevant to the form; no partial or duplicate mutation.",
            )


def build_monthly_controls() -> list[dict]:
    controls = []
    cumulative = defaultdict(int)
    opening_types = {"OpeningBalance"}
    for month in range(1, 13):
        start = date(YEAR, month, 1)
        end = month_end(start)
        rows = [
            event
            for event in financial_events
            if start <= date.fromisoformat(event.effective_date) <= end and event.event_type not in opening_types
        ]
        monthly = {
            "income_cents": sum(row.income_cents for row in rows),
            "expense_cents": sum(row.expense_cents for row in rows),
            "ar_delta_cents": sum(row.ar_delta_cents for row in rows),
            "operating_cash_delta_cents": sum(row.operating_cash_delta_cents for row in rows),
            "deposit_cash_delta_cents": sum(row.deposit_cash_delta_cents for row in rows),
            "reserve_cash_delta_cents": sum(row.reserve_cash_delta_cents for row in rows),
            "deposit_liability_delta_cents": sum(row.deposit_liability_delta_cents for row in rows),
            "loan_liability_delta_cents": sum(row.loan_liability_delta_cents for row in rows),
            "owner_distribution_cents": sum(row.owner_distribution_cents for row in rows),
            "capital_asset_delta_cents": sum(row.capital_asset_delta_cents for row in rows),
            "accumulated_depreciation_delta_cents": sum(row.accumulated_depreciation_delta_cents for row in rows),
        }
        for key, value in monthly.items():
            cumulative[key] += value
        controls.append(
            {
                "month": start.strftime("%Y-%m"),
                "event_count": len(rows),
                **monthly,
                "net_operating_income_cents": monthly["income_cents"] - monthly["expense_cents"],
                "cumulative_income_cents": cumulative["income_cents"],
                "cumulative_expense_cents": cumulative["expense_cents"],
                "cumulative_net_operating_income_cents": cumulative["income_cents"] - cumulative["expense_cents"],
            }
        )
    return controls


def validate(monthly_controls: list[dict]) -> dict:
    debit = sum(row["debit_cents"] for row in journal)
    credit = sum(row["credit_cents"] for row in journal)
    if debit != credit:
        raise ValueError(f"Journal is not balanced: {debit} != {credit}")
    if len(properties) != 30 or len(units) != 40 or len(leases) != 45:
        raise ValueError("Portfolio control counts changed unexpectedly")
    if len({item.asset_id for item in assets}) != len(assets):
        raise ValueError("Duplicate scan asset ID")
    if len({item.run_id for item in schedules}) != len(schedules):
        raise ValueError("Duplicate run ID")
    if len({item.event_id for item in financial_events}) != len(financial_events):
        raise ValueError("Duplicate financial event ID")
    if not screen_inventory or not field_inventory or not role_journeys:
        raise ValueError("Source-derived screen, field, and role inventories must not be empty")
    journey_screen_ids = {row["screen_id"] for row in role_journeys}
    inventory_screen_ids = {row["screen_id"] for row in screen_inventory}
    if journey_screen_ids != inventory_screen_ids:
        raise ValueError("Every inventoried screen must have at least one positive role journey")
    expected_journeys = sum(len(row["allowed_roles"].split("; ")) for row in screen_inventory)
    if len(role_journeys) != expected_journeys:
        raise ValueError("Every allowed persona/screen pair must have one positive role journey")
    if len({row["field_id"] for row in field_inventory}) != len(field_inventory):
        raise ValueError("Duplicate field inventory ID")
    if len({row["screen_id"] for row in screen_inventory}) != len(screen_inventory):
        raise ValueError("Duplicate screen inventory ID")
    out_of_year = [item.run_id for item in schedules if not (START <= date.fromisoformat(item.simulated_clock) <= END)]
    if out_of_year:
        raise ValueError(f"Run rows outside 2027: {out_of_year[:5]}")
    run_dates = {date.fromisoformat(item.simulated_clock) for item in schedules}
    calendar_weeks: defaultdict[tuple[int, int], set[date]] = defaultdict(set)
    run_weeks: defaultdict[tuple[int, int], set[date]] = defaultdict(set)
    current = START
    while current <= END:
        week_key = (current.isocalendar().year, current.isocalendar().week)
        calendar_weeks[week_key].add(current)
        if current in run_dates:
            run_weeks[week_key].add(current)
        current += timedelta(days=1)
    sparse_weeks = {
        key: len(run_weeks[key])
        for key, days in calendar_weeks.items()
        if len(days) >= 2 and len(run_weeks[key]) < 2
    }
    if sparse_weeks:
        raise ValueError(f"Each calendar week needs at least two distinct workdays: {sparse_weeks}")
    missing_asset_refs = []
    asset_ids = {item.asset_id for item in assets}
    for schedule in schedules:
        for ref in [part.strip() for part in schedule.asset_refs.split(";") if part.strip()]:
            if ref not in asset_ids:
                missing_asset_refs.append((schedule.run_id, ref))
    if missing_asset_refs:
        raise ValueError(f"Missing asset refs: {missing_asset_refs[:5]}")
    scan_first = sum(1 for item in schedules if "Scan" in item.entry_mode or "scan" in item.entry_mode)
    return {
        "properties": len(properties),
        "units": len(units),
        "leases": len(leases),
        "opening_occupied": sum(1 for item in units if item.opening_state == "Occupied"),
        "opening_vacant": sum(1 for item in units if item.opening_state == "Vacant"),
        "scan_assets": len(assets),
        "runbook_rows": len(schedules),
        "financial_events": len(financial_events),
        "journal_lines": len(journal),
        "coverage_surfaces": len(coverage),
        "inventoried_screens": len(screen_inventory),
        "inventoried_fields": len(field_inventory),
        "role_journeys": len(role_journeys),
        "crud_lifecycles": len(crud_lifecycles),
        "notification_scenarios": len(notification_scenarios),
        "distinct_run_days": len({item.simulated_clock for item in schedules}),
        "scan_or_attachment_run_rows": scan_first,
        "journal_debits_cents": debit,
        "journal_credits_cents": credit,
        "year_income_cents": sum(row["income_cents"] for row in monthly_controls),
        "year_expense_cents": sum(row["expense_cents"] for row in monthly_controls),
        "year_noi_cents": sum(row["net_operating_income_cents"] for row in monthly_controls),
    }


def row_table(headers: list[str], rows: list[list[str]], css_class: str = "") -> str:
    head = "".join(f"<th>{html.escape(header)}</th>" for header in headers)
    body = "".join(
        "<tr>" + "".join(f"<td>{cell}</td>" for cell in row) + "</tr>"
        for row in rows
    )
    return f'<div class="table-wrap"><table class="{css_class}"><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table></div>'


def render_html(monthly_controls: list[dict], stats: dict) -> str:
    portfolio_rows = []
    for prop in properties:
        prop_units = [unit for unit in units if unit.property_id == prop.property_id]
        portfolio_rows.append(
            [
                html.escape(prop.property_id),
                html.escape(prop.property_name),
                html.escape(prop.rental_structure),
                html.escape(prop.address),
                html.escape(", ".join(unit.unit_id + " " + unit.label for unit in prop_units)),
                html.escape(prop.owner_entity_name),
                dollars(prop.original_cost_cents),
                dollars(prop.opening_loan_balance_cents),
            ]
        )
    monthly_rows = [
        [
            html.escape(row["month"]),
            str(row["event_count"]),
            dollars(row["income_cents"]),
            dollars(row["expense_cents"]),
            dollars(row["net_operating_income_cents"]),
            dollars(row["operating_cash_delta_cents"]),
            dollars(row["ar_delta_cents"]),
            dollars(row["deposit_liability_delta_cents"]),
        ]
        for row in monthly_controls
    ]

    schedule_sections = []
    schedules_by_month: defaultdict[str, list[Schedule]] = defaultdict(list)
    for item in sorted(schedules, key=lambda row: (row.simulated_clock, row.sequence)):
        schedules_by_month[item.simulated_clock[:7]].append(item)
    for month_key, month_rows in schedules_by_month.items():
        title = datetime.strptime(month_key, "%Y-%m").strftime("%B %Y")
        rows = []
        for item in month_rows:
            rows.append(
                [
                    f'<code>{html.escape(item.run_id)}</code><br>{html.escape(item.simulated_clock)}',
                    f"<strong>{html.escape(item.workflow)}</strong><br><span class='muted'>{html.escape(item.phase)} · {html.escape(item.role)}</span>",
                    f"{html.escape(item.client)}<br>{html.escape(item.entry_mode)}",
                    html.escape(item.entity_refs),
                    html.escape(item.asset_refs),
                    html.escape(item.financial_refs),
                    html.escape(item.exact_actions),
                    html.escape(item.expected_result),
                    html.escape(item.negative_or_retry_check),
                ]
            )
        schedule_sections.append(
            f"<details id='{month_key}'><summary>{title} — {len(month_rows)} run rows</summary>"
            + row_table(
                ["Run/date", "Workflow", "Client/mode", "Entities", "Scans", "Money refs", "Exact actions", "Expected", "Retry/negative"],
                rows,
                "schedule-table",
            )
            + "</details>"
        )

    coverage_rows = []
    for row in coverage:
        coverage_rows.append(
            [
                f"<code>{html.escape(row['coverage_id'])}</code>",
                html.escape(row["domain"]),
                html.escape(row["surface"]),
                html.escape(row["web_route"]),
                html.escape(row["mobile_surface"]),
                html.escape(row["scan_or_attachment_path"]),
                f"W {html.escape(row['manual_web_date'])}<br>M {html.escape(row['manual_mobile_date'])}<br>SW {html.escape(row['scan_web_date'])}<br>SM {html.escape(row['scan_mobile_date'])}",
                html.escape(row["mobile_gap_policy"]),
                html.escape(row["acceptance_action"]),
            ]
        )
    screen_rows = [
        [
            f"<code>{html.escape(row['screen_id'])}</code>",
            html.escape(row["planned_date"]),
            html.escape(row["client"]),
            html.escape(row["surface"]),
            html.escape(row["primary_role"]),
            html.escape(row["allowed_roles"]),
            html.escape(row["source_file"]),
            html.escape(row["allowed_proof"]),
            html.escape(row["denied_proof"]),
        ]
        for row in screen_inventory
    ]
    field_rows = [
        [
            f"<code>{html.escape(row['field_id'])}</code>",
            html.escape(row["planned_date"]),
            html.escape(row["client"]),
            html.escape(row["screen_or_component"]),
            html.escape(row["control_type"]),
            html.escape(row["field_key"]),
            html.escape(row["label_or_hint"]),
            html.escape(row["role"]),
            f"{html.escape(row['source_file'])}:{row['source_line']}",
            html.escape(row["required_proof"]),
        ]
        for row in field_inventory
    ]
    lifecycle_rows = [
        [
            f"<code>{html.escape(row['lifecycle_id'])}</code>",
            html.escape(row["planned_date"]),
            html.escape(row["entity"]),
            html.escape(row["role"]),
            html.escape(row["lifecycle"]),
            html.escape(row["delete_policy"]),
            html.escape(row["acceptance"]),
        ]
        for row in crud_lifecycles
    ]
    notification_rows = [
        [
            f"<code>{html.escape(row['notification_id'])}</code>",
            html.escape(row["planned_date"]),
            html.escape(row["trigger_event"]),
            html.escape(row["recipients"]),
            html.escape(row["channels_or_route"]),
            html.escape(row["deep_link_destination"]),
            html.escape(row["preference_test"]),
            html.escape(row["delivery_test"]),
            html.escape(row["privacy_test"]),
        ]
        for row in notification_scenarios
    ]

    asset_family_counts = defaultdict(int)
    asset_expectation_counts = defaultdict(int)
    for item in assets:
        asset_family_counts[item.document_family] += 1
        asset_expectation_counts[item.intake_expectation] += 1
    family_rows = [[html.escape(key), str(value)] for key, value in sorted(asset_family_counts.items(), key=lambda row: (-row[1], row[0]))]

    return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<link rel="icon" href="data:,">
<title>TSK-749 — 2027 Scan-First Rental Company Simulation</title>
<style>
:root{{--ink:#15202b;--muted:#5b6875;--paper:#f6f2e9;--card:#fffdfa;--line:#d9d1c4;--blue:#173f5f;--teal:#2a7f78;--gold:#d49b2a;--red:#a84435;}}
*{{box-sizing:border-box}} body{{margin:0;background:var(--paper);color:var(--ink);font:15px/1.45 Inter,ui-sans-serif,system-ui,-apple-system,sans-serif}}
header{{background:linear-gradient(135deg,#102c3d,#1f5a65);color:white;padding:48px 5vw 40px}} header h1{{max-width:1100px;margin:0;font:700 clamp(32px,5vw,66px)/1.02 Georgia,serif}}
header p{{max-width:920px;font-size:18px;color:#dcebed}} main{{max-width:1500px;margin:auto;padding:32px 3vw 80px}}
h2{{font:700 32px/1.1 Georgia,serif;margin-top:48px}} h3{{margin-top:32px}} code{{font-size:12px}} .muted{{color:var(--muted)}}
.callout{{border-left:5px solid var(--gold);background:#fff6dc;padding:18px 22px;margin:24px 0}} .grid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:14px}}
.card{{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:18px;box-shadow:0 4px 16px #4d402012}} .kpi{{font:700 30px/1 Georgia,serif;color:var(--blue)}} .label{{color:var(--muted);margin-top:6px}}
.steps{{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:16px}} .step b{{display:block;color:var(--teal);font-size:13px;text-transform:uppercase;letter-spacing:.08em}}
.table-wrap{{overflow:auto;border:1px solid var(--line);border-radius:10px;background:white;margin:12px 0 26px}} table{{border-collapse:collapse;width:100%;min-width:900px}} th{{position:sticky;top:0;background:#173f5f;color:white;text-align:left;padding:10px;font-size:12px;z-index:1}} td{{vertical-align:top;border-bottom:1px solid #ebe5db;padding:9px;max-width:390px}} tr:nth-child(even){{background:#faf7f1}}
.schedule-table{{min-width:2200px}} .schedule-table td:nth-child(7),.schedule-table td:nth-child(8),.schedule-table td:nth-child(9){{min-width:330px}}
details{{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:0 14px;margin:12px 0}} summary{{cursor:pointer;font-weight:700;padding:16px;color:var(--blue)}}
.pill{{display:inline-block;border:1px solid var(--line);border-radius:99px;padding:4px 9px;margin:2px;background:white}} nav{{position:sticky;top:0;background:#f6f2e9ee;backdrop-filter:blur(8px);z-index:4;padding:10px 3vw;border-bottom:1px solid var(--line);overflow:auto;white-space:nowrap}} nav a{{color:var(--blue);margin-right:18px;text-decoration:none;font-weight:650}}
ul.checks li{{margin:8px 0}} .danger{{color:var(--red);font-weight:700}} footer{{margin-top:60px;color:var(--muted);border-top:1px solid var(--line);padding-top:20px}}
@media(max-width:700px){{header{{padding:32px 20px}} main{{padding:20px 14px 60px}} h2{{font-size:26px}}}}
</style>
</head>
<body>
<header>
  <div class="pill">TSK-749</div><div class="pill">Simulation year 2027</div><div class="pill">Synthetic Columbus portfolio</div>
  <h1>Run a rental company for one year—day by day.</h1>
  <p>This is the execution contract for a scan-first, manual-parity, web-and-mobile acceptance year. It fixes the actors, dates, documents, amounts, expected states, and independent accounting totals before anyone touches the simulation clock.</p>
</header>
<nav><a href="#contract">Contract</a><a href="#company">Company</a><a href="#run-day">Run one day</a><a href="#calendar">Calendar</a><a href="#scans">Scans</a><a href="#coverage">Coverage</a><a href="#screens">Screens & roles</a><a href="#fields">Fields</a><a href="#lifecycles">CRUD</a><a href="#notifications">Notifications</a><a href="#money">Money</a><a href="#close">Year-end</a></nav>
<main>
<section id="contract">
<h2>1. Non-negotiable execution contract</h2>
<div class="callout"><strong>Do not improvise amounts or dates.</strong> The HTML explains the run; the CSVs and workbook are the oracle. If the UI disagrees, stop that day, preserve evidence, and classify the variance before advancing the clock.</div>
<div class="grid">
  <div class="card"><div class="kpi">{stats['properties']}</div><div class="label">properties</div></div>
  <div class="card"><div class="kpi">{stats['units']}</div><div class="label">rentable units</div></div>
  <div class="card"><div class="kpi">{stats['opening_occupied']}/{stats['opening_vacant']}</div><div class="label">opening occupied / vacant</div></div>
  <div class="card"><div class="kpi">{stats['scan_assets']}</div><div class="label">planned scan assets</div></div>
  <div class="card"><div class="kpi">{stats['runbook_rows']}</div><div class="label">dated run rows</div></div>
  <div class="card"><div class="kpi">{stats['financial_events']}</div><div class="label">financial events</div></div>
  <div class="card"><div class="kpi">{stats['coverage_surfaces']}</div><div class="label">surface families × parity passes</div></div>
  <div class="card"><div class="kpi">{stats['inventoried_screens']}</div><div class="label">current web/mobile screens</div></div>
  <div class="card"><div class="kpi">{stats['inventoried_fields']}</div><div class="label">source-declared form controls</div></div>
  <div class="card"><div class="kpi">{stats['notification_scenarios']}</div><div class="label">notification/reminder triggers</div></div>
  <div class="card"><div class="kpi">{stats['crud_lifecycles']}</div><div class="label">full entity lifecycles</div></div>
  <div class="card"><div class="kpi">{stats['distinct_run_days']}</div><div class="label">distinct simulated workdays</div></div>
  <div class="card"><div class="kpi">{dollars(stats['year_noi_cents'])}</div><div class="label">oracle 2027 net operating result</div></div>
</div>
<h3>What “both scans and manual entry on web and mobile” means</h3>
<ul class="checks">
<li>Every supported direct scan target—Lease, Expense, Payment, Application, and WorkOrder—gets repeated PDF/photo intake on web and mobile.</li>
<li>Every create/edit/lifecycle surface gets a manual web pass and manual mobile pass with distinct real simulation events, so duplicate records are not manufactured merely for parity.</li>
<li>Documents without a supported typed scan target are still created and attached. The operator probes the scan front door; an unsupported or misrouted result is recorded as a product gap, never silently treated as coverage.</li>
<li>Full desktop-only surfaces such as lease-template design, bulk import, forensic audit, or advanced reports receive an explicit mobile gap proof because this request restores mobile/admin parity to scope.</li>
<li>Scan confirmation is a draft-review step. No extracted value is trusted merely because it is present, and no domain record may exist before confirmation.</li>
<li>Every current page/screen and every directly declared form control receives a stable SCR/FLD ID derived from repository source. Regenerate this plan immediately before execution so newly added fields cannot disappear from scope.</li>
<li>“Save works” requires submit, committed response, reload/relaunch, exact normalized read-back, edit, other-client read-back where supported, audit/history, validation, role denial, duplicate retry, and atomic failure proof.</li>
</ul>
<h3>Date rules</h3>
<p>Each run uses four different date concepts: <strong>simulation clock</strong> (what the app thinks today is), <strong>document date</strong> (printed on the source), <strong>effective/service date</strong> (when money or state belongs), and <strong>entry/upload time</strong> (when the operator acts). Never overwrite one with another. America/New_York is the business timezone. Run midnight, DST, month-end, and year-end boundary cases exactly where scheduled.</p>
</section>

<section id="company">
<h2>2. The fictional company and opening portfolio</h2>
<p><strong>Blue Door Property Management</strong> manages 30 wholly owned or partner-owned residential properties in Columbus, Ohio: 20 single rentals and 10 duplexes, totaling 40 rentable Units. Five owner entities receive monthly statements and distributions. The year begins with 36 occupied Units and four genuine vacancies; no demo-domain data may be used.</p>
{row_table(["ID","Property","Structure","Address","Units","Owner","Opening basis","Opening loan"], portfolio_rows)}
</section>

<section id="run-day">
<h2>3. How to run one simulated day</h2>
<div class="steps">
<div class="card step"><b>01 · Freeze</b>Set the dev clock to the row’s date and America/New_York time. Capture the clock UI and worker status before any action.</div>
<div class="card step"><b>02 · Baseline</b>Record relevant list counts, balances, statuses, current user/role, source SHA, client build, and pending scan/worker queues.</div>
<div class="card step"><b>03 · Scan</b>Create only the listed synthetic asset. Upload/capture from the assigned client, preserve raw preview, review fields, correct known degradation, and confirm once.</div>
<div class="card step"><b>04 · Manual</b>Execute the assigned manual form/lifecycle action with the specified distinct entity. Validate required, invalid, cancel, retry, and cross-client read-back.</div>
<div class="card step"><b>05 · Money</b>Post only listed FIN events. Compare Tenant Account, Unit, Lease, Accounting, banking, owner, portal, and report projections to the oracle.</div>
<div class="card step"><b>06 · Close</b>Capture required evidence, run the negative/retry check, log every unexplained difference, and advance the clock only after the completion gate passes.</div>
</div>
<h3>Daily evidence folder</h3>
<p><code>Docs/Testing/Results/2027-year-simulation/YYYY-MM-DD/</code> contains <code>runlog.md</code>, web screenshots, Android screenshots/UI dumps, exported reports, scan draft/source IDs, financial variance CSV, network or API evidence, and defect links. Evidence is immutable after the day closes; corrections get a later addendum.</p>
</section>

<section id="calendar">
<h2>4. The dated operating calendar</h2>
<p>The calendar contains ordinary work several days each week plus rent cycles, applications, move-ins, renewals/endings, delinquency, maintenance, inspections, owner activity, banking, tax, access, portals, and deliberate failures. Expand one month and execute rows in sequence.</p>
{''.join(schedule_sections)}
</section>

<section id="scans">
<h2>5. Scan corpus plan</h2>
<p><code>scan-assets.csv</code> is the fixture-generation contract. Every row fixes the filename, date, target or contextual attachment, format, pages/images, capture difficulty, expected fields, edge case, linked records, and paired manual action. Render it with <code>scripts/qa/render-year-simulation-scan-corpus.py</code>, then use the matching dated folder under <code>output/pdf/tsk-749-year-simulation-scan-corpus/documents/</code>. Never use real leases, checks, IDs, signatures, statements, or account numbers.</p>
{row_table(["Document family","Planned assets"], family_rows)}
<h3>Required quality distribution</h3>
<p><span class="pill">clean exported PDF</span><span class="pill">multi-page lease</span><span class="pill">phone JPEG</span><span class="pill">stitched camera PDF</span><span class="pill">skew</span><span class="pill">shadow</span><span class="pill">low contrast</span><span class="pill">thermal receipt</span><span class="pill">handwriting</span><span class="pill">duplicate</span><span class="pill">wrong target</span><span class="pill">blank/unsupported/oversize</span></p>
<p class="danger">The corpus generator must watermark every page “SYNTHETIC QA — NOT A REAL DOCUMENT” and must never create plausible government IDs, banking credentials, or real signatures.</p>
</section>

<section id="coverage">
<h2>6. Web/mobile × scan/manual coverage ledger</h2>
<p>This matrix is the promise that “everything” is measured. W = manual web, M = manual mobile, SW = scan/attachment web, SM = scan/attachment mobile. A current product gap is still a required run: prove it and capture it during execution.</p>
{row_table(["ID","Domain","Surface","Web","Mobile","Scan path","Proof dates","Gap policy","Acceptance action"], coverage_rows)}
</section>

<section id="screens">
<h2>7. Every screen, assigned persona, and access proof</h2>
<p>This source-derived list includes every current SvelteKit page and every interactive Flutter screen, sheet, tab, or flow. The assigned persona must reach it through normal navigation and use every permitted action; an adjacent persona must be denied. Relationship users use their purpose-built Tenant and Owner portals, while staff use capability- and property-scoped shells.</p>
{row_table(["ID","Date","Client","Route/screen","Primary persona","All allowed personas","Source","Allowed proof","Denied proof"], screen_rows)}
</section>

<section id="fields">
<h2>8. Field-by-field persistence ledger</h2>
<p>Each FLD row is a directly declared input, select, textarea, text field, dropdown, switch, checkbox, radio group, or segmented control found in the current web/mobile source. On its scheduled day, use a realistic non-default value, save, reload/relaunch, compare the normalized value, edit it, check the paired client, and run relevant required/format/length/range/optional-null/stale-submit failures. Compound widgets and fields rendered dynamically are also exercised through their parent SCR and CRUD passes; if execution exposes an unlisted editable control, add a planner correction before advancing the clock.</p>
{row_table(["ID","Date","Client","Screen/component","Control","Field key","Label/hint","Persona","Source line","Acceptance proof"], field_rows)}
</section>

<section id="lifecycles">
<h2>9. Create, edit, delete-safe, and full lifecycle certification</h2>
<p>Disposable synthetic records prove confirmation cancel/confirm paths and safe deletion/deactivation. Posted money, signed documents, audit trails, and historical relationships are never destructively deleted: their supported reversal, void, supersede, archive, close, or deactivate action is the lifecycle under test.</p>
{row_table(["ID","Date","Entity","Persona","Lifecycle","Delete policy","Acceptance"], lifecycle_rows)}
</section>

<section id="notifications">
<h2>10. Notifications, reminders, routing, and delivery</h2>
<p>Every row triggers the real business event rather than fabricating an inbox entry. Verify recipient/property/relationship routing, user preferences, required-alert policy, outbox/provider result, retry/dead-letter behavior, duplicate suppression, safe preview copy, unread/read synchronization, and authorized deep links on web and mobile.</p>
{row_table(["ID","Date","Trigger","Recipients","Channel/route","Deep link","Preference proof","Delivery proof","Privacy proof"], notification_rows)}
</section>

<section id="money">
<h2>11. Independent financial oracle</h2>
<p>The oracle is append-only and double-entry. <code>financial-oracle.csv</code> stores business-event deltas; <code>journal.csv</code> stores balanced debit/credit lines; <code>monthly-controls.csv</code> fixes month-end expectations; the workbook recalculates these controls with formulas. Posted corrections are reversals, refunds, applications, or adjustments—never edits or deletes.</p>
{row_table(["Month","Events","Income","Expense","Net operating result","Operating cash Δ","Tenant AR Δ","Deposit liability Δ"], monthly_rows)}
<div class="callout"><strong>Year control:</strong> income {dollars(stats['year_income_cents'])}; expense {dollars(stats['year_expense_cents'])}; net operating result {dollars(stats['year_noi_cents'])}; journal debits and credits both {dollars(stats['journal_debits_cents'])}.</div>
<h3>Money reconciliation order</h3>
<ol>
<li>Tenant-account subledger by LeaseManagement and posted entry.</li>
<li>Security-deposit trust cash versus deposit liability and disposition history.</li>
<li>Operating, reserve, and deposit bank cash versus statement/match state.</li>
<li>Property/Unit expenses, work-order costs, owner allocations, Schedule E categories, and vendor totals.</li>
<li>Loans: opening principal, monthly principal reductions, interest expense, payoff, and amortization.</li>
<li>Capital assets, depreciation, disposition basis removal, selling costs, and gain.</li>
<li>Owner statements/distributions and consolidated P&L, cash flow, general ledger, rent roll, delinquency, occupancy, and year-end packet.</li>
</ol>
</section>

<section id="close">
<h2>12. Year-end acceptance and stop conditions</h2>
<ul class="checks">
<li>All dated RUN rows have evidence and a pass, confirmed bug, or explicit environment blocker.</li>
<li>All COV rows have manual web/mobile proof and scan/attachment proof where applicable; mobile gaps were not waived.</li>
<li>All SCR rows have allowed-role use and adjacent-role denial proof; every Tenant and Owner portal surface was used with real relationship-scoped identities.</li>
<li>All FLD rows have save, reload/relaunch, normalized read-back, edit, validation, cross-client, audit/history, retry, and atomicity evidence.</li>
<li>All CRUD rows complete their safe lifecycle, including cancel/confirm deletion or the correct reversal/void/archive guard.</li>
<li>All NTF rows prove trigger, routing, preference, delivery/retry, unread/read sync, privacy-safe copy, and authorized deep links.</li>
<li>Every planned SCN asset exists, is synthetic/watermarked, was uploaded once in its planned positive path, and designated duplicates/invalids created no unintended domain records.</li>
<li>Every FIN event is represented exactly once in the system or has a documented product defect; journal debits equal credits.</li>
<li>Every monthly close ties with zero unexplained variance before the next month begins.</li>
<li>Year-end totals tie across the independent workbook, system reports, bank reconciliation, tenant accounts, deposits, owners, tax, and audit exports.</li>
<li>All list filters, joins, sorting, paging, counts, and aggregates are DB-side; translated SQL is preserved for representative 0/1/20/200+ record states.</li>
<li>All aggregate writes are atomic; failure injection proves no half-created lease, account, ledger, audit, document, signature, inbox/outbox, allocation, or disposition.</li>
<li>Web and mobile read the same committed truth after realtime/refresh; route rendering alone is not acceptance.</li>
</ul>
<h3>Companion files</h3>
<p><code>portfolio.csv</code> · <code>leases.csv</code> · <code>schedule.csv</code> · <code>scan-assets.csv</code> · <code>surface-coverage.csv</code> · <code>screen-inventory.csv</code> · <code>field-inventory.csv</code> · <code>role-journeys.csv</code> · <code>crud-lifecycles.csv</code> · <code>notification-scenarios.csv</code> · <code>financial-oracle.csv</code> · <code>journal.csv</code> · <code>monthly-controls.csv</code> · <code>control-totals.json</code> · <code>rental-command-2027-simulation-oracle.xlsx</code></p>
</section>
<footer>Generated for TSK-749 from the repository’s current routes, mobile features, scan audit, parity ledger, foundation blueprint, and E2E scenarios. This is a synthetic product-acceptance plan, not legal, tax, accounting, banking, or property-management advice.</footer>
</main>
</body>
</html>"""


def write_outputs() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    property_rows = [asdict(item) for item in properties]
    unit_rows = [asdict(item) for item in units]
    lease_rows = [
        {
            **asdict(item),
            "start_date": iso(item.start_date),
            "end_date": iso(item.end_date),
        }
        for item in leases
    ]
    asset_rows = [asdict(item) for item in assets]
    schedule_rows = [asdict(item) for item in sorted(schedules, key=lambda row: (row.simulated_clock, row.sequence))]
    financial_rows = [asdict(item) for item in financial_events]
    monthly_controls = build_monthly_controls()
    stats = validate(monthly_controls)

    csv_write(OUT / "portfolio.csv", property_rows)
    csv_write(OUT / "units.csv", unit_rows)
    csv_write(OUT / "leases.csv", lease_rows)
    csv_write(OUT / "scan-assets.csv", asset_rows)
    csv_write(OUT / "schedule.csv", schedule_rows)
    csv_write(OUT / "surface-coverage.csv", coverage)
    csv_write(OUT / "screen-inventory.csv", screen_inventory)
    csv_write(OUT / "field-inventory.csv", field_inventory)
    csv_write(OUT / "role-journeys.csv", role_journeys)
    csv_write(OUT / "crud-lifecycles.csv", crud_lifecycles)
    csv_write(OUT / "notification-scenarios.csv", notification_scenarios)
    csv_write(OUT / "financial-oracle.csv", financial_rows)
    csv_write(OUT / "journal.csv", journal)
    csv_write(OUT / "monthly-controls.csv", monthly_controls)
    (OUT / "control-totals.json").write_text(json.dumps(stats, indent=2) + "\n", encoding="utf-8")
    (OUT / "index.html").write_text(render_html(monthly_controls, stats), encoding="utf-8")
    (OUT / "README.md").write_text(
        """# 2027 Scan-First Rental Company Simulation

Open `index.html` first. It is the day-by-day execution contract for TSK-749.

The CSVs and `rental-command-2027-simulation-oracle.xlsx` are the independent
controls. Do not change amounts or dates during execution without issuing a
versioned planner correction and regenerating every dependent control.

`scan-assets.csv` is the source manifest for the rendered synthetic fixture corpus.
Generate it with `scripts/qa/render-year-simulation-scan-corpus.py`; the default
destination is `output/pdf/tsk-749-year-simulation-scan-corpus/`. Use the dated
folders under `documents/` with the matching simulated clock day. The generated
`corpus-index.csv` records the checksum and validation result for every upload file.
No real documents, identities, signatures, accounts, or production data are permitted.

`screen-inventory.csv`, `field-inventory.csv`, `role-journeys.csv`,
`crud-lifecycles.csv`, and `notification-scenarios.csv` are regenerated from the
current product source and the exhaustive operational contract. Run the generator
again immediately before executing the year so newly added screens or fields do
not escape coverage.
""",
        encoding="utf-8",
    )
    print(json.dumps(stats, indent=2))


def main() -> None:
    build_portfolio()
    build_leases()
    build_opening_setup()
    build_monthly_rent_and_payments()
    build_weekly_operations()
    build_recurring_and_owner_finance()
    build_lease_lifecycle()
    build_capital_assets_disposition_and_adjustments()
    build_screen_and_field_inventory()
    build_role_notification_and_crud_calendar()
    build_nonfinancial_surface_calendar()
    write_outputs()


if __name__ == "__main__":
    main()
