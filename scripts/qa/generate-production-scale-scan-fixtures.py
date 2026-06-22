#!/usr/bin/env python3
"""Generate sanitized, production-scale scan fixtures for TSK-397.

The output deliberately includes both PDFs and camera-style JPEGs. The web app is
the test surface for this audit, but mobile and web share the extraction engine,
so camera images are required to exercise the vision path.
"""

from __future__ import annotations

import random
import shutil
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont
from reportlab.lib.pagesizes import letter
from reportlab.lib.units import inch
from reportlab.pdfgen import canvas


ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "output" / "qa" / "production-scale-scans"
RNG = random.Random(397)


FIRST_NAMES = [
    "Avery", "Blake", "Casey", "Dana", "Elliot", "Finley", "Gray", "Harper",
    "Indigo", "Jordan", "Kai", "Logan", "Morgan", "Noel", "Oakley", "Parker",
    "Quinn", "Riley", "Sage", "Taylor",
]
LAST_NAMES = [
    "Brooks", "Chen", "Diaz", "Ellis", "Foster", "Garcia", "Hayes", "Ibrahim",
    "Johnson", "Kim", "Lewis", "Miller", "Nguyen", "Ortiz", "Patel", "Reed",
    "Singh", "Thomas", "Vega", "Williams",
]
PROPERTY_NAMES = [
    "Cedar Point Flats", "Riverside Flats", "Maple Court", "Sunset Ridge",
    "Northline Duplexes", "Harbor View Homes", "Oak Terrace", "Summit Row",
]
STREETS = [
    "742 Evergreen St", "1188 Maple Ave", "302 Cedar Rd", "91 Summit Dr",
    "455 River Ln", "19 Oak Ct", "807 Harbor Blvd", "66 Northline Pkwy",
]
VENDORS = [
    "Apex Plumbing", "Green Thumb Landscaping", "ComfortZone HVAC",
    "Hardware House", "Summit Roofing", "Metro Electric", "City Water Utility",
    "Prime Pest Control",
]
ISSUES = [
    "Kitchen sink leaking under cabinet", "Front door lock sticks",
    "HVAC blowing warm air", "Bedroom window will not close",
    "Toilet runs continuously", "Hall light fixture flickers",
    "Garbage disposal jammed", "Washer drain backing up",
]


@dataclass(frozen=True)
class LeaseCase:
    index: int
    lease_number: str
    tenant: str
    property_name: str
    address: str
    city: str
    state: str
    postal: str
    unit: str
    rent: int
    start: str
    end: str


def clean_output() -> None:
    if OUT.exists():
        shutil.rmtree(OUT)
    for name in [
        "01-leases",
        "01-leases-camera",
        "02-expenses",
        "02-expenses-camera",
        "03-payments",
        "03-payments-camera",
        "04-applications",
        "04-applications-camera",
        "05-work-orders",
        "05-work-orders-camera",
    ]:
        (OUT / name).mkdir(parents=True, exist_ok=True)


def build_leases(count: int = 40) -> list[LeaseCase]:
    leases: list[LeaseCase] = []
    for i in range(1, count + 1):
        tenant = f"{FIRST_NAMES[(i - 1) % len(FIRST_NAMES)]} {LAST_NAMES[(i * 3) % len(LAST_NAMES)]}"
        prop_idx = (i - 1) % len(PROPERTY_NAMES)
        unit = f"{1 + ((i - 1) % 4)}{chr(ord('A') + ((i - 1) % 4))}"
        rent = 1050 + (i % 9) * 75
        start_month = 1 + ((i - 1) % 12)
        start = f"2026-{start_month:02d}-01"
        end = f"2027-{start_month:02d}-01"
        leases.append(
            LeaseCase(
                index=i,
                lease_number=f"QA-2026-{i:03d}-{unit}",
                tenant=tenant,
                property_name=PROPERTY_NAMES[prop_idx],
                address=STREETS[prop_idx],
                city="Columbus",
                state="OH",
                postal=f"43{200 + prop_idx:03d}",
                unit=unit,
                rent=rent,
                start=start,
                end=end,
            )
        )
    return leases


def draw_pdf(path: Path, title: str, lines: list[str], footer: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    c = canvas.Canvas(str(path), pagesize=letter)
    width, height = letter
    c.setTitle(title)
    c.setFont("Helvetica-Bold", 17)
    c.drawString(0.75 * inch, height - 0.8 * inch, title)
    c.setFont("Helvetica", 10)
    c.drawString(0.75 * inch, height - 1.05 * inch, "Synthetic QA document. No real people, accounts, or properties.")
    y = height - 1.45 * inch
    c.setFont("Helvetica", 12)
    for line in lines:
        if y < 0.85 * inch:
            c.showPage()
            y = height - 0.85 * inch
            c.setFont("Helvetica", 12)
        c.drawString(0.75 * inch, y, line)
        y -= 0.28 * inch
    c.setFont("Helvetica-Oblique", 9)
    c.drawString(0.75 * inch, 0.5 * inch, footer)
    c.save()


def draw_camera_image(path: Path, title: str, lines: list[str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    background = Image.new("RGB", (1800, 2400), (205, 207, 202))
    paper = Image.new("RGB", (1500, 2050), (255, 254, 248))
    draw = ImageDraw.Draw(paper)
    try:
        title_font = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial Bold.ttf", 54)
        body_font = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 34)
        small_font = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 24)
    except OSError:
        title_font = body_font = small_font = ImageFont.load_default()

    draw.text((80, 80), title, fill=(20, 24, 28), font=title_font)
    draw.text((80, 150), "Synthetic QA camera capture", fill=(80, 80, 80), font=small_font)
    y = 245
    for line in lines:
        draw.text((90, y), line, fill=(25, 25, 25), font=body_font)
        y += 58
    for _ in range(120):
        x = RNG.randrange(60, 1440)
        y = RNG.randrange(210, 1980)
        shade = RNG.randrange(210, 244)
        draw.point((x, y), fill=(shade, shade, shade))

    angle = RNG.uniform(-1.6, 1.6)
    paper = paper.rotate(angle, expand=True, fillcolor=(205, 207, 202)).filter(ImageFilter.UnsharpMask(radius=1.2))
    x = (background.width - paper.width) // 2 + RNG.randrange(-24, 25)
    y = (background.height - paper.height) // 2 + RNG.randrange(-24, 25)
    background.paste(paper, (x, y))
    background.save(path, "JPEG", quality=88, optimize=True)


def lease_lines(lease: LeaseCase) -> list[str]:
    return [
        f"Lease number: {lease.lease_number}",
        f"Tenant name: {lease.tenant}",
        f"Property name: {lease.property_name}",
        f"Premises: {lease.address}, Unit {lease.unit}, {lease.city}, {lease.state} {lease.postal}",
        f"Start date: {lease.start}",
        f"End date: {lease.end}",
        f"Monthly rent: ${lease.rent}.00",
        f"Security deposit: ${lease.rent}.00",
        "Rent due day: 1",
        "Landlord: TSK397 QA Holdings LLC",
        "Signatures: Synthetic landlord and synthetic tenant",
    ]


def expense_lines(lease: LeaseCase, i: int) -> list[str]:
    vendor = VENDORS[i % len(VENDORS)]
    amount = 45 + (i % 15) * 18.75
    return [
        f"Vendor: {vendor}",
        f"Receipt number: RCPT-{i:04d}",
        f"Transaction date: 2026-{1 + (i % 12):02d}-{1 + (i % 25):02d}",
        f"Property: {lease.property_name}",
        f"Address: {lease.address}, Unit {lease.unit}",
        f"Lease reference: {lease.lease_number}",
        f"Category: Repairs",
        f"Subtotal: ${amount - 5:.2f}",
        "Tax: $5.00",
        f"Total amount: ${amount:.2f}",
        "Payment method: Visa 4242",
        "Line items: materials, labor, service fee",
    ]


def payment_lines(lease: LeaseCase, i: int) -> list[str]:
    return [
        "Rent check",
        f"Payer name: {lease.tenant}",
        "Bank name: First QA Bank",
        f"Check number: {8000 + i}",
        f"Payment date: 2026-{1 + (i % 12):02d}-03",
        f"Amount: ${lease.rent}.00",
        f"Memo: rent for lease {lease.lease_number}",
        f"Property: {lease.property_name}",
        f"Unit: {lease.unit}",
    ]


def application_lines(lease: LeaseCase, i: int) -> list[str]:
    applicant = f"{FIRST_NAMES[(i + 5) % len(FIRST_NAMES)]} {LAST_NAMES[(i + 7) % len(LAST_NAMES)]}"
    return [
        "Completed rental application",
        f"Applicant name: {applicant}",
        f"Email: qa.applicant.{i:03d}@example.local",
        f"Phone: 555-01{i % 100:02d}",
        f"Applying for: {lease.property_name} Unit {lease.unit}",
        f"Current employer: QA Employer {i % 9}",
        f"Monthly income: ${3600 + (i % 10) * 250}.00",
        "Pets: none",
        "Government ID last 4: 1000",
        "Notes: synthetic application for extraction testing",
    ]


def work_order_lines(lease: LeaseCase, i: int) -> list[str]:
    issue = ISSUES[i % len(ISSUES)]
    return [
        "Maintenance request",
        f"Tenant: {lease.tenant}",
        f"Property: {lease.property_name}",
        f"Address: {lease.address}, Unit {lease.unit}",
        f"Lease reference: {lease.lease_number}",
        f"Issue: {issue}",
        "Priority: Medium",
        "Requested entry window: Weekday afternoon",
        "Photos attached: one synthetic camera image",
    ]


def make_documents() -> None:
    clean_output()
    leases = build_leases()

    for lease in leases:
        lines = lease_lines(lease)
        name = f"lease-{lease.index:03d}-{lease.unit.lower()}"
        draw_pdf(OUT / "01-leases" / f"{name}.pdf", "Residential Lease Agreement", lines, "TSK-397 lease PDF fixture")
        draw_camera_image(OUT / "01-leases-camera" / f"{name}.jpg", "Lease Photo", lines)

    for i in range(1, 81):
        lease = leases[(i - 1) % len(leases)]
        lines = expense_lines(lease, i)
        draw_pdf(OUT / "02-expenses" / f"expense-{i:03d}.pdf", "Vendor Receipt", lines, "TSK-397 expense PDF fixture")
        draw_camera_image(OUT / "02-expenses-camera" / f"expense-{i:03d}.jpg", "Receipt Photo", lines)

    for i in range(1, 41):
        lease = leases[(i - 1) % len(leases)]
        lines = payment_lines(lease, i)
        draw_pdf(OUT / "03-payments" / f"payment-{i:03d}.pdf", "Rent Check", lines, "TSK-397 rent check PDF fixture")
        draw_camera_image(OUT / "03-payments-camera" / f"payment-{i:03d}.jpg", "Rent Check Photo", lines)

    for i in range(1, 41):
        lease = leases[(i - 1) % len(leases)]
        lines = application_lines(lease, i)
        draw_pdf(OUT / "04-applications" / f"application-{i:03d}.pdf", "Rental Application", lines, "TSK-397 application PDF fixture")
        draw_camera_image(OUT / "04-applications-camera" / f"application-{i:03d}.jpg", "Application Photo", lines)

    for i in range(1, 41):
        lease = leases[(i - 1) % len(leases)]
        lines = work_order_lines(lease, i)
        draw_pdf(OUT / "05-work-orders" / f"work-order-{i:03d}.pdf", "Maintenance Request", lines, "TSK-397 work-order PDF fixture")
        draw_camera_image(OUT / "05-work-orders-camera" / f"work-order-{i:03d}.jpg", "Maintenance Photo", lines)

    manifest = OUT / "MANIFEST.txt"
    counts = {p.name: len([x for x in p.iterdir() if x.is_file()]) for p in sorted(OUT.iterdir()) if p.is_dir()}
    manifest.write_text(
        "\n".join([
            "TSK-397 synthetic scan fixture manifest",
            "No real people, properties, bank accounts, or sensitive data.",
            *(f"{name}: {count}" for name, count in counts.items()),
            f"total: {sum(counts.values())}",
            "",
        ]),
        encoding="utf-8",
    )


if __name__ == "__main__":
    make_documents()
    print(f"Wrote fixtures to {OUT}")
