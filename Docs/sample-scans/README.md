# Sample documents for testing the scanner

A set of realistic documents to test Rental Command's **scan → draft → confirm** flow. Each one
comes in **two formats**:

- **`.pdf`** — born-digital (has a real text layer). The scanner reads it by extracting the text.
  Good for "import a PDF" or "print + scan."
- **`.png`** — an image of the same document. The scanner reads it by **vision**. Good for
  "take a photo" (photograph it on screen, or print it) or importing the image directly.

The vendors, tenants, and properties match the demo seed data, so when you confirm a document the
resulting record lines up sensibly.

## What's in the set

| File | Type | Scan it as | Becomes |
|---|---|---|---|
| `01-receipt-apex-plumbing` | Paid plumbing receipt | Receipt / Bill | **Expense** (paid) — $346.69 |
| `02-receipt-handy-pro` | Paid handyman receipt | Receipt / Bill | **Expense** (paid) |
| `03-receipt-hardware-store` | Paid hardware-store receipt | Receipt / Bill | **Expense** (paid, supplies) |
| `04-invoice-green-thumb-landscaping` | Landscaping invoice (due) | Receipt / Bill | **Expense** (unpaid bill) |
| `05-invoice-comfortzone-hvac` | HVAC service invoice (due) | Receipt / Bill | **Expense** (unpaid bill) |
| `06-invoice-summit-roofing` | Roofing invoice (large, due) | Receipt / Bill | **Expense** (unpaid bill) |
| `07-utility-electric` | Electric statement (due) | Receipt / Bill | **Expense** (utilities) |
| `08-utility-water-sewer` | Water/sewer bill (due) | Receipt / Bill | **Expense** (utilities) |
| `09-property-tax-statement` | County property-tax statement | Receipt / Bill | **Expense** (taxes) |
| `10-insurance-premium` | Dwelling-policy premium notice | Receipt / Bill | **Expense** (insurance) |
| `11-rent-check-marcus-williams` | Rent check — Marcus Williams, $1,500 | Rent Check / Payment | **Payment** (rent) |
| `12-rent-check-priya-patel` | Rent check — Priya Patel, $1,375 | Rent Check / Payment | **Payment** (rent) |
| `13-lease-sunset-ridge.txt` | Residential lease (Property + Unit + Tenant + Lease) — text source, render to PNG/PDF later via `generate.py` | Lease | Front-door "New rental from your lease" flow — Property «Sunset Ridge Apartments, 482 Sunset Ridge Drive», Unit «12B», Tenant «Daniel R. Fletcher», Lease «$1,575/mo, Jul 1 2026 – Jun 30 2027» |

## How to use them

- **On the phone:** open the PNGs on a screen and **take a photo** through the Scan flow, or copy
  the images to the phone and import them.
- **On the website:** drag a PDF (or PNG) onto the upload zone, or click to browse.
- Pick the right document type first — **Receipt / Bill** for items 1–10, **Rent Check / Payment**
  for items 11–12. (The app also auto-detects, but choosing helps.)

## Regenerating / adding more

```bash
python3 -m venv /tmp/scanvenv
/tmp/scanvenv/bin/pip install reportlab pymupdf
/tmp/scanvenv/bin/python Docs/sample-scans/generate.py
```

Edit the `DOCS` list at the bottom of `generate.py` to add or change documents.
