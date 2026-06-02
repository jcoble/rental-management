#!/usr/bin/env python3
"""
Generate realistic SAMPLE DOCUMENTS for testing Rental Command's scan->draft->confirm flow.

Produces, for each document, BOTH:
  - a born-digital PDF  (the scanner reads its text layer)
  - a PNG image          (the scanner reads it via vision -- photograph it or import it)

Documents use the demo vendors/tenants so a confirmed record lines up with seeded data.

Run:  /tmp/scanvenv/bin/python Docs/sample-scans/generate.py
Deps: reportlab, pymupdf  (pip install reportlab pymupdf)
"""
import os
import fitz  # PyMuPDF
from reportlab.lib.pagesizes import letter
from reportlab.lib.units import inch
from reportlab.lib.colors import HexColor
from reportlab.pdfgen import canvas

OUT = os.path.dirname(os.path.abspath(__file__))
W, H = letter
INK = HexColor("#1a1a1a")
MUTED = HexColor("#666666")
LINE = HexColor("#cccccc")
ACCENT = HexColor("#2a4d7a")

def money(x):
    return f"${x:,.2f}"

def page_frame(c):
    c.setFillColor(HexColor("#fbfbf9"))
    c.rect(0, 0, W, H, fill=1, stroke=0)
    c.setStrokeColor(LINE)
    c.setLineWidth(1)
    c.rect(0.45 * inch, 0.45 * inch, W - 0.9 * inch, H - 0.9 * inch, fill=0, stroke=1)

def text(c, x, y, s, size=10, color=INK, font="Helvetica", right=False, center=False):
    c.setFillColor(color)
    c.setFont(font, size)
    if right:
        c.drawRightString(x, y, s)
    elif center:
        c.drawCentredString(x, y, s)
    else:
        c.drawString(x, y, s)

def business_doc(path, *, kind_label, biz, addr, phone, website, doc_no, date,
                 items, tax_rate, paid=None, due_date=None, bill_to=None, note=None):
    """Receipt / Invoice / Utility / Tax / Insurance -- shared business-document layout."""
    c = canvas.Canvas(path, pagesize=letter)
    page_frame(c)
    x0 = 0.75 * inch
    xr = W - 0.75 * inch
    y = H - 1.0 * inch

    text(c, x0, y, biz, size=20, font="Helvetica-Bold", color=ACCENT)
    text(c, xr, y, kind_label, size=15, font="Helvetica-Bold", color=MUTED, right=True)
    y -= 16
    for ln in addr:
        text(c, x0, y, ln, size=9, color=MUTED); y -= 12
    text(c, x0, y, phone, size=9, color=MUTED); y -= 12
    if website:
        text(c, x0, y, website, size=9, color=MUTED); y -= 12

    my = H - 1.0 * inch - 30
    text(c, xr, my, f"No. {doc_no}", size=10, right=True); my -= 14
    text(c, xr, my, f"Date: {date}", size=10, right=True); my -= 14
    if due_date:
        text(c, xr, my, f"Due: {due_date}", size=10, right=True, color=HexColor("#a23")); my -= 14

    y = min(y, my) - 18
    if bill_to:
        text(c, x0, y, "Bill To:", size=9, font="Helvetica-Bold", color=MUTED); y -= 13
        for ln in bill_to:
            text(c, x0, y, ln, size=10); y -= 13
        y -= 8

    c.setStrokeColor(LINE); c.setLineWidth(1)
    c.line(x0, y, xr, y); y -= 16
    text(c, x0, y, "Description", size=9, font="Helvetica-Bold", color=MUTED)
    text(c, xr - 2.4 * inch, y, "Qty", size=9, font="Helvetica-Bold", color=MUTED, right=True)
    text(c, xr - 1.3 * inch, y, "Unit", size=9, font="Helvetica-Bold", color=MUTED, right=True)
    text(c, xr, y, "Amount", size=9, font="Helvetica-Bold", color=MUTED, right=True)
    y -= 6
    c.line(x0, y, xr, y); y -= 16

    subtotal = 0.0
    for desc, qty, unit in items:
        amt = qty * unit
        subtotal += amt
        text(c, x0, y, desc, size=10)
        text(c, xr - 2.4 * inch, y, str(qty), size=10, right=True)
        text(c, xr - 1.3 * inch, y, money(unit), size=10, right=True)
        text(c, xr, y, money(amt), size=10, right=True)
        y -= 15

    y -= 4
    c.line(xr - 2.4 * inch, y, xr, y); y -= 16
    tax = round(subtotal * tax_rate, 2)
    total = round(subtotal + tax, 2)
    text(c, xr - 1.5 * inch, y, "Subtotal", size=10, color=MUTED, right=True)
    text(c, xr, y, money(subtotal), size=10, right=True); y -= 15
    text(c, xr - 1.5 * inch, y, f"Tax ({tax_rate*100:.2f}%)", size=10, color=MUTED, right=True)
    text(c, xr, y, money(tax), size=10, right=True); y -= 17
    text(c, xr - 1.5 * inch, y, "TOTAL", size=12, font="Helvetica-Bold", right=True)
    text(c, xr, y, money(total), size=12, font="Helvetica-Bold", right=True); y -= 26

    if paid:
        c.setFillColor(HexColor("#e7f3e7"))
        c.roundRect(x0, y - 6, 2.6 * inch, 22, 4, fill=1, stroke=0)
        text(c, x0 + 8, y, f"PAID -- {paid}", size=10, font="Helvetica-Bold", color=HexColor("#2a7a2a"))
        y -= 30
    elif due_date:
        text(c, x0, y, f"Amount Due: {money(total)}  --  due {due_date}", size=11,
             font="Helvetica-Bold", color=HexColor("#a23")); y -= 22

    if note:
        text(c, x0, y, note, size=8, color=MUTED); y -= 12

    text(c, W / 2, 0.7 * inch, "Thank you for your business", size=8, color=MUTED, center=True)
    c.showPage(); c.save()


def check_doc(path, *, payer, payer_addr, bank, check_no, date, pay_to, amount,
              amount_words, memo, routing, account):
    """A personal check (-> Payment)."""
    c = canvas.Canvas(path, pagesize=letter)
    c.setFillColor(HexColor("#eef2f7"))
    c.rect(0, 0, W, H, fill=1, stroke=0)
    cw, ch = 6.6 * inch, 2.9 * inch
    cx, cy = (W - cw) / 2, (H - ch) / 2 + 1.2 * inch
    c.setFillColor(HexColor("#f7faff"))
    c.setStrokeColor(HexColor("#9bb0cc")); c.setLineWidth(1.2)
    c.roundRect(cx, cy, cw, ch, 6, fill=1, stroke=1)
    pad = 16
    text(c, cx + pad, cy + ch - 22, payer, size=12, font="Helvetica-Bold")
    text(c, cx + pad, cy + ch - 36, payer_addr[0], size=8, color=MUTED)
    text(c, cx + pad, cy + ch - 47, payer_addr[1], size=8, color=MUTED)
    text(c, cx + cw - pad, cy + ch - 22, f"No. {check_no}", size=11, right=True)
    text(c, cx + cw - pad, cy + ch - 42, f"Date  {date}", size=10, right=True)
    y = cy + ch - 80
    text(c, cx + pad, y, "PAY TO THE", size=7, color=MUTED)
    text(c, cx + pad, y - 9, "ORDER OF", size=7, color=MUTED)
    text(c, cx + pad + 64, y - 4, pay_to, size=12, font="Helvetica-Bold")
    c.setStrokeColor(HexColor("#aaaaaa")); c.line(cx + pad + 60, y - 8, cx + cw - 1.5 * inch, y - 8)
    c.setStrokeColor(HexColor("#555")); c.setLineWidth(1.2)
    c.rect(cx + cw - 1.4 * inch, y - 16, 1.2 * inch, 24, fill=0, stroke=1)
    text(c, cx + cw - 1.32 * inch, y - 8, "$", size=11)
    text(c, cx + cw - 0.22 * inch, y - 8, f"{amount:,.2f}", size=12, font="Helvetica-Bold", right=True)
    yw = y - 34
    text(c, cx + pad, yw, amount_words + "  Dollars", size=10, font="Helvetica-Oblique")
    c.setStrokeColor(HexColor("#aaaaaa")); c.setLineWidth(0.8)
    c.line(cx + pad, yw - 4, cx + cw - pad, yw - 4)
    text(c, cx + pad, cy + 40, bank, size=10, font="Helvetica-Bold", color=ACCENT)
    text(c, cx + pad, cy + 20, f"MEMO  {memo}", size=9, color=MUTED)
    c.line(cx + cw - 2.3 * inch, cy + 26, cx + cw - pad, cy + 26)
    text(c, cx + cw - 1.2 * inch, cy + 16, "Authorized Signature", size=7, color=MUTED, center=True)
    c.setFont("Courier", 12); c.setFillColor(INK)
    c.drawString(cx + pad, cy + 6, f"|:{routing}:|  {account}||  {check_no}")
    c.showPage(); c.save()


DOCS = []
def receipt(n, slug, **kw): DOCS.append((f"{n:02d}-{slug}", "biz", kw))
def check(n, slug, **kw): DOCS.append((f"{n:02d}-{slug}", "check", kw))

receipt(1, "receipt-apex-plumbing", kind_label="RECEIPT", biz="Apex Plumbing Co.",
    addr=["1420 Industrial Pkwy", "Columbus, OH 43215"], phone="(614) 555-0200",
    website="apexplumbing.example", doc_no="RC-48821", date="04/12/2026",
    items=[("Emergency drain clearing - Unit 4B", 1, 185.00), ("Replace P-trap & gasket", 1, 42.50),
           ("Service call (after hours)", 1, 95.00)], tax_rate=0.075, paid="Visa ****4417")
receipt(2, "receipt-handy-pro", kind_label="RECEIPT", biz="Handy Pro Services",
    addr=["88 Maple Ave", "Columbus, OH 43201"], phone="(614) 555-0204", website="",
    doc_no="2026-0337", date="03/28/2026",
    items=[("Patch & paint hallway drywall", 3, 55.00), ("Replace 2 interior door knobs", 2, 28.00),
           ("Caulk bathroom tub", 1, 35.00)], tax_rate=0.075, paid="Cash")
receipt(3, "receipt-hardware-store", kind_label="SALES RECEIPT", biz="Buckeye Hardware & Supply",
    addr=["2200 N High St", "Columbus, OH 43202"], phone="(614) 555-0918",
    website="buckeyehardware.example", doc_no="0042-117833", date="04/02/2026",
    items=[("LED shop light 4ft", 2, 24.99), ("Furnace filter 16x25 (3pk)", 1, 31.49),
           ("Smoke detector 9V", 4, 12.75), ("Drywall screws 1lb", 1, 8.49)],
    tax_rate=0.075, paid="Mastercard ****2093")
receipt(4, "invoice-green-thumb-landscaping", kind_label="INVOICE", biz="Green Thumb Landscaping",
    addr=["540 Greenfield Rd", "Columbus, OH 43204"], phone="(614) 555-0203",
    website="greenthumb.example", doc_no="INV-7741", date="04/01/2026", due_date="04/30/2026",
    bill_to=["Rental Command Properties", "Maple Ridge Duplex"],
    items=[("Monthly lawn maintenance (April)", 1, 140.00), ("Spring mulch - 6 yards", 6, 38.00),
           ("Shrub trimming", 1, 75.00)], tax_rate=0.0)
receipt(5, "invoice-comfortzone-hvac", kind_label="INVOICE", biz="ComfortZone HVAC",
    addr=["3105 Cooling Way", "Columbus, OH 43219"], phone="(614) 555-0202",
    website="comfortzonehvac.example", doc_no="CZ-2026-588", date="05/06/2026", due_date="05/21/2026",
    bill_to=["Rental Command Properties", "Oakwood Apartments - Unit 12"],
    items=[("A/C diagnostic & recharge (2 lbs R-410A)", 1, 240.00), ("Replace capacitor", 1, 65.00),
           ("Annual service plan", 1, 120.00)], tax_rate=0.075)
receipt(6, "invoice-summit-roofing", kind_label="INVOICE", biz="Summit Roofing Inc.",
    addr=["77 Ridgeline Dr", "Columbus, OH 43220"], phone="(614) 555-0205",
    website="summitroofing.example", doc_no="SR-9012", date="05/02/2026", due_date="06/01/2026",
    bill_to=["Rental Command Properties", "Maple Ridge Duplex"],
    items=[("Repair storm damage - north slope", 1, 1850.00), ("Replace 2 sq. of shingles", 2, 320.00),
           ("Flashing & sealant", 1, 145.00)], tax_rate=0.0,
    note="Workmanship warranty: 5 years. Materials per manufacturer.")
receipt(7, "utility-electric", kind_label="ELECTRIC STATEMENT", biz="Reliable Power & Light",
    addr=["PO Box 7700", "Columbus, OH 43216"], phone="(800) 555-7000",
    website="reliablepower.example", doc_no="ACCT 88-204-5513", date="04/18/2026", due_date="05/10/2026",
    bill_to=["Maple Ridge Duplex - Unit 4B"],
    items=[("Electricity usage - 612 kWh", 612, 0.118), ("Distribution charge", 1, 18.40),
           ("State regulatory fee", 1, 3.25)], tax_rate=0.0)
receipt(8, "utility-water-sewer", kind_label="WATER / SEWER BILL", biz="City of Columbus Water & Sewer",
    addr=["910 Dublin Rd", "Columbus, OH 43215"], phone="(614) 555-6100",
    website="columbuswater.example", doc_no="ACCT 5523-119", date="04/15/2026", due_date="05/05/2026",
    bill_to=["Oakwood Apartments - Unit 12"],
    items=[("Water - 4,200 gal", 1, 38.90), ("Sewer service", 1, 41.20), ("Stormwater fee", 1, 9.50)],
    tax_rate=0.0)
receipt(9, "property-tax-statement", kind_label="PROPERTY TAX STATEMENT", biz="Franklin County Treasurer",
    addr=["373 S High St, 17th Fl", "Columbus, OH 43215"], phone="(614) 555-0299",
    website="franklincountytreasurer.example", doc_no="PARCEL 010-244781", date="01/20/2026",
    due_date="06/22/2026", bill_to=["Maple Ridge Duplex", "128 Maple Ridge Ct, Columbus, OH 43204"],
    items=[("First-half real estate tax (2026)", 1, 1742.36)], tax_rate=0.0,
    note="Make checks payable to Franklin County Treasurer. Parcel 010-244781.")
receipt(10, "insurance-premium", kind_label="PREMIUM NOTICE", biz="Heartland Mutual Insurance",
    addr=["500 Insurance Plaza", "Dublin, OH 43017"], phone="(800) 555-3400",
    website="heartlandmutual.example", doc_no="POL DP3-771204", date="04/05/2026", due_date="05/01/2026",
    bill_to=["Rental Command Properties", "Maple Ridge Duplex - Dwelling Policy"],
    items=[("Annual dwelling fire premium", 1, 1280.00), ("Liability endorsement", 1, 95.00)],
    tax_rate=0.0, note="Coverage: $250,000 dwelling / $300,000 liability. Policy DP3-771204.")
check(11, "rent-check-marcus-williams", payer="Marcus Williams",
    payer_addr=["128 Maple Ridge Ct, Unit 4B", "Columbus, OH 43204"], bank="Huntington National Bank",
    check_no="1042", date="05/01/2026", pay_to="Rental Command Properties", amount=1500.00,
    amount_words="One thousand five hundred and 00/100", memo="May rent - Unit 4B",
    routing="044000024", account="0023 4419 88")
check(12, "rent-check-priya-patel", payer="Priya Patel",
    payer_addr=["55 Oakwood Ln, Unit 12", "Columbus, OH 43219"], bank="Chase Bank",
    check_no="2087", date="05/03/2026", pay_to="Rental Command Properties", amount=1375.00,
    amount_words="One thousand three hundred seventy-five and 00/100", memo="May rent - Oakwood #12",
    routing="021000021", account="9981 2030 17")


def render_png(pdf_path, png_path, dpi=170):
    doc = fitz.open(pdf_path)
    doc.load_page(0).get_pixmap(dpi=dpi).save(png_path)
    doc.close()


if __name__ == "__main__":
    for name, kind, kw in DOCS:
        pdf = os.path.join(OUT, name + ".pdf")
        (business_doc if kind == "biz" else check_doc)(pdf, **kw)
        render_png(pdf, os.path.join(OUT, name + ".png"))
    print(f"Generated {len(DOCS)} documents (PDF + PNG).")
