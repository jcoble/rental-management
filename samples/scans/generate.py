#!/usr/bin/env python3
"""
generate.py – Rental Command sample scan document generator.

Produces ~14 realistic test files under this directory:
  - Clean PDFs (straight-from-program quality)
  - "Photographed" images (PNG/JPG with slight skew, noise, and lower contrast)

Run with:
    .venv/bin/python generate.py          # from samples/scans/
    # or
    python3 samples/scans/generate.py     # from repo root (needs .venv activated or reportlab installed)

Requires: reportlab, Pillow
  pip install reportlab pillow
"""

import io
import math
import os
import random
import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont
from reportlab.lib import colors
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import inch
from reportlab.platypus import (
    HRFlowable,
    Paragraph,
    SimpleDocTemplate,
    Spacer,
    Table,
    TableStyle,
)
from reportlab.platypus.flowables import KeepTogether

OUT_DIR = os.path.dirname(os.path.abspath(__file__))

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def out(name: str) -> str:
    return os.path.join(OUT_DIR, name)


def build_pdf(filename: str, story: list) -> str:
    path = out(filename)
    doc = SimpleDocTemplate(
        path,
        pagesize=letter,
        leftMargin=0.75 * inch,
        rightMargin=0.75 * inch,
        topMargin=0.75 * inch,
        bottomMargin=0.75 * inch,
    )
    doc.build(story)
    return path


def photographed_png(pdf_path: str, out_name: str, skew_deg: float = 2.5, noise: int = 18) -> str:
    """
    Convert a single-page PDF to a 'photographed' PNG by:
    1. Rendering it at 150 DPI via reportlab canvas → in-memory bitmap approach.
       (We re-render the PDF content as a PIL image using a page-image approach.)
    2. Applying slight rotation, contrast reduction, and random pixel noise.

    Because we cannot easily rasterize the PDF without poppler/ghostscript,
    we re-render the document to a PIL image ourselves using reportlab's canvas
    and a custom approach: render each story element to a bitmap via a
    ReportLab renderPM call if available, otherwise render to PNG via svg path.
    Fallback: just load the PDF bytes and encode as fake JPEG noise image.
    """
    try:
        from reportlab.graphics import renderPM
        from reportlab.lib.utils import ImageReader

        # Try to rasterize using rlPyCairo or similar
        # If not available, use a fallback raster approach
    except ImportError:
        pass

    return _photographed_from_pdf_fallback(pdf_path, out_name, skew_deg, noise)


def _photographed_from_pdf_fallback(pdf_path: str, out_name: str, skew_deg: float, noise: int) -> str:
    """
    Fallback: open the PDF as raw bytes, create a white page image,
    stamp 'photographed' style overlay text from file name, and apply effects.
    Since we don't have poppler, we render a white linen page with the PDF filename
    and a note that the content is inside the accompanying clean PDF.

    For actual visual realism, we render the document content directly as a PIL image
    by re-creating the layout using PIL's draw primitives based on the document type.
    """
    # We'll call the specific render functions instead
    return None  # signal to caller to use dedicated image renderer


def add_photo_effects(img: Image.Image, skew_deg: float = 2.5, noise: int = 18) -> Image.Image:
    """Apply photographic imperfections to a PIL Image."""
    # Slight rotation (skew)
    img = img.rotate(skew_deg, expand=True, fillcolor=(240, 238, 230), resample=Image.BICUBIC)

    # Slight contrast/brightness reduction
    from PIL import ImageEnhance
    img = ImageEnhance.Contrast(img).enhance(0.82)
    img = ImageEnhance.Brightness(img).enhance(0.93)

    # Add grain/noise
    import numpy as np
    arr = np.array(img, dtype=np.int16)
    grain = np.random.randint(-noise, noise + 1, arr.shape, dtype=np.int16)
    arr = np.clip(arr + grain, 0, 255).astype(np.uint8)
    img = Image.fromarray(arr)

    # Subtle blur (camera focus falloff)
    img = img.filter(ImageFilter.GaussianBlur(radius=0.4))

    return img


def render_page_to_image(draw_fn, width_px: int = 1275, height_px: int = 1650) -> Image.Image:
    """Create a white page image and call draw_fn(draw, width, height)."""
    img = Image.new("RGB", (width_px, height_px), color=(252, 251, 248))
    draw = ImageDraw.Draw(img)
    draw_fn(draw, width_px, height_px)
    return img


def save_jpg(img: Image.Image, filename: str, quality: int = 82) -> str:
    path = out(filename)
    img.convert("RGB").save(path, "JPEG", quality=quality)
    return path


def save_png(img: Image.Image, filename: str) -> str:
    path = out(filename)
    img.save(path, "PNG")
    return path


# ---------------------------------------------------------------------------
# Font helpers  (PIL uses default bitmap font; we do best-effort)
# ---------------------------------------------------------------------------

def get_font(size: int):
    try:
        return ImageFont.truetype("/System/Library/Fonts/Helvetica.ttc", size)
    except Exception:
        try:
            return ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", size)
        except Exception:
            return ImageFont.load_default()


def get_font_bold(size: int):
    try:
        return ImageFont.truetype("/System/Library/Fonts/Helvetica.ttc", size, index=1)
    except Exception:
        return get_font(size)


def text(draw: ImageDraw.Draw, xy, msg: str, size: int = 22, bold: bool = False, color=(20, 20, 20), anchor="la"):
    fnt = get_font_bold(size) if bold else get_font(size)
    draw.text(xy, msg, font=fnt, fill=color, anchor=anchor)


def hline(draw: ImageDraw.Draw, y: int, x0: int = 60, x1: int = 1215, width: int = 2, color=(100, 100, 100)):
    draw.line([(x0, y), (x1, y)], fill=color, width=width)


# ---------------------------------------------------------------------------
# ReportLab style helpers
# ---------------------------------------------------------------------------

styles = getSampleStyleSheet()

def rl_h1(txt):
    s = ParagraphStyle("h1s", parent=styles["Normal"], fontSize=18, fontName="Helvetica-Bold",
                       spaceAfter=6, spaceBefore=6, leading=22)
    return Paragraph(txt, s)

def rl_h2(txt):
    s = ParagraphStyle("h2s", parent=styles["Normal"], fontSize=13, fontName="Helvetica-Bold",
                       spaceAfter=4, spaceBefore=8, leading=16)
    return Paragraph(txt, s)

def rl_body(txt, size=10):
    s = ParagraphStyle("bods", parent=styles["Normal"], fontSize=size, fontName="Helvetica",
                       spaceAfter=3, leading=14)
    return Paragraph(txt, s)

def rl_label(txt):
    s = ParagraphStyle("labs", parent=styles["Normal"], fontSize=9, fontName="Helvetica",
                       textColor=colors.HexColor("#555555"), spaceAfter=2, leading=12)
    return Paragraph(txt, s)

def spacer(h=0.15):
    return Spacer(1, h * inch)

def hr():
    return HRFlowable(width="100%", thickness=1, color=colors.HexColor("#cccccc"), spaceAfter=6, spaceBefore=6)


# ===========================================================================
# DOCUMENT 1 – Lease Agreement (clean PDF + photographed JPG)
# ===========================================================================

def make_lease_pdf():
    story = [
        rl_h1("RESIDENTIAL LEASE AGREEMENT"),
        hr(),
        rl_body("<b>Property:</b> 4812 Westview Drive, Unit 2 — Westview Four-Plex, Columbus, OH 43235"),
        rl_body("<b>Landlord / Owner:</b> Greenleaf Residential LLC, 900 High Street Suite 110, Columbus OH 43215"),
        rl_body("<b>Tenant(s):</b> Marcus Williams"),
        spacer(0.1),
        rl_h2("1. TERM"),
        rl_body("This Lease commences on <b>August 1, 2025</b> and ends on <b>July 31, 2026</b> (12-month term). "
                "Thereafter, tenancy shall convert to a month-to-month arrangement unless either party provides "
                "30 days' written notice of termination."),
        spacer(0.1),
        rl_h2("2. RENT"),
        rl_body("Monthly rent is <b>$1,250.00</b>, due on the <b>1st day</b> of each calendar month. "
                "Rent is payable by check, ACH, or the owner's online portal. A late fee of $75.00 applies "
                "if rent is received after the 5th of the month."),
        spacer(0.1),
        rl_h2("3. SECURITY DEPOSIT"),
        rl_body("Tenant has deposited <b>$1,250.00</b> with Landlord as a security deposit, held in a separate "
                "escrow account. Deposit will be returned within 30 days of vacating, less any deductions "
                "per Ohio Revised Code § 5321.16."),
        spacer(0.1),
        rl_h2("4. UTILITIES"),
        rl_body("Tenant is responsible for: Electric (AEP Ohio), Gas (Columbia Gas), Internet. "
                "Landlord provides: Water/Sewer, Trash removal, Common-area landscaping."),
        spacer(0.1),
        rl_h2("5. OCCUPANTS"),
        rl_body("Only Marcus Williams is an authorized occupant. Guests staying more than 7 consecutive days "
                "must receive prior written approval from Landlord."),
        spacer(0.1),
        rl_h2("6. PETS"),
        rl_body("No pets permitted without prior written consent and a non-refundable pet fee of $300."),
        spacer(0.1),
        rl_h2("7. MAINTENANCE & REPAIRS"),
        rl_body("Tenant shall keep the premises clean and notify Landlord promptly of any needed repairs. "
                "Tenant is responsible for minor maintenance (light bulb replacement, air filters monthly). "
                "Landlord is responsible for structural and mechanical systems."),
        spacer(0.1),
        rl_h2("8. RENEWAL / NOTICE"),
        rl_body("To vacate at end of term, Tenant must give written notice no later than 30 days before expiry. "
                "Failure to give notice converts the lease to month-to-month automatically."),
        spacer(0.3),
        hr(),
        rl_body("<b>Tenant Signature:</b> _________________________   Date: ___________"),
        spacer(0.15),
        rl_body("<b>Landlord / Authorized Agent:</b> _________________________   Date: ___________"),
        spacer(0.1),
        rl_label("Greenleaf Residential LLC  ·  (614) 555-0182  ·  leasing@greenleafresidential.com"),
    ]
    return build_pdf("lease_westview_unit2_marcus_williams.pdf", story)


def draw_lease_image(draw, W, H):
    y = 55
    text(draw, (W // 2, y), "RESIDENTIAL LEASE AGREEMENT", size=36, bold=True, anchor="mt")
    y += 55
    hline(draw, y)
    y += 30
    text(draw, (60, y), "Property:", size=22, bold=True)
    text(draw, (200, y), "4812 Westview Drive, Unit 2 — Westview Four-Plex, Columbus OH 43235", size=22)
    y += 40
    text(draw, (60, y), "Tenant(s):", size=22, bold=True)
    text(draw, (210, y), "Marcus Williams", size=22)
    y += 40
    text(draw, (60, y), "Landlord:", size=22, bold=True)
    text(draw, (190, y), "Greenleaf Residential LLC", size=22)
    y += 50
    hline(draw, y)
    y += 30

    sections = [
        ("1. TERM", "Commences August 1, 2025  –  Ends July 31, 2026  (12-month term)"),
        ("2. RENT", "Monthly rent: $1,250.00  |  Due: 1st of each month  |  Late fee after 5th: $75.00"),
        ("3. SECURITY DEPOSIT", "Deposit: $1,250.00  held in escrow per ORC § 5321.16"),
        ("4. UTILITIES", "Tenant: Electric, Gas, Internet    Landlord: Water/Sewer, Trash"),
        ("5. OCCUPANTS", "Authorized: Marcus Williams only. Guests >7 days require written approval."),
        ("6. PETS", "No pets without prior written consent + $300 non-refundable fee."),
        ("7. MAINTENANCE", "Tenant: light bulbs, air filters. Landlord: structural + mechanical systems."),
    ]
    for title, body in sections:
        text(draw, (60, y), title, size=21, bold=True)
        y += 34
        text(draw, (80, y), body, size=19)
        y += 44

    hline(draw, y + 10)
    y += 40
    text(draw, (60, y), "Tenant Signature: ___________________________   Date: ____________", size=20)
    y += 50
    text(draw, (60, y), "Landlord / Agent:  ___________________________   Date: ____________", size=20)
    y += 55
    text(draw, (60, y), "Greenleaf Residential LLC  ·  (614) 555-0182  ·  leasing@greenleafresidential.com",
         size=17, color=(100, 100, 100))


# ===========================================================================
# DOCUMENT 2 – Rent Receipt (clean PDF + photographed JPG, slightly skewed)
# ===========================================================================

def make_rent_receipt_pdf():
    story = [
        rl_h1("RENT RECEIPT"),
        hr(),
        spacer(0.1),
        rl_body("<b>Receipt No.:</b> RCP-2025-0847"),
        rl_body("<b>Date Received:</b> September 3, 2025"),
        rl_body("<b>Received From:</b> Derek Johnson"),
        rl_body("<b>Property / Unit:</b> 221 Clintonville Townhome — Unit A, Columbus OH 43202"),
        spacer(0.2),
        hr(),
        spacer(0.1),
        Table(
            [
                ["Description", "Amount"],
                ["Monthly Rent — September 2025", "$1,150.00"],
                ["Late Fee (received after 5th)", "$0.00"],
                ["", ""],
                ["TOTAL RECEIVED", "$1,150.00"],
            ],
            colWidths=[4.5 * inch, 2.5 * inch],
            style=TableStyle([
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#2d4a7a")),
                ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
                ("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"),
                ("FONTSIZE", (0, 0), (-1, 0), 11),
                ("ALIGN", (1, 0), (1, -1), "RIGHT"),
                ("FONTNAME", (0, 4), (-1, 4), "Helvetica-Bold"),
                ("LINEABOVE", (0, 4), (-1, 4), 1, colors.black),
                ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.HexColor("#f5f7fa"), colors.white]),
                ("TOPPADDING", (0, 0), (-1, -1), 8),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 8),
                ("LEFTPADDING", (0, 0), (0, -1), 10),
            ]),
        ),
        spacer(0.3),
        rl_body("<b>Payment Method:</b> Personal Check #1082"),
        rl_body("<b>Balance Due:</b> $0.00"),
        spacer(0.4),
        hr(),
        rl_body("Received by: _________________________   Signature: _________________________"),
        spacer(0.15),
        rl_label("Greenleaf Residential LLC  ·  (614) 555-0182  ·  payments@greenleafresidential.com"),
    ]
    return build_pdf("rent_receipt_derek_johnson_sep2025.pdf", story)


def draw_rent_receipt_image(draw, W, H):
    y = 50
    text(draw, (W // 2, y), "RENT RECEIPT", size=40, bold=True, anchor="mt")
    y += 60
    hline(draw, y)
    y += 35
    fields = [
        ("Receipt No.", "RCP-2025-0847"),
        ("Date Received", "September 3, 2025"),
        ("Received From", "Derek Johnson"),
        ("Property / Unit", "221 Clintonville Townhome — Unit A, Columbus OH 43202"),
    ]
    for label, val in fields:
        text(draw, (60, y), f"{label}:", size=22, bold=True)
        text(draw, (350, y), val, size=22)
        y += 42
    y += 20
    hline(draw, y)
    y += 30
    # Table header
    draw.rectangle([(60, y), (1215, y + 44)], fill=(45, 74, 122))
    text(draw, (80, y + 10), "Description", size=21, bold=True, color=(255, 255, 255))
    text(draw, (1200, y + 10), "Amount", size=21, bold=True, color=(255, 255, 255), anchor="ra")
    y += 44
    rows = [
        ("Monthly Rent — September 2025", "$1,150.00"),
        ("Late Fee (received after 5th)", "$0.00"),
    ]
    bg = [True, False]
    for i, (desc, amt) in enumerate(rows):
        bgc = (245, 247, 250) if bg[i] else (255, 255, 255)
        draw.rectangle([(60, y), (1215, y + 40)], fill=bgc)
        text(draw, (80, y + 8), desc, size=20)
        text(draw, (1200, y + 8), amt, size=20, anchor="ra")
        y += 40
    hline(draw, y, width=2)
    y += 10
    text(draw, (80, y), "TOTAL RECEIVED", size=22, bold=True)
    text(draw, (1200, y), "$1,150.00", size=22, bold=True, anchor="ra")
    y += 55
    text(draw, (60, y), "Payment Method:", size=22, bold=True)
    text(draw, (310, y), "Personal Check #1082", size=22)
    y += 42
    text(draw, (60, y), "Balance Due:", size=22, bold=True)
    text(draw, (235, y), "$0.00", size=22)
    y += 70
    hline(draw, y)
    y += 30
    text(draw, (60, y), "Received by: ___________________________   Signature: ___________________________", size=20)
    y += 55
    text(draw, (60, y), "Greenleaf Residential LLC  ·  (614) 555-0182  ·  payments@greenleafresidential.com",
         size=17, color=(100, 100, 100))


# ===========================================================================
# DOCUMENT 3 – Vendor Invoice: ComfortZone HVAC (clean PDF + noisy JPG)
# ===========================================================================

def make_hvac_invoice_pdf():
    story = [
        rl_h1("COMFORTZONE HVAC SERVICES"),
        rl_body("3301 Morse Road, Columbus OH 43231  ·  (614) 555-0224  ·  info@comfortzonehvac.com  ·  Lic# HVAC-OH-44821"),
        hr(),
        spacer(0.1),
        Table(
            [
                ["INVOICE", ""],
                ["Invoice No.:", "INV-CZ-20251014"],
                ["Invoice Date:", "October 14, 2025"],
                ["Due Date:", "November 13, 2025 (Net 30)"],
                ["Bill To:", "Greenleaf Residential LLC"],
                ["", "4812 Westview Drive, Columbus OH 43235"],
                ["Property:", "Westview Four-Plex — Unit 3 (HVAC system)"],
            ],
            colWidths=[2.0 * inch, 5.0 * inch],
            style=TableStyle([
                ("FONTNAME", (0, 0), (0, -1), "Helvetica-Bold"),
                ("FONTSIZE", (0, 0), (-1, -1), 10),
                ("TOPPADDING", (0, 0), (-1, -1), 5),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
                ("SPAN", (0, 0), (1, 0)),
                ("FONTSIZE", (0, 0), (0, 0), 14),
            ]),
        ),
        spacer(0.2),
        rl_h2("Services Rendered"),
        Table(
            [
                ["#", "Description", "Qty", "Unit Price", "Total"],
                ["1", "Diagnostic inspection — HVAC unit failure (Unit 3)", "1", "$95.00", "$95.00"],
                ["2", "Replace capacitor (35/5 MFD dual run)", "1", "$145.00", "$145.00"],
                ["3", "Replace contactor relay", "1", "$110.00", "$110.00"],
                ["4", "Refrigerant recharge (R-410A, 1.5 lbs)", "1.5 lbs", "$65.00/lb", "$97.50"],
                ["5", "Labor (2.5 hours @ $85/hr)", "2.5 hrs", "$85.00", "$212.50"],
                ["", "", "", "Subtotal", "$660.00"],
                ["", "", "", "Tax (0%)", "$0.00"],
                ["", "", "", "TOTAL DUE", "$660.00"],
            ],
            colWidths=[0.35 * inch, 3.5 * inch, 0.75 * inch, 1.2 * inch, 1.2 * inch],
            style=TableStyle([
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#1a3a5c")),
                ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
                ("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"),
                ("FONTNAME", (0, -1), (-1, -1), "Helvetica-Bold"),
                ("LINEABOVE", (0, -3), (-1, -3), 1, colors.black),
                ("ALIGN", (2, 0), (-1, -1), "RIGHT"),
                ("ROWBACKGROUNDS", (0, 1), (-1, -4), [colors.HexColor("#f2f5f9"), colors.white]),
                ("TOPPADDING", (0, 0), (-1, -1), 7),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 7),
                ("LEFTPADDING", (1, 0), (1, -1), 8),
            ]),
        ),
        spacer(0.3),
        rl_body("<b>Payment Terms:</b> Net 30. Checks payable to ComfortZone HVAC Services LLC. "
                "ACH available on request."),
        rl_body("<b>Technician:</b> Ryan T. Meredith  |  <b>Work Completed:</b> October 14, 2025"),
        spacer(0.15),
        rl_label("Thank you for your business! Questions? (614) 555-0224 or billing@comfortzonehvac.com"),
    ]
    return build_pdf("vendor_invoice_comfortzone_hvac_oct2025.pdf", story)


def draw_hvac_invoice_image(draw, W, H):
    y = 45
    text(draw, (60, y), "COMFORTZONE HVAC SERVICES", size=32, bold=True)
    y += 42
    text(draw, (60, y), "3301 Morse Rd, Columbus OH 43231  ·  (614) 555-0224  ·  Lic# HVAC-OH-44821",
         size=19, color=(80, 80, 80))
    y += 40
    hline(draw, y)
    y += 30
    text(draw, (60, y), "INVOICE", size=28, bold=True)
    y += 45
    pairs = [
        ("Invoice No.", "INV-CZ-20251014"),
        ("Invoice Date", "October 14, 2025"),
        ("Due Date", "November 13, 2025 (Net 30)"),
        ("Bill To", "Greenleaf Residential LLC — 4812 Westview Drive, Columbus OH 43235"),
        ("Property", "Westview Four-Plex — Unit 3 (HVAC System)"),
    ]
    for k, v in pairs:
        text(draw, (60, y), f"{k}:", size=21, bold=True)
        text(draw, (300, y), v, size=21)
        y += 38
    y += 15
    hline(draw, y)
    y += 25
    text(draw, (60, y), "Services Rendered", size=24, bold=True)
    y += 38
    draw.rectangle([(60, y), (1215, y + 40)], fill=(26, 58, 92))
    cols = [(80, "Description"), (670, "Qty"), (790, "Unit Price"), (980, "Total")]
    for cx, ct in cols:
        text(draw, (cx, y + 8), ct, size=20, bold=True, color=(255, 255, 255))
    y += 40
    line_items = [
        ("Diagnostic inspection — HVAC failure, Unit 3", "1", "$95.00", "$95.00"),
        ("Replace 35/5 MFD dual-run capacitor", "1", "$145.00", "$145.00"),
        ("Replace contactor relay", "1", "$110.00", "$110.00"),
        ("Refrigerant recharge R-410A (1.5 lbs)", "1.5 lbs", "$65.00/lb", "$97.50"),
        ("Labor (2.5 hrs @ $85/hr)", "2.5 hrs", "$85.00", "$212.50"),
    ]
    for i, (desc, qty, up, tot) in enumerate(line_items):
        bgc = (242, 245, 249) if i % 2 == 0 else (255, 255, 255)
        draw.rectangle([(60, y), (1215, y + 38)], fill=bgc)
        text(draw, (80, y + 8), desc, size=19)
        text(draw, (695, y + 8), qty, size=19, anchor="ra")
        text(draw, (890, y + 8), up, size=19, anchor="ra")
        text(draw, (1205, y + 8), tot, size=19, anchor="ra")
        y += 38
    hline(draw, y, width=2)
    y += 15
    totals = [("Subtotal", "$660.00"), ("Tax (0%)", "$0.00"), ("TOTAL DUE", "$660.00")]
    for i, (label, val) in enumerate(totals):
        bold = i == 2
        text(draw, (890, y), label, size=22 if bold else 20, bold=bold, anchor="ra")
        text(draw, (1205, y), val, size=22 if bold else 20, bold=bold, anchor="ra")
        y += 40
    y += 20
    text(draw, (60, y), "Payment Terms: Net 30   |   Technician: Ryan T. Meredith   |   Work Completed: Oct 14, 2025",
         size=19, color=(60, 60, 60))
    y += 40
    text(draw, (60, y), "Questions? (614) 555-0224  ·  billing@comfortzonehvac.com",
         size=17, color=(120, 120, 120))


# ===========================================================================
# DOCUMENT 4 – Utility Bill (AEP Ohio) — Expense
# ===========================================================================

def make_utility_bill_pdf():
    story = [
        rl_h1("AEP OHIO"),
        rl_body("P.O. Box 371496, Pittsburgh PA 15250  ·  Customer Service: 1-800-277-2177  ·  aepohio.com"),
        hr(),
        spacer(0.1),
        rl_body("<b>Account No.:</b> 7621-8840-0034"),
        rl_body("<b>Service Address:</b> 4812 Westview Drive, Columbus OH 43235"),
        rl_body("<b>Bill Date:</b> November 5, 2025"),
        rl_body("<b>Due Date:</b> November 26, 2025"),
        rl_body("<b>Account Holder:</b> Greenleaf Residential LLC (Common Area)"),
        spacer(0.2),
        hr(),
        rl_h2("Electric Usage Summary"),
        Table(
            [
                ["Billing Period", "Meter Read", "kWh Used", "Rate", "Charge"],
                ["Oct 5 – Nov 4, 2025", "29,441 → 30,086", "645 kWh", "$0.1312/kWh", "$84.62"],
                ["Distribution charge", "", "", "", "$18.40"],
                ["Customer charge", "", "", "", "$7.50"],
                ["State excise tax", "", "", "", "$3.12"],
                ["Previous balance", "", "", "", "$0.00"],
                ["TOTAL DUE", "", "", "", "$113.64"],
            ],
            colWidths=[2.0 * inch, 1.5 * inch, 1.0 * inch, 1.2 * inch, 1.3 * inch],
            style=TableStyle([
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#005f87")),
                ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
                ("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"),
                ("FONTNAME", (0, -1), (-1, -1), "Helvetica-Bold"),
                ("LINEABOVE", (0, -1), (-1, -1), 1.5, colors.black),
                ("ALIGN", (2, 0), (-1, -1), "RIGHT"),
                ("ROWBACKGROUNDS", (0, 1), (-1, -2), [colors.HexColor("#eef3f8"), colors.white]),
                ("TOPPADDING", (0, 0), (-1, -1), 7),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 7),
                ("LEFTPADDING", (0, 0), (0, -1), 8),
            ]),
        ),
        spacer(0.3),
        rl_body("Please remit <b>$113.64</b> by <b>November 26, 2025</b> to avoid a late charge. "
                "Pay online at aepohio.com or by phone at 1-800-277-2177."),
        spacer(0.15),
        rl_label("This bill is for common-area service only. Questions? 1-800-277-2177."),
    ]
    return build_pdf("utility_bill_aep_ohio_nov2025.pdf", story)


def draw_utility_bill_image(draw, W, H):
    y = 45
    text(draw, (60, y), "AEP OHIO", size=40, bold=True, color=(0, 95, 135))
    y += 52
    text(draw, (60, y), "P.O. Box 371496, Pittsburgh PA 15250  ·  1-800-277-2177  ·  aepohio.com",
         size=19, color=(80, 80, 80))
    y += 40
    hline(draw, y, color=(0, 95, 135))
    y += 30
    pairs = [
        ("Account No.", "7621-8840-0034"),
        ("Service Address", "4812 Westview Drive, Columbus OH 43235"),
        ("Bill Date", "November 5, 2025"),
        ("Due Date", "November 26, 2025"),
        ("Account Holder", "Greenleaf Residential LLC (Common Area)"),
    ]
    for k, v in pairs:
        text(draw, (60, y), f"{k}:", size=21, bold=True)
        text(draw, (330, y), v, size=21)
        y += 40
    y += 15
    hline(draw, y)
    y += 25
    text(draw, (60, y), "Electric Usage Summary", size=26, bold=True)
    y += 40
    draw.rectangle([(60, y), (1215, y + 40)], fill=(0, 95, 135))
    header_cols = [(80, "Billing Period"), (480, "Meter Read"), (700, "kWh"), (850, "Rate"), (1100, "Charge")]
    for cx, ct in header_cols:
        text(draw, (cx, y + 8), ct, size=19, bold=True, color=(255, 255, 255))
    y += 40
    rows_data = [
        ("Oct 5 – Nov 4, 2025", "29,441 → 30,086", "645 kWh", "$0.1312/kWh", "$84.62"),
        ("Distribution charge", "", "", "", "$18.40"),
        ("Customer charge", "", "", "", "$7.50"),
        ("State excise tax", "", "", "", "$3.12"),
        ("Previous balance", "", "", "", "$0.00"),
    ]
    for i, row in enumerate(rows_data):
        bgc = (238, 243, 248) if i % 2 == 0 else (255, 255, 255)
        draw.rectangle([(60, y), (1215, y + 38)], fill=bgc)
        for j, (cx, _) in enumerate(header_cols):
            text(draw, (cx, y + 8), row[j], size=18)
        y += 38
    hline(draw, y, width=2)
    y += 12
    text(draw, (80, y), "TOTAL DUE", size=24, bold=True)
    text(draw, (1205, y), "$113.64", size=24, bold=True, anchor="ra")
    y += 65
    text(draw, (60, y), "Please remit $113.64 by November 26, 2025 to avoid a late charge.", size=20)
    y += 42
    text(draw, (60, y), "Pay online at aepohio.com  or  1-800-277-2177", size=19, color=(0, 95, 135))


# ===========================================================================
# DOCUMENT 5 – W-9 (vendor tax form) — clean PDF only (official look)
# ===========================================================================

def make_w9_pdf():
    story = [
        rl_h1("Form W-9 — Request for Taxpayer Identification Number and Certification"),
        rl_label("(Rev. October 2018)  Department of the Treasury — Internal Revenue Service"),
        hr(),
        spacer(0.1),
        rl_h2("Part I — Taxpayer Identification"),
        rl_body("<b>1. Name (as shown on income tax return):</b>  Apex Plumbing & Mechanical LLC"),
        rl_body("<b>2. Business name (if different):</b>  Apex Plumbing LLC"),
        rl_body("<b>3. Federal tax classification:</b>  ☑ LLC  —  Tax classification: S"),
        rl_body("<b>4. Address:</b>  1822 Neil Avenue, Columbus OH 43201"),
        rl_body("<b>5. City, State, ZIP:</b>  Columbus, OH  43201"),
        rl_body("<b>6. Account numbers (optional):</b>  VENDOR-APX-0041"),
        spacer(0.2),
        rl_h2("Part II — Taxpayer Identification Number (TIN)"),
        rl_body("<b>Employer Identification Number (EIN):</b>  47-3920154"),
        spacer(0.2),
        rl_h2("Part III — Certification"),
        rl_body(
            "Under penalties of perjury, I certify that: (1) the number shown on this form is my correct "
            "taxpayer identification number, (2) I am not subject to backup withholding, (3) I am a U.S. "
            "person, and (4) the FATCA code(s) entered on this form (if any) indicating that I am exempt "
            "from FATCA reporting is correct."
        ),
        spacer(0.3),
        hr(),
        rl_body("<b>Signature of U.S. person:</b>  ___________________________   <b>Date:</b>  ___________"),
        spacer(0.1),
        rl_body("<b>Print Name:</b>  Daniel K. Osei   <b>Title:</b>  Owner"),
        spacer(0.1),
        rl_label("Apex Plumbing & Mechanical LLC  ·  (614) 555-0317  ·  accounting@apexplumbing.com"),
    ]
    return build_pdf("w9_apex_plumbing.pdf", story)


def draw_w9_image(draw, W, H):
    y = 45
    text(draw, (W // 2, y), "Form W-9", size=36, bold=True, anchor="mt")
    y += 48
    text(draw, (W // 2, y),
         "Request for Taxpayer Identification Number and Certification", size=22, anchor="mt")
    y += 32
    text(draw, (W // 2, y), "(Rev. October 2018)  Department of the Treasury — Internal Revenue Service",
         size=17, color=(90, 90, 90), anchor="mt")
    y += 40
    hline(draw, y)
    y += 28

    fields = [
        ("1. Name (as shown on income tax return)", "Apex Plumbing & Mechanical LLC"),
        ("2. Business name (if different)", "Apex Plumbing LLC"),
        ("3. Federal tax classification", "LLC — Tax classification: S"),
        ("4. Address", "1822 Neil Avenue, Columbus OH 43201"),
        ("5. City, State, ZIP", "Columbus, OH  43201"),
        ("6. Account numbers (optional)", "VENDOR-APX-0041"),
    ]
    for label, val in fields:
        text(draw, (60, y), label + ":", size=19, bold=True)
        text(draw, (60, y + 26), val, size=20)
        y += 72
    y += 10
    hline(draw, y)
    y += 25
    text(draw, (60, y), "Part II — Taxpayer Identification Number (TIN)", size=24, bold=True)
    y += 44
    text(draw, (60, y), "Employer Identification Number (EIN):", size=21, bold=True)
    text(draw, (600, y), "47-3920154", size=21)
    y += 55
    hline(draw, y)
    y += 25
    text(draw, (60, y), "Part III — Certification", size=24, bold=True)
    y += 40
    cert_text = ("Under penalties of perjury, I certify that the number shown is my correct TIN, "
                 "I am not subject to backup withholding, and I am a U.S. person.")
    text(draw, (60, y), cert_text, size=18, color=(50, 50, 50))
    y += 60
    hline(draw, y)
    y += 28
    text(draw, (60, y), "Signature: ___________________________   Date: ____________", size=20)
    y += 42
    text(draw, (60, y), "Print Name: Daniel K. Osei    Title: Owner", size=20)
    y += 45
    text(draw, (60, y), "Apex Plumbing & Mechanical LLC  ·  (614) 555-0317  ·  accounting@apexplumbing.com",
         size=17, color=(100, 100, 100))


# ===========================================================================
# DOCUMENT 6 – Rental Application — Application draft
# ===========================================================================

def make_rental_application_pdf():
    story = [
        rl_h1("RENTAL APPLICATION"),
        rl_body("Greenleaf Residential LLC  |  900 High St Suite 110, Columbus OH 43215  |  (614) 555-0182"),
        hr(),
        rl_h2("Applicant Information"),
        Table(
            [
                ["Full Name:", "Jasmine L. Carter"],
                ["Date of Birth:", "March 14, 1993"],
                ["Phone:", "(614) 555-7741"],
                ["Email:", "jasmine.carter@email.com"],
                ["Current Address:", "908 North Fourth St, Apt 3B, Columbus OH 43201"],
                ["How long at current address?", "2 years 4 months"],
                ["Reason for moving:", "Lease not renewed — building sold"],
            ],
            colWidths=[2.0 * inch, 5.0 * inch],
            style=TableStyle([
                ("FONTNAME", (0, 0), (0, -1), "Helvetica-Bold"),
                ("FONTSIZE", (0, 0), (-1, -1), 10),
                ("TOPPADDING", (0, 0), (-1, -1), 6),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 6),
                ("ROWBACKGROUNDS", (0, 0), (-1, -1), [colors.HexColor("#f5f7fa"), colors.white]),
                ("LEFTPADDING", (0, 0), (-1, -1), 8),
            ]),
        ),
        spacer(0.15),
        rl_h2("Employment & Income"),
        Table(
            [
                ["Employer:", "Nationwide Children's Hospital"],
                ["Position:", "Registered Nurse (RN)"],
                ["Employment Status:", "Full-time, permanent"],
                ["Start Date:", "June 2020"],
                ["Monthly Gross Income:", "$5,480.00"],
                ["Supervisor / HR Contact:", "Teresa Nguyen, (614) 555-8800 ext. 4421"],
            ],
            colWidths=[2.0 * inch, 5.0 * inch],
            style=TableStyle([
                ("FONTNAME", (0, 0), (0, -1), "Helvetica-Bold"),
                ("FONTSIZE", (0, 0), (-1, -1), 10),
                ("TOPPADDING", (0, 0), (-1, -1), 6),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 6),
                ("ROWBACKGROUNDS", (0, 0), (-1, -1), [colors.HexColor("#f5f7fa"), colors.white]),
                ("LEFTPADDING", (0, 0), (-1, -1), 8),
            ]),
        ),
        spacer(0.15),
        rl_h2("Rental History"),
        rl_body("<b>Previous Landlord:</b>  Brookfield Property Management, (614) 555-1230"),
        rl_body("<b>Address:</b>  908 N Fourth St, Columbus OH  |  <b>Dates:</b>  Feb 2023 – Present"),
        rl_body("<b>Monthly Rent:</b>  $975  |  <b>Departed in good standing?</b>  Yes"),
        spacer(0.15),
        rl_h2("References"),
        rl_body("1.  Dr. Anita Solis  |  (614) 555-0042  |  Colleague"),
        rl_body("2.  James Carter     |  (614) 555-3310  |  Family (brother)"),
        spacer(0.15),
        rl_h2("Background & Authorization"),
        rl_body("☑ I authorize Greenleaf Residential LLC to obtain a credit report and criminal background check."),
        rl_body("☑ All information provided is true and accurate to the best of my knowledge."),
        spacer(0.2),
        hr(),
        rl_body("<b>Applicant Signature:</b>  ___________________________   <b>Date:</b>  July 15, 2025"),
        spacer(0.1),
        rl_label("Application fee $50 non-refundable. Processing time 2–3 business days. (614) 555-0182"),
    ]
    return build_pdf("rental_application_jasmine_carter.pdf", story)


def draw_rental_application_image(draw, W, H):
    y = 45
    text(draw, (W // 2, y), "RENTAL APPLICATION", size=36, bold=True, anchor="mt")
    y += 48
    text(draw, (W // 2, y),
         "Greenleaf Residential LLC  ·  900 High St Suite 110, Columbus OH 43215  ·  (614) 555-0182",
         size=19, anchor="mt", color=(80, 80, 80))
    y += 40
    hline(draw, y)
    y += 28
    text(draw, (60, y), "Applicant Information", size=26, bold=True)
    y += 40
    app_fields = [
        ("Full Name", "Jasmine L. Carter"),
        ("Date of Birth", "March 14, 1993"),
        ("Phone", "(614) 555-7741"),
        ("Email", "jasmine.carter@email.com"),
        ("Current Address", "908 North Fourth St, Apt 3B, Columbus OH 43201"),
        ("Time at Address", "2 years 4 months"),
        ("Reason for Moving", "Lease not renewed — building sold"),
    ]
    for i, (k, v) in enumerate(app_fields):
        bgc = (245, 247, 250) if i % 2 == 0 else (255, 255, 255)
        draw.rectangle([(60, y), (1215, y + 36)], fill=bgc)
        text(draw, (80, y + 7), f"{k}:", size=19, bold=True)
        text(draw, (430, y + 7), v, size=19)
        y += 36
    y += 25
    text(draw, (60, y), "Employment & Income", size=26, bold=True)
    y += 38
    emp_fields = [
        ("Employer", "Nationwide Children's Hospital"),
        ("Position", "Registered Nurse (RN)"),
        ("Status", "Full-time, permanent since June 2020"),
        ("Monthly Gross Income", "$5,480.00"),
        ("HR Contact", "Teresa Nguyen  (614) 555-8800 x4421"),
    ]
    for i, (k, v) in enumerate(emp_fields):
        bgc = (245, 247, 250) if i % 2 == 0 else (255, 255, 255)
        draw.rectangle([(60, y), (1215, y + 36)], fill=bgc)
        text(draw, (80, y + 7), f"{k}:", size=19, bold=True)
        text(draw, (430, y + 7), v, size=19)
        y += 36
    y += 25
    hline(draw, y)
    y += 28
    text(draw, (60, y), "I authorize a credit + background check.  All information is accurate.", size=19)
    y += 48
    text(draw, (60, y), "Applicant Signature: ___________________________   Date: July 15, 2025", size=20)
    y += 42
    text(draw, (60, y), "Application fee: $50 non-refundable   ·   Processing: 2–3 business days",
         size=17, color=(100, 100, 100))


# ===========================================================================
# DOCUMENT 7 – Work Order / Maintenance Request — WorkOrder draft
# ===========================================================================

def make_work_order_pdf():
    story = [
        rl_h1("MAINTENANCE WORK ORDER"),
        rl_body("Greenleaf Residential LLC  ·  (614) 555-0182  ·  maintenance@greenleafresidential.com"),
        hr(),
        spacer(0.1),
        Table(
            [
                ["Work Order No.:", "WO-2025-0391"],
                ["Date Submitted:", "December 2, 2025"],
                ["Priority:", "HIGH — water damage risk"],
                ["Status:", "Open — Awaiting Scheduling"],
                ["Property / Unit:", "Westview Four-Plex — Unit 1, 4812 Westview Drive, Columbus OH 43235"],
                ["Tenant:", "Denise Okafor"],
                ["Tenant Phone:", "(614) 555-2299"],
            ],
            colWidths=[2.0 * inch, 5.0 * inch],
            style=TableStyle([
                ("FONTNAME", (0, 0), (0, -1), "Helvetica-Bold"),
                ("FONTSIZE", (0, 0), (-1, -1), 10),
                ("TOPPADDING", (0, 0), (-1, -1), 6),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 6),
                ("ROWBACKGROUNDS", (0, 0), (-1, -1), [colors.HexColor("#fff5f5"), colors.white]),
                ("LEFTPADDING", (0, 0), (-1, -1), 8),
            ]),
        ),
        spacer(0.15),
        rl_h2("Problem Description"),
        rl_body(
            "Tenant reports a slow leak under the kitchen sink. Water has been dripping from "
            "the P-trap connection for approximately 3 days. There is visible water staining on "
            "the cabinet floor. No mold observed yet. Tenant has placed a bucket under the "
            "drain as a temporary measure."
        ),
        spacer(0.15),
        rl_h2("Scope of Work"),
        rl_body("1. Inspect P-trap and supply lines under kitchen sink."),
        rl_body("2. Replace P-trap assembly and compression fittings as needed."),
        rl_body("3. Check supply shut-off valves — replace if corroded."),
        rl_body("4. Inspect cabinet floor for water damage; treat if early-stage."),
        rl_body("5. Document with photos before and after repair."),
        spacer(0.15),
        rl_h2("Assigned Vendor"),
        rl_body("<b>Vendor:</b>   Apex Plumbing & Mechanical LLC"),
        rl_body("<b>Contact:</b>  Daniel K. Osei  ·  (614) 555-0317"),
        rl_body("<b>Scheduled:</b>  December 4, 2025 — 9:00 AM – 12:00 PM"),
        rl_body("<b>Est. Cost:</b>  $180 – $240 (parts + 1.5 hrs labor)"),
        spacer(0.3),
        hr(),
        rl_body("<b>Manager Approval:</b>  ___________________________   Date: ___________"),
        spacer(0.1),
        rl_body("<b>Technician Sign-off:</b>  ___________________________   Completed: ___________"),
    ]
    return build_pdf("work_order_westview_unit1_sink_leak.pdf", story)


def draw_work_order_image(draw, W, H):
    y = 45
    text(draw, (W // 2, y), "MAINTENANCE WORK ORDER", size=34, bold=True, anchor="mt")
    y += 50
    text(draw, (W // 2, y),
         "Greenleaf Residential LLC  ·  (614) 555-0182  ·  maintenance@greenleafresidential.com",
         size=19, anchor="mt", color=(80, 80, 80))
    y += 42
    hline(draw, y)
    y += 28
    meta = [
        ("Work Order No.", "WO-2025-0391"),
        ("Date Submitted", "December 2, 2025"),
        ("Priority", "HIGH — water damage risk"),
        ("Status", "Open — Awaiting Scheduling"),
        ("Property / Unit", "Westview Four-Plex — Unit 1, 4812 Westview Drive, Columbus OH 43235"),
        ("Tenant", "Denise Okafor   ·   (614) 555-2299"),
    ]
    for i, (k, v) in enumerate(meta):
        bgc = (255, 245, 245) if i < 3 else ((245, 247, 250) if i % 2 == 0 else (255, 255, 255))
        draw.rectangle([(60, y), (1215, y + 36)], fill=bgc)
        text(draw, (80, y + 7), f"{k}:", size=19, bold=True)
        text(draw, (380, y + 7), v, size=19)
        y += 36
    y += 25
    text(draw, (60, y), "Problem Description", size=26, bold=True)
    y += 40
    desc = ("Tenant reports a slow leak under the kitchen sink. P-trap connection dripping "
            "~3 days. Visible water staining on cabinet floor. Tenant has bucket in place.")
    text(draw, (60, y), desc, size=19)
    y += 50
    text(draw, (60, y), "Scope of Work", size=26, bold=True)
    y += 38
    scope = [
        "1. Inspect P-trap and supply lines under kitchen sink.",
        "2. Replace P-trap assembly and compression fittings as needed.",
        "3. Check supply shut-off valves — replace if corroded.",
        "4. Inspect cabinet floor for water damage; treat if early-stage.",
        "5. Document with photos before and after repair.",
    ]
    for s in scope:
        text(draw, (80, y), s, size=19)
        y += 34
    y += 20
    text(draw, (60, y), "Assigned Vendor", size=26, bold=True)
    y += 38
    vendor_lines = [
        ("Vendor", "Apex Plumbing & Mechanical LLC"),
        ("Contact", "Daniel K. Osei  ·  (614) 555-0317"),
        ("Scheduled", "December 4, 2025 — 9:00 AM – 12:00 PM"),
        ("Est. Cost", "$180 – $240  (parts + 1.5 hrs labor)"),
    ]
    for k, v in vendor_lines:
        text(draw, (80, y), f"{k}:", size=19, bold=True)
        text(draw, (310, y), v, size=19)
        y += 36
    y += 30
    hline(draw, y)
    y += 28
    text(draw, (60, y), "Manager Approval: ___________________________   Date: ____________", size=20)
    y += 42
    text(draw, (60, y), "Technician Sign-off: ________________________   Completed: ________", size=20)


# ===========================================================================
# DOCUMENT 8 – Repair Receipt (Apex Plumbing) — Expense (photographed only)
# ===========================================================================

def draw_repair_receipt_image(draw, W, H):
    """Handwritten-style repair receipt from Apex Plumbing — rougher look."""
    y = 48
    text(draw, (W // 2, y), "APEX PLUMBING & MECHANICAL LLC", size=30, bold=True, anchor="mt")
    y += 42
    text(draw, (W // 2, y), "1822 Neil Ave, Columbus OH 43201  ·  (614) 555-0317",
         size=20, anchor="mt", color=(60, 60, 60))
    y += 40
    hline(draw, y)
    y += 28
    text(draw, (60, y), "RECEIPT / PAID INVOICE", size=26, bold=True)
    y += 42
    pairs = [
        ("Receipt No.", "APX-RCP-8821"),
        ("Date", "December 4, 2025"),
        ("Customer", "Greenleaf Residential LLC"),
        ("Property", "4812 Westview Dr Unit 1, Columbus OH 43235"),
        ("Tech", "M. Guerrero"),
    ]
    for k, v in pairs:
        text(draw, (60, y), f"{k}:", size=21, bold=True)
        text(draw, (340, y), v, size=21)
        y += 40
    y += 15
    hline(draw, y)
    y += 25
    draw.rectangle([(60, y), (1215, y + 40)], fill=(200, 220, 200))
    text(draw, (80, y + 8), "Description", size=20, bold=True)
    text(draw, (1205, y + 8), "Amount", size=20, bold=True, anchor="ra")
    y += 40
    items = [
        ("P-trap replacement kit (PVC 1-1/2\")", "$28.50"),
        ("Supply line replacement (pair)", "$19.00"),
        ("Labor — 1.5 hrs @ $95/hr", "$142.50"),
    ]
    for i, (d, a) in enumerate(items):
        bgc = (240, 248, 240) if i % 2 == 0 else (255, 255, 255)
        draw.rectangle([(60, y), (1215, y + 36)], fill=bgc)
        text(draw, (80, y + 7), d, size=19)
        text(draw, (1205, y + 7), a, size=19, anchor="ra")
        y += 36
    hline(draw, y, width=2)
    y += 12
    text(draw, (900, y), "TOTAL PAID", size=22, bold=True)
    text(draw, (1205, y), "$190.00", size=22, bold=True, anchor="ra")
    y += 55
    text(draw, (60, y), "Payment: Check #9843 — PAID IN FULL", size=20, bold=True)
    y += 45
    text(draw, (60, y), "Thank you for your business!  ·  Lic# PLB-OH-07714",
         size=17, color=(100, 100, 100))
    y += 42
    text(draw, (60, y), "Technician signature: ___________________________", size=20)


# ===========================================================================
# MAIN
# ===========================================================================

def main():
    random.seed(42)
    created = []

    print("Generating sample scan documents …")

    # --- LEASE ---
    p = make_lease_pdf()
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    img = render_page_to_image(draw_lease_image)
    img = add_photo_effects(img, skew_deg=1.8, noise=14)
    p = save_jpg(img, "lease_westview_unit2_marcus_williams_photo.jpg", quality=78)
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    # --- RENT RECEIPT ---
    p = make_rent_receipt_pdf()
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    img = render_page_to_image(draw_rent_receipt_image)
    img = add_photo_effects(img, skew_deg=3.2, noise=22)
    p = save_jpg(img, "rent_receipt_derek_johnson_sep2025_photo.jpg", quality=75)
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    # --- HVAC INVOICE ---
    p = make_hvac_invoice_pdf()
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    img = render_page_to_image(draw_hvac_invoice_image)
    img = add_photo_effects(img, skew_deg=2.1, noise=20)
    p = save_jpg(img, "vendor_invoice_comfortzone_hvac_oct2025_photo.jpg", quality=80)
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    # --- UTILITY BILL ---
    p = make_utility_bill_pdf()
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    img = render_page_to_image(draw_utility_bill_image)
    img = add_photo_effects(img, skew_deg=1.5, noise=16)
    p = save_jpg(img, "utility_bill_aep_ohio_nov2025_photo.jpg", quality=82)
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    # --- W-9 ---
    p = make_w9_pdf()
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    img = render_page_to_image(draw_w9_image)
    img = add_photo_effects(img, skew_deg=0.9, noise=10)  # W-9 = cleaner scan
    p = save_png(img, "w9_apex_plumbing_photo.png")
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    # --- RENTAL APPLICATION ---
    p = make_rental_application_pdf()
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    img = render_page_to_image(draw_rental_application_image)
    img = add_photo_effects(img, skew_deg=2.8, noise=19)
    p = save_jpg(img, "rental_application_jasmine_carter_photo.jpg", quality=77)
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    # --- WORK ORDER ---
    p = make_work_order_pdf()
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    img = render_page_to_image(draw_work_order_image)
    img = add_photo_effects(img, skew_deg=3.5, noise=25)  # most skewed — field photo
    p = save_jpg(img, "work_order_westview_unit1_sink_leak_photo.jpg", quality=73)
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    # --- REPAIR RECEIPT (image only — rougher / low-confidence) ---
    img = render_page_to_image(draw_repair_receipt_image)
    img = add_photo_effects(img, skew_deg=4.0, noise=28)
    p = save_jpg(img, "repair_receipt_apex_plumbing_dec2025_photo.jpg", quality=70)
    created.append(p)
    print(f"  + {os.path.basename(p)}")

    print(f"\nDone. {len(created)} files in {OUT_DIR}/")
    return created


if __name__ == "__main__":
    try:
        import numpy  # noqa: F401
    except ImportError:
        print("numpy not found — installing …")
        import subprocess
        subprocess.check_call([sys.executable, "-m", "pip", "install", "numpy"])

    main()
