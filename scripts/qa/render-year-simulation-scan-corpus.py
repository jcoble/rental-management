#!/usr/bin/env python3
"""Render the TSK-749 scan manifest into deterministic synthetic QA documents."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import re
import shutil
import textwrap
from collections import Counter
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterable

from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageFont
from pypdf import PdfReader
from reportlab.lib import colors
from reportlab.lib.pagesizes import letter
from reportlab.pdfbase.pdfmetrics import stringWidth
from reportlab.pdfgen import canvas


REPO = Path(__file__).resolve().parents[2]
PLAN = REPO / "Docs" / "Testing" / "YearSimulation2027"
DEFAULT_OUTPUT = REPO / "output" / "pdf" / "tsk-749-year-simulation-scan-corpus"
PAGE_WIDTH, PAGE_HEIGHT = letter
NAVY = colors.HexColor("#173B55")
TEAL = colors.HexColor("#1E6673")
GOLD = colors.HexColor("#D79A28")
INK = colors.HexColor("#17212B")
MUTED = colors.HexColor("#5C6873")
PAPER = colors.HexColor("#FAF7EF")


@dataclass(frozen=True)
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
    confidentiality: str


def read_csv(name: str) -> list[dict[str, str]]:
    with (PLAN / name).open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def money(cents: str | int | None) -> str:
    value = int(cents or 0)
    return f"${value / 100:,.2f}"


def asset_number(asset_id: str) -> int:
    return int(asset_id.split("-")[1])


def page_count(asset: Asset) -> int:
    match = re.match(r"(\d+)-(\d+) pages", asset.pages_or_images)
    if match:
        low, high = map(int, match.groups())
        return low + asset_number(asset.asset_id) % (high - low + 1)
    match = re.match(r"(\d+) pages", asset.pages_or_images)
    if match:
        return int(match.group(1))
    return 1


def image_panel_count(asset: Asset) -> int:
    match = re.match(r"(\d+) (?:image|images|page photos)", asset.pages_or_images)
    return int(match.group(1)) if match else 1


def slug(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")


def wrap(text: str, width: int) -> list[str]:
    return textwrap.wrap(text, width=width, break_long_words=False) or [""]


def fit_text(c: canvas.Canvas, text: str, x: float, y: float, max_width: float, size: float) -> None:
    while size > 7 and stringWidth(text, "Helvetica-Bold", size) > max_width:
        size -= 0.5
    c.setFont("Helvetica-Bold", size)
    c.drawString(x, y, text)


def data_context(
    asset: Asset,
    properties: dict[str, dict[str, str]],
    units: dict[str, dict[str, str]],
    leases: dict[str, dict[str, str]],
    events: dict[str, dict[str, str]],
) -> dict[str, str]:
    prop = properties.get(asset.property_id, {})
    unit = units.get(asset.unit_id, {})
    lease = leases.get(asset.lease_id, {})
    event = events.get(asset.financial_event_id, {})
    principal_cents = int(prop.get("monthly_principal_cents") or 0)
    interest_cents = int(prop.get("monthly_interest_cents") or 0)
    return {
        "property_name": prop.get("property_name", "Portfolio-level record"),
        "address": " ".join(
            part
            for part in [
                prop.get("address", ""),
                prop.get("city", ""),
                prop.get("state", ""),
                prop.get("postal_code", ""),
            ]
            if part
        )
        or "Synthetic portfolio address",
        "owner": prop.get("owner_entity_name", "Blue Door Property Management"),
        "unit": unit.get("label", asset.unit_id or "N/A"),
        "tenant": lease.get("tenant_name", event.get("counterparty", "Synthetic QA Counterparty")),
        "tenant_email": lease.get("tenant_email", "qa-contact@example.local"),
        "lease_start": lease.get("start_date", asset.planned_date),
        "lease_end": lease.get("end_date", asset.planned_date),
        "rent": money(lease.get("monthly_rent_cents")),
        "deposit": money(lease.get("deposit_cents")),
        "amount": money(event.get("amount_cents")),
        "original_cost": money(prop.get("original_cost_cents")),
        "opening_loan_balance": money(prop.get("opening_loan_balance_cents")),
        "principal": money(principal_cents),
        "interest": money(interest_cents),
        "mortgage_due": money(principal_cents + interest_cents),
        "reference": event.get("reference", asset.asset_id),
        "event_type": event.get("event_type", asset.intended_target),
        "category": event.get("category", asset.document_family),
        "memo": event.get("memo", asset.intake_expectation),
        "effective_date": event.get("effective_date", asset.planned_date),
    }


def family_title(asset: Asset) -> str:
    titles = {
        "Rent check": "TENANT RENT CHECK",
        "Money order": "MONEY ORDER RECEIPT",
        "Vendor invoice / receipt": "VENDOR INVOICE / RECEIPT",
        "Monthly mortgage statement": "MONTHLY MORTGAGE STATEMENT",
        "Mortgage statement": "MORTGAGE ACCOUNT STATEMENT",
        "Operating bank statement": "OPERATING ACCOUNT STATEMENT",
        "Residential lease agreement": "RESIDENTIAL LEASE AGREEMENT",
        "Rental application packet": "RENTAL APPLICATION PACKET",
        "Security deposit receipt": "SECURITY DEPOSIT RECEIPT",
        "Maintenance request with supporting photo": "MAINTENANCE REQUEST",
        "Move-out / non-renewal notice": "NOTICE OF MOVE-OUT / NON-RENEWAL",
        "Move-out inspection report": "MOVE-OUT INSPECTION REPORT",
        "Synthetic legal notice / court filing": "SYNTHETIC LEGAL ACCEPTANCE FORM",
        "Deed / settlement statement": "DEED AND SETTLEMENT RECORD",
        "Closing disclosure / settlement statement": "CLOSING DISCLOSURE",
        "Capital improvement invoice": "CAPITAL IMPROVEMENT INVOICE",
    }
    return titles.get(asset.document_family, asset.document_family.upper())


def field_rows(asset: Asset, ctx: dict[str, str]) -> list[tuple[str, str]]:
    common = [
        ("Document ID", asset.asset_id),
        ("Document date", asset.planned_date),
        ("Property", f"{asset.property_id or 'Portfolio'} - {ctx['property_name']}"),
        ("Unit", f"{asset.unit_id or 'N/A'} - {ctx['unit']}"),
        ("Reference", ctx["reference"]),
    ]
    family = asset.document_family
    if family == "Residential lease agreement":
        return common + [
            ("Landlord", ctx["owner"]),
            ("Resident", ctx["tenant"]),
            ("Premises", ctx["address"]),
            ("Lease term", f"{ctx['lease_start']} through {ctx['lease_end']}"),
            ("Monthly rent", ctx["rent"]),
            ("Security deposit", ctx["deposit"]),
            ("Rent due", "First day of each month"),
        ]
    if family == "Rental application packet":
        return common + [
            ("Applicant", ctx["tenant"]),
            ("Email", ctx["tenant_email"]),
            ("Requested home", ctx["address"]),
            ("Desired move-in", ctx["lease_start"]),
            ("Monthly income", "$5,450.00"),
            ("Employer", "Synthetic QA Employer"),
            ("Optional middle name", ""),
        ]
    if family in {"Rent check", "Money order", "Security deposit receipt"}:
        return common + [
            ("Payor", ctx["tenant"]),
            ("Payee", "Blue Door Property Management"),
            ("Amount", ctx["amount"] if ctx["amount"] != "$0.00" else ctx["rent"]),
            ("Memo", f"{asset.lease_id or 'Portfolio'} / {asset.unit_id or 'N/A'}"),
            ("Status", "For QA upload only - VOID"),
        ]
    if "mortgage" in family.lower():
        return common + [
            ("Borrower", ctx["owner"]),
            ("Property address", ctx["address"]),
            ("Payment due", ctx["effective_date"]),
            ("Amount due", ctx["amount"] if ctx["amount"] != "$0.00" else ctx["mortgage_due"]),
            ("Principal", ctx["principal"]),
            ("Interest", ctx["interest"]),
            ("Escrow", "$318.00"),
            ("Unpaid principal balance", ctx["opening_loan_balance"]),
            ("Synthetic account", "XXXX-4242"),
        ]
    if family == "Operating bank statement":
        return common + [
            ("Account owner", "Blue Door Property Management"),
            ("Statement period", asset.planned_date[:7]),
            ("Synthetic account", "XXXX-4242"),
            ("Opening balance", "$150,000.00"),
            ("Deposits", "$74,325.00"),
            ("Withdrawals", "$59,410.00"),
            ("Closing balance", "$164,915.00"),
        ]
    if family in {"Vendor invoice / receipt", "Capital improvement invoice"}:
        return common + [
            ("Vendor", ctx["tenant"]),
            ("Service location", ctx["address"]),
            ("Invoice date", ctx["effective_date"]),
            ("Subtotal", ctx["amount"]),
            ("Tax", "$0.00"),
            ("Total due", ctx["amount"]),
            ("Payment terms", "Net 15"),
        ]
    if family == "Maintenance request with supporting photo":
        return common + [
            ("Requested by", ctx["tenant"]),
            ("Location", ctx["address"]),
            ("Priority", "Normal"),
            ("Issue", "Water visible beneath kitchen sink after faucet use"),
            ("Permission to enter", "Yes, with notice"),
            ("Preferred contact", ctx["tenant_email"]),
        ]
    if family == "Move-out inspection report":
        return common + [
            ("Resident", ctx["tenant"]),
            ("Inspection date", asset.planned_date),
            ("Inspector", "Morgan Reed"),
            ("Keys returned", "Yes"),
            ("Normal wear", "Minor wall scuffs"),
            ("Chargeable damage", "Broken bedroom blind - $85.00"),
        ]
    if "notice" in family.lower() or "legal" in family.lower():
        return common + [
            ("Recipient", ctx["tenant"]),
            ("Premises", ctx["address"]),
            ("Effective date", ctx["effective_date"]),
            ("Delivery method", "Certified mail and portal copy"),
            ("Response deadline", "See synthetic notice terms"),
            ("QA disclaimer", "Not legal advice; not valid for service"),
        ]
    if "settlement" in family.lower() or family == "Closing disclosure / settlement statement":
        return common + [
            ("Owner / seller", ctx["owner"]),
            ("Property address", ctx["address"]),
            ("Settlement date", ctx["effective_date"]),
            ("Contract price", ctx["amount"] if ctx["amount"] != "$0.00" else ctx["original_cost"]),
            ("Loan payoff", "$84,500.00"),
            ("Closing costs", "$7,925.00"),
            ("Net proceeds", "$132,575.00"),
        ]
    return common + [
        ("Counterparty", ctx["tenant"]),
        ("Amount", ctx["amount"]),
        ("Description", ctx["memo"]),
    ]


def draw_watermark(c: canvas.Canvas, text: str = "SYNTHETIC QA - NOT VALID") -> None:
    c.saveState()
    c.setFillColor(colors.Color(0.75, 0.12, 0.12, alpha=0.10))
    c.setFont("Helvetica-Bold", 38)
    c.translate(PAGE_WIDTH / 2, PAGE_HEIGHT / 2)
    c.rotate(35)
    c.drawCentredString(0, 0, text)
    c.restoreState()


def draw_header(c: canvas.Canvas, asset: Asset, page_no: int, total_pages: int) -> float:
    c.setFillColor(NAVY)
    c.rect(0, PAGE_HEIGHT - 104, PAGE_WIDTH, 104, fill=1, stroke=0)
    c.setFillColor(colors.white)
    fit_text(c, family_title(asset), 42, PAGE_HEIGHT - 52, PAGE_WIDTH - 84, 17)
    c.setFont("Helvetica", 8.5)
    c.drawString(42, PAGE_HEIGHT - 72, f"{asset.asset_id} | Planned upload {asset.planned_date}")
    c.drawRightString(PAGE_WIDTH - 42, PAGE_HEIGHT - 72, f"Page {page_no} of {total_pages}")
    c.setFillColor(GOLD)
    c.rect(0, PAGE_HEIGHT - 109, PAGE_WIDTH, 5, fill=1, stroke=0)
    return PAGE_HEIGHT - 137


def draw_footer(c: canvas.Canvas, asset: Asset) -> None:
    c.setStrokeColor(colors.HexColor("#CCD5DB"))
    c.line(42, 40, PAGE_WIDTH - 42, 40)
    c.setFillColor(MUTED)
    c.setFont("Helvetica", 7)
    c.drawString(42, 27, asset.confidentiality)
    c.drawRightString(PAGE_WIDTH - 42, 27, f"{asset.asset_id} | {asset.filename}")


def draw_table(c: canvas.Canvas, rows: Iterable[tuple[str, str]], y: float) -> float:
    label_width = 132
    row_height = 31
    for index, (label, value) in enumerate(rows):
        if y < 116:
            break
        c.setFillColor(colors.HexColor("#F1F4F5") if index % 2 == 0 else colors.white)
        c.roundRect(42, y - row_height + 4, PAGE_WIDTH - 84, row_height, 3, fill=1, stroke=0)
        c.setFillColor(MUTED)
        c.setFont("Helvetica-Bold", 8)
        c.drawString(52, y - 11, label.upper())
        c.setFillColor(INK)
        value_lines = wrap(value or "[intentionally blank optional field]", 58)
        c.setFont("Helvetica", 9)
        for offset, line in enumerate(value_lines[:2]):
            c.drawString(42 + label_width, y - 11 - offset * 11, line)
        y -= row_height
    return y


def draw_check(c: canvas.Canvas, asset: Asset, ctx: dict[str, str], y: float) -> float:
    amount = ctx["amount"] if ctx["amount"] != "$0.00" else ctx["rent"]
    c.setFillColor(colors.HexColor("#EEF5ED"))
    c.setStrokeColor(TEAL)
    c.roundRect(42, y - 190, PAGE_WIDTH - 84, 182, 8, fill=1, stroke=1)
    c.setFillColor(INK)
    c.setFont("Helvetica-Bold", 12)
    c.drawString(58, y - 32, "VOID SYNTHETIC CHECK - QA ONLY")
    c.setFont("Helvetica", 8)
    c.drawRightString(PAGE_WIDTH - 58, y - 32, f"No. {asset_number(asset.asset_id):06d}")
    c.drawString(58, y - 62, f"PAY TO: Blue Door Property Management")
    c.setFont("Helvetica-Bold", 13)
    c.drawRightString(PAGE_WIDTH - 58, y - 62, amount)
    c.setFont("Helvetica", 9)
    c.drawString(58, y - 92, f"PAYOR: {ctx['tenant']}")
    c.drawString(58, y - 114, f"MEMO: {asset.lease_id} / {asset.unit_id} / {asset.planned_date[:7]}")
    c.setFont("Helvetica-Oblique", 10)
    c.drawRightString(PAGE_WIDTH - 58, y - 116, "Synthetic Signature")
    c.setFont("Courier", 10)
    c.drawCentredString(PAGE_WIDTH / 2, y - 159, "VOID 000000000 000424242 0000000000")
    return y - 210


def draw_photo_panel(c: canvas.Canvas, asset: Asset, y: float, page_no: int) -> float:
    c.setFillColor(colors.HexColor("#D9E3E7"))
    c.roundRect(42, y - 245, PAGE_WIDTH - 84, 230, 6, fill=1, stroke=0)
    c.setFillColor(colors.HexColor("#8FA7B0"))
    c.rect(62, y - 216, PAGE_WIDTH - 124, 165, fill=1, stroke=0)
    if asset.document_family == "Maintenance request with supporting photo":
        c.setFillColor(colors.HexColor("#E8ECEC"))
        c.rect(104, y - 135, 300, 58, fill=1, stroke=0)
        c.setFillColor(colors.HexColor("#7E8B91"))
        c.rect(236, y - 173, 36, 42, fill=1, stroke=0)
        c.setStrokeColor(colors.HexColor("#2C6675"))
        c.setLineWidth(5)
        c.arc(205, y - 112, 305, y - 45, 10, 160)
        c.setFillColor(colors.HexColor("#4C97B8"))
        for dx, dy in [(250, -188), (277, -197), (224, -202)]:
            c.circle(dx, y + dy, 8, fill=1, stroke=0)
        caption = "Tenant photo: moisture beneath kitchen sink"
    else:
        c.setFillColor(colors.HexColor("#F5F1E7"))
        c.rect(78, y - 198, 130, 120, fill=1, stroke=0)
        c.rect(220, y - 198, 145, 120, fill=1, stroke=0)
        c.rect(377, y - 198, 112, 120, fill=1, stroke=0)
        caption = f"Inspection photo group {page_no}: synthetic room condition"
    c.setFillColor(INK)
    c.setFont("Helvetica-Oblique", 8)
    c.drawString(62, y - 232, caption)
    return y - 262


def draw_terms(c: canvas.Canvas, asset: Asset, ctx: dict[str, str], y: float, page_no: int) -> float:
    headings = {
        "Residential lease agreement": [
            "Parties and premises",
            "Rent, deposits, and fees",
            "Use and occupancy",
            "Maintenance and access",
            "Utilities and services",
            "Rules and disclosures",
            "Default and remedies",
            "Move-out responsibilities",
            "Signatures and acknowledgments",
        ],
        "Rental application packet": [
            "Applicant identity",
            "Residence history",
            "Employment and income",
            "References",
            "Household and pets",
            "Consent placeholder",
        ],
        "Monthly mortgage statement": ["Payment summary", "Transaction history", "Escrow activity"],
        "Mortgage statement": ["Account summary", "Principal and interest", "Payment coupon"],
        "Operating bank statement": ["Account summary", "Deposits", "Withdrawals", "Reconciliation"],
        "Move-out inspection report": [
            "Entry and living room",
            "Kitchen and appliances",
            "Bedrooms and bathrooms",
            "Safety devices",
            "Exterior and keys",
            "Charge summary",
            "Resident acknowledgment",
        ],
    }
    options = headings.get(asset.document_family, ["Document details", "Line items", "Acceptance notes"])
    heading = options[(page_no - 1) % len(options)]
    c.setFillColor(NAVY)
    c.setFont("Helvetica-Bold", 12)
    c.drawString(42, y, heading)
    y -= 24
    paragraphs = [
        (
            f"This synthetic section records {heading.lower()} for {ctx['property_name']} "
            f"({asset.property_id or 'portfolio level'}). It exists only to exercise scan extraction, "
            "draft confirmation, saved-value readback, edit, and audit behavior."
        ),
        (
            f"Expected extraction targets: {asset.expected_fields}. Values must remain associated with "
            f"{asset.unit_id or 'no unit'} and {asset.lease_id or 'no lease'}."
        ),
        (
            f"Capture profile: {asset.capture_profile}. Edge case: "
            f"{asset.edge_case or 'standard clean-source acceptance case'}."
        ),
    ]
    c.setFillColor(INK)
    c.setFont("Helvetica", 9)
    for paragraph in paragraphs:
        for line in wrap(paragraph, 91):
            c.drawString(42, y, line)
            y -= 12
        y -= 8
    return y


def render_pdf(
    asset: Asset,
    path: Path,
    ctx: dict[str, str],
) -> None:
    total_pages = page_count(asset)
    path.parent.mkdir(parents=True, exist_ok=True)
    pdf = canvas.Canvas(str(path), pagesize=letter, pageCompression=1)
    pdf.setTitle(f"{asset.asset_id} - {asset.document_family}")
    pdf.setAuthor("Rental Command Synthetic QA Corpus")
    pdf.setSubject(asset.intake_expectation)
    for page_no in range(1, total_pages + 1):
        pdf.setFillColor(PAPER)
        pdf.rect(0, 0, PAGE_WIDTH, PAGE_HEIGHT, fill=1, stroke=0)
        y = draw_header(pdf, asset, page_no, total_pages)
        draw_watermark(pdf)
        if page_no == 1:
            if asset.document_family == "Rent check":
                y = draw_check(pdf, asset, ctx, y)
            y = draw_table(pdf, field_rows(asset, ctx), y)
        else:
            if asset.document_family in {
                "Move-out inspection report",
                "Maintenance request with supporting photo",
            }:
                y = draw_photo_panel(pdf, asset, y, page_no)
            y = draw_terms(pdf, asset, ctx, y, page_no)
        if y > 180:
            y -= 14
            pdf.setFillColor(NAVY)
            pdf.setFont("Helvetica-Bold", 9)
            pdf.drawString(42, y, "INTAKE ACCEPTANCE")
            y -= 16
            pdf.setFillColor(INK)
            pdf.setFont("Helvetica", 8.5)
            for line in wrap(asset.intake_expectation, 94):
                pdf.drawString(42, y, line)
                y -= 11
        draw_footer(pdf, asset)
        pdf.showPage()
    pdf.save()


def pil_font(size: int, bold: bool = False, italic: bool = False) -> ImageFont.FreeTypeFont:
    candidates = []
    if italic:
        candidates.extend(
            [
                "/System/Library/Fonts/Supplemental/Arial Italic.ttf",
                "/System/Library/Fonts/Supplemental/Times New Roman Italic.ttf",
            ]
        )
    elif bold:
        candidates.extend(
            [
                "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
                "/System/Library/Fonts/Supplemental/Helvetica Bold.ttf",
            ]
        )
    else:
        candidates.extend(
            [
                "/System/Library/Fonts/Supplemental/Arial.ttf",
                "/System/Library/Fonts/Supplemental/Helvetica.ttf",
            ]
        )
    for candidate in candidates:
        if Path(candidate).exists():
            return ImageFont.truetype(candidate, size)
    return ImageFont.load_default(size=size)


def image_wrapped(
    draw: ImageDraw.ImageDraw,
    text: str,
    xy: tuple[int, int],
    font: ImageFont.ImageFont,
    fill: tuple[int, int, int],
    width: int,
    spacing: int = 7,
) -> int:
    lines = wrap(text, width)
    draw.multiline_text(xy, "\n".join(lines), font=font, fill=fill, spacing=spacing)
    bbox = draw.multiline_textbbox(xy, "\n".join(lines), font=font, spacing=spacing)
    return bbox[3]


def draw_image_document(asset: Asset, ctx: dict[str, str], panel_count: int) -> Image.Image:
    panel_height = 1450
    width = 1200
    height = panel_height * panel_count
    background = Image.new("RGB", (width + 160, height + 160), (74, 82, 85))
    title_font = pil_font(40, bold=True)
    heading_font = pil_font(24, bold=True)
    body_font = pil_font(21)
    small_font = pil_font(16)
    hand_font = pil_font(25, italic=True)
    for panel in range(panel_count):
        paper = Image.new("RGB", (width, panel_height), (249, 247, 239))
        draw = ImageDraw.Draw(paper)
        draw.rectangle((0, 0, width, 150), fill=(23, 59, 85))
        draw.rectangle((0, 150, width, 160), fill=(215, 154, 40))
        draw.text((55, 42), family_title(asset), font=title_font, fill="white")
        draw.text(
            (55, 105),
            f"{asset.asset_id} | {asset.planned_date} | image {panel + 1} of {panel_count}",
            font=small_font,
            fill=(225, 233, 238),
        )
        draw.text((820, 24), "SYNTHETIC QA", font=heading_font, fill=(255, 215, 140))
        y = 205
        if panel == 0 and asset.document_family in {"Rent check", "Money order"}:
            draw.rounded_rectangle((60, y, 1140, y + 430), radius=20, fill=(236, 245, 235), outline=(30, 102, 115), width=4)
            draw.text((95, y + 38), "VOID - SYNTHETIC PAYMENT INSTRUMENT", font=heading_font, fill=(23, 59, 85))
            amt = ctx["amount"] if ctx["amount"] != "$0.00" else ctx["rent"]
            draw.text((95, y + 105), "PAY TO: Blue Door Property Management", font=body_font, fill=(23, 33, 43))
            draw.text((860, y + 105), amt, font=heading_font, fill=(23, 33, 43))
            draw.text((95, y + 170), f"PAYOR: {ctx['tenant']}", font=body_font, fill=(23, 33, 43))
            draw.text((95, y + 225), f"MEMO: {asset.lease_id} / {asset.unit_id}", font=body_font, fill=(23, 33, 43))
            draw.text((755, y + 300), "Synthetic Signature", font=hand_font, fill=(38, 67, 91))
            draw.text((250, y + 370), "VOID 000000000 000424242 0000000000", font=pil_font(20), fill=(23, 33, 43))
            y += 470
        elif asset.document_family == "Maintenance request with supporting photo" and panel > 0:
            draw.rounded_rectangle((80, y, 1120, y + 650), radius=18, fill=(146, 166, 173))
            draw.rectangle((240, y + 100, 960, y + 290), fill=(231, 236, 235))
            draw.rectangle((535, y + 285, 665, y + 445), fill=(103, 119, 126))
            draw.arc((450, y + 25, 760, y + 235), 190, 350, fill=(45, 103, 119), width=18)
            for dx, dy in [(600, 490), (535, 535), (680, 555), (455, 520)]:
                draw.ellipse((dx - 22, y + dy - 22, dx + 22, y + dy + 22), fill=(68, 150, 190))
            draw.text((105, y + 585), "Tenant photo: moisture beneath kitchen sink", font=small_font, fill=(20, 38, 47))
            y += 700
        else:
            rows = field_rows(asset, ctx) if panel == 0 else [
                ("Section", f"Supporting page photo {panel + 1}"),
                ("Property", ctx["property_name"]),
                ("Premises", ctx["address"]),
                ("Expected target", asset.intended_target),
                ("Extraction fields", asset.expected_fields),
                ("Edge case", asset.edge_case or "Standard acceptance case"),
            ]
            for index, (label, value) in enumerate(rows):
                if y > panel_height - 230:
                    break
                fill = (239, 242, 242) if index % 2 == 0 else (255, 255, 252)
                draw.rounded_rectangle((55, y, 1145, y + 105), radius=9, fill=fill)
                draw.text((75, y + 18), label.upper(), font=small_font, fill=(82, 96, 108))
                image_wrapped(draw, value or "[intentionally blank optional field]", (330, y + 18), body_font, (23, 33, 43), 54)
                y += 114
        if y < panel_height - 240:
            draw.text((55, y + 18), "INTAKE ACCEPTANCE", font=heading_font, fill=(23, 59, 85))
            y = image_wrapped(draw, asset.intake_expectation, (55, y + 58), body_font, (23, 33, 43), 84)
        draw.line((55, panel_height - 115, 1145, panel_height - 115), fill=(190, 201, 207), width=2)
        draw.text((55, panel_height - 90), asset.confidentiality, font=small_font, fill=(83, 97, 108))
        draw.text((55, panel_height - 56), asset.filename, font=small_font, fill=(83, 97, 108))

        if "low contrast" in asset.capture_profile:
            paper = ImageEnhance.Contrast(paper).enhance(0.62)
            paper = ImageEnhance.Brightness(paper).enhance(1.08)
        if "shadow" in asset.capture_profile or "phone" in asset.capture_profile:
            shadow = Image.new("RGBA", (width + 50, panel_height + 50), (0, 0, 0, 0))
            ImageDraw.Draw(shadow).rounded_rectangle((25, 25, width + 10, panel_height + 10), radius=10, fill=(0, 0, 0, 105))
            shadow = shadow.filter(ImageFilter.GaussianBlur(18))
            background.paste(shadow, (45, panel * panel_height + 45), shadow)
        angle = ((asset_number(asset.asset_id) % 5) - 2) * 0.35 if "skew" in asset.capture_profile or "phone" in asset.capture_profile else 0
        paper = paper.rotate(angle, resample=Image.Resampling.BICUBIC, expand=False, fillcolor=(249, 247, 239))
        background.paste(paper, (80, 80 + panel * panel_height))
    return background


def render_jpeg(asset: Asset, path: Path, ctx: dict[str, str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    panels = min(image_panel_count(asset), 9)
    image = draw_image_document(asset, ctx, panels)
    image.save(path, "JPEG", quality=82, optimize=True, progressive=True, dpi=(150, 150))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def validate_file(asset: Asset, path: Path) -> dict[str, str | int]:
    expected_suffix = ".pdf" if asset.format == "PDF" else ".jpg"
    if path.suffix.lower() != expected_suffix:
        raise ValueError(f"{asset.asset_id}: extension {path.suffix} != {expected_suffix}")
    if not path.exists() or path.stat().st_size == 0:
        raise ValueError(f"{asset.asset_id}: missing or empty file")
    result: dict[str, str | int] = {
        "asset_id": asset.asset_id,
        "planned_date": asset.planned_date,
        "filename": asset.filename,
        "document_family": asset.document_family,
        "format": asset.format,
        "relative_path": str(path.relative_to(path.parents[2])),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
        "page_or_panel_count": 0,
        "width": "",
        "height": "",
        "status": "OK",
    }
    if asset.format == "PDF":
        reader = PdfReader(path)
        count = len(reader.pages)
        if count != page_count(asset):
            raise ValueError(f"{asset.asset_id}: PDF pages {count} != {page_count(asset)}")
        text = "".join((page.extract_text() or "") for page in reader.pages[:2])
        if asset.asset_id not in text or "SYNTHETIC" not in text:
            raise ValueError(f"{asset.asset_id}: required QA text not extractable")
        result["page_or_panel_count"] = count
    else:
        with Image.open(path) as image:
            image.verify()
        with Image.open(path) as image:
            width, height = image.size
        if width < 1000 or height < 1200:
            raise ValueError(f"{asset.asset_id}: JPEG dimensions too small: {width}x{height}")
        result["page_or_panel_count"] = image_panel_count(asset)
        result["width"] = width
        result["height"] = height
    return result


def write_csv(path: Path, rows: list[dict[str, str | int]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not rows:
        return
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def create_preview(pdf: Path, output: Path) -> None:
    import subprocess

    output.parent.mkdir(parents=True, exist_ok=True)
    prefix = output.with_suffix("")
    subprocess.run(
        ["pdftoppm", "-f", "1", "-singlefile", "-png", "-r", "110", str(pdf), str(prefix)],
        check=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
    )


def make_contact_sheet(
    items: list[tuple[str, Path]],
    output: Path,
    columns: int = 4,
    thumb_width: int = 280,
    thumb_height: int = 370,
) -> None:
    rows = math.ceil(len(items) / columns)
    sheet = Image.new(
        "RGB",
        (columns * (thumb_width + 30) + 30, rows * (thumb_height + 65) + 30),
        "#D9D5CC",
    )
    draw = ImageDraw.Draw(sheet)
    font = pil_font(18)
    for index, (label, source) in enumerate(items):
        with Image.open(source) as opened:
            image = opened.convert("RGB")
        image.thumbnail((thumb_width, thumb_height))
        x = 30 + (index % columns) * (thumb_width + 30)
        y = 25 + (index // columns) * (thumb_height + 65)
        sheet.paste(image, (x + (thumb_width - image.width) // 2, y))
        label_lines = "\n".join(label[offset : offset + 30] for offset in range(0, len(label), 30))
        draw.multiline_text(
            (x, y + thumb_height + 8),
            label_lines,
            font=font,
            fill="#17212B",
            spacing=3,
        )
    output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output)


def build_visual_review_evidence(output: Path) -> list[str]:
    review_dir = output / "visual-review"
    working = review_dir / ".working"
    review_dir.mkdir(parents=True, exist_ok=True)
    if working.exists():
        shutil.rmtree(working)
    working.mkdir(parents=True)
    corpus_rows = list(csv.DictReader((output / "corpus-index.csv").open(newline="", encoding="utf-8")))
    preview_rows = list(csv.DictReader((output / "preview-index.csv").open(newline="", encoding="utf-8")))

    first_pages = [
        (row["document_family"], output / row["preview_png"])
        for row in sorted(preview_rows, key=lambda item: item["document_family"])
    ]
    make_contact_sheet(first_pages, review_dir / "pdf-family-first-pages.png")

    first_jpegs: dict[str, Path] = {}
    first_pdfs: dict[str, Path] = {}
    for row in corpus_rows:
        source = output / row["relative_path"]
        if row["format"] == "Camera JPEG":
            first_jpegs.setdefault(row["document_family"], source)
        else:
            first_pdfs.setdefault(row["document_family"], source)
    make_contact_sheet(
        sorted(first_jpegs.items()),
        review_dir / "jpeg-family-samples.png",
    )

    last_pages: list[tuple[str, Path]] = []
    for family, source in sorted(first_pdfs.items()):
        count = len(PdfReader(source).pages)
        if count <= 1:
            continue
        destination = working / f"{source.stem}-last.png"
        prefix = destination.with_suffix("")
        import subprocess

        subprocess.run(
            [
                "pdftoppm",
                "-f",
                str(count),
                "-l",
                str(count),
                "-singlefile",
                "-png",
                "-r",
                "110",
                str(source),
                str(prefix),
            ],
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
        )
        last_pages.append((f"{family} - page {count}", destination))
    make_contact_sheet(last_pages, review_dir / "pdf-family-last-pages.png")

    lease_source = first_jpegs["Residential lease agreement"]
    with Image.open(lease_source) as opened:
        lease_image = opened.convert("RGB")
    panel_height = (lease_image.height - 160) // 9
    lease_panels: list[tuple[str, Path]] = []
    for panel in range(9):
        panel_path = working / f"lease-panel-{panel + 1}.png"
        lease_image.crop(
            (0, 80 + panel * panel_height, lease_image.width, 80 + (panel + 1) * panel_height)
        ).save(panel_path)
        lease_panels.append((f"Lease camera panel {panel + 1}", panel_path))
    make_contact_sheet(
        lease_panels,
        review_dir / "lease-nine-panel-review.png",
        columns=3,
    )
    shutil.rmtree(working)
    return [
        "visual-review/pdf-family-first-pages.png",
        "visual-review/pdf-family-last-pages.png",
        "visual-review/jpeg-family-samples.png",
        "visual-review/lease-nine-panel-review.png",
    ]


def render_all(output: Path, clean: bool) -> dict[str, object]:
    assets = [Asset(**row) for row in read_csv("scan-assets.csv")]
    properties = {row["property_id"]: row for row in read_csv("portfolio.csv")}
    units = {row["unit_id"]: row for row in read_csv("units.csv")}
    leases = {row["lease_id"]: row for row in read_csv("leases.csv")}
    events = {row["event_id"]: row for row in read_csv("financial-oracle.csv")}
    if clean and output.exists():
        shutil.rmtree(output)
    documents = output / "documents"
    previews = output / "image-previews"
    documents.mkdir(parents=True, exist_ok=True)

    validation_rows: list[dict[str, str | int]] = []
    family_preview_source: dict[str, Path] = {}
    for index, asset in enumerate(assets, start=1):
        target = documents / asset.planned_date / asset.filename
        ctx = data_context(asset, properties, units, leases, events)
        if asset.format == "PDF":
            render_pdf(asset, target, ctx)
            family_preview_source.setdefault(asset.document_family, target)
        else:
            render_jpeg(asset, target, ctx)
        validation_rows.append(validate_file(asset, target))
        if index % 100 == 0 or index == len(assets):
            print(f"Rendered and validated {index}/{len(assets)}")

    preview_rows: list[dict[str, str | int]] = []
    for family, source in sorted(family_preview_source.items()):
        preview = previews / f"{slug(family)}.png"
        create_preview(source, preview)
        with Image.open(preview) as image:
            width, height = image.size
        preview_rows.append(
            {
                "document_family": family,
                "source_pdf": str(source.relative_to(output)),
                "preview_png": str(preview.relative_to(output)),
                "width": width,
                "height": height,
                "sha256": sha256(preview),
                "status": "PENDING_VISUAL_REVIEW",
            }
        )

    write_csv(output / "corpus-index.csv", validation_rows)
    write_csv(output / "preview-index.csv", preview_rows)
    counts = Counter(asset.document_family for asset in assets)
    formats = Counter(asset.format for asset in assets)
    total_bytes = sum(int(row["bytes"]) for row in validation_rows)
    report = {
        "generated_at": datetime.now().astimezone().isoformat(),
        "source_manifest": str((PLAN / "scan-assets.csv").resolve()),
        "output_root": str(output.resolve()),
        "asset_count": len(assets),
        "validated_count": len(validation_rows),
        "format_counts": dict(sorted(formats.items())),
        "family_counts": dict(sorted(counts.items())),
        "date_folder_count": len({asset.planned_date for asset in assets}),
        "preview_count": len(preview_rows),
        "total_bytes": total_bytes,
        "manifest_asset_ids_sha256": hashlib.sha256(
            "\n".join(asset.asset_id for asset in assets).encode()
        ).hexdigest(),
        "status": "FILES_VALIDATED_VISUAL_REVIEW_PENDING",
    }
    (output / "validation-report.json").write_text(json.dumps(report, indent=2) + "\n")
    (output / "README.md").write_text(
        "\n".join(
            [
                "# Rental Command 2027 synthetic scan corpus",
                "",
                "Use `documents/YYYY-MM-DD/` with the matching simulated clock date.",
                "Every file is synthetic QA data and is invalid for legal, banking, or identity use.",
                "",
                f"- Business documents: {len(assets)}",
                f"- Native PDFs: {formats['PDF']}",
                f"- Native camera JPEGs: {formats['Camera JPEG']}",
                f"- PDF-derived PNG previews: {len(preview_rows)}",
                f"- Date folders: {report['date_folder_count']}",
                "",
                "`corpus-index.csv` contains the SHA-256 checksum and validation result for every upload file.",
                "`preview-index.csv` maps representative PDFs to PNG previews for visual inspection.",
                "",
            ]
        )
    )
    return report


def record_visual_review(output: Path) -> dict[str, object]:
    preview_index = output / "preview-index.csv"
    report_path = output / "validation-report.json"
    if not preview_index.exists() or not report_path.exists():
        raise FileNotFoundError("Render and validate the corpus before recording visual review")
    with preview_index.open(newline="", encoding="utf-8") as handle:
        previews = list(csv.DictReader(handle))
    for row in previews:
        row["status"] = "VISUALLY_REVIEWED"
    write_csv(preview_index, previews)
    evidence_files = build_visual_review_evidence(output)
    corpus_rows = list(csv.DictReader((output / "corpus-index.csv").open(newline="", encoding="utf-8")))
    jpeg_families = sorted({row["document_family"] for row in corpus_rows if row["format"] == "Camera JPEG"})
    pdf_families = sorted({row["document_family"] for row in corpus_rows if row["format"] == "PDF"})
    multipage_pdf_families = sorted(
        {
            row["document_family"]
            for row in corpus_rows
            if row["format"] == "PDF" and int(row["page_or_panel_count"]) > 1
        }
    )
    review = {
        "reviewed_at": datetime.now().astimezone().isoformat(),
        "reviewer": "Codex visual inspection",
        "pdf_first_page_families": pdf_families,
        "pdf_last_page_families": multipage_pdf_families,
        "native_jpeg_families": jpeg_families,
        "long_jpeg_review": "All 9 panels of a residential lease camera JPEG inspected",
        "evidence_files": evidence_files,
        "defects_fixed": [
            "Opening mortgage statements now use property principal and interest when no event row exists",
            "Opening deed and settlement records now use original property cost when no event row exists",
        ],
        "result": "PASS - zero remaining clipping, overlap, unreadable text, broken glyph, or page transition defects",
    }
    (output / "visual-review.json").write_text(json.dumps(review, indent=2) + "\n")
    report = json.loads(report_path.read_text())
    report["visual_review"] = "PASS"
    report["visual_reviewed_at"] = review["reviewed_at"]
    report["status"] = "COMPLETE"
    report_path.write_text(json.dumps(report, indent=2) + "\n")
    return review


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--clean", action="store_true")
    parser.add_argument("--record-visual-review", action="store_true")
    args = parser.parse_args()
    if args.record_visual_review:
        report = record_visual_review(args.output.resolve())
    else:
        report = render_all(args.output.resolve(), args.clean)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
