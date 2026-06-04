# Sample Scan Documents

Test documents for the **Scan → Draft → Confirm** flow in Rental Command.
Drop any file into the Scan page to see the LLM extractor produce a draft record.

Regenerate all files:
```bash
cd samples/scans
.venv/bin/python generate.py
# or from repo root (with venv activated or reportlab+pillow+numpy installed):
# python3 samples/scans/generate.py
```

---

## Files

| File | Doc Type | Expected Draft Record | Notes |
|------|----------|----------------------|-------|
| `lease_westview_unit2_marcus_williams.pdf` | Residential Lease Agreement | **Lease** | Clean PDF. Tenant: Marcus Williams. Rent $1,250/mo. Aug 2025–Jul 2026. |
| `lease_westview_unit2_marcus_williams_photo.jpg` | Lease (photographed) | **Lease** | Slight skew + grain. Tests low-confidence field detection on a photo. |
| `rent_receipt_derek_johnson_sep2025.pdf` | Rent Receipt | **Payment** | Clean PDF. Derek Johnson, Unit A Clintonville Townhome, $1,150, Sept 2025. |
| `rent_receipt_derek_johnson_sep2025_photo.jpg` | Rent Receipt (photographed) | **Payment** | More skew + noise. Good for testing amount/date extraction from an image. |
| `vendor_invoice_comfortzone_hvac_oct2025.pdf` | Vendor Invoice | **Expense** | Clean PDF. ComfortZone HVAC, HVAC repair Westview Unit 3, $660.00, Oct 2025. |
| `vendor_invoice_comfortzone_hvac_oct2025_photo.jpg` | Vendor Invoice (photographed) | **Expense** | Mid-grade noise. Line-item table test. |
| `utility_bill_aep_ohio_nov2025.pdf` | Utility Bill | **Expense** | Clean PDF. AEP Ohio common-area electric, $113.64, Nov 2025. |
| `utility_bill_aep_ohio_nov2025_photo.jpg` | Utility Bill (photographed) | **Expense** | Light noise. Tests extraction of account number + amount from a photo. |
| `w9_apex_plumbing.pdf` | IRS Form W-9 | **Vendor (tax ID)** | Clean PDF. Apex Plumbing & Mechanical LLC, EIN 47-3920154. |
| `w9_apex_plumbing_photo.png` | W-9 (photographed) | **Vendor (tax ID)** | Minimal skew — simulates a reasonably clean desk scan of an official form. |
| `rental_application_jasmine_carter.pdf` | Rental Application | **Application** | Clean PDF. Jasmine L. Carter, income $5,480/mo, current address Columbus OH. |
| `rental_application_jasmine_carter_photo.jpg` | Rental Application (photographed) | **Application** | Medium noise + skew. Good for testing applicant-field extraction. |
| `work_order_westview_unit1_sink_leak.pdf` | Maintenance Work Order | **WorkOrder** | Clean PDF. Westview Unit 1 kitchen sink leak, priority HIGH, assigned Apex Plumbing. |
| `work_order_westview_unit1_sink_leak_photo.jpg` | Work Order (photographed) | **WorkOrder** | Heavy skew + high noise — simulates a field photo taken in dim conditions. |
| `repair_receipt_apex_plumbing_dec2025_photo.jpg` | Repair Receipt (image only) | **Expense** | No PDF companion. Most degraded file — tests low-confidence extraction UI. Total: $190.00. |

---

## Demo data alignment

All names, addresses, and vendors match the seeded demo data:

- **Tenants**: Marcus Williams, Derek Johnson, Denise Okafor, Jasmine L. Carter (applicant)
- **Properties**: Westview Four-Plex (4812 Westview Drive), Clintonville Townhome (221 Unit A)
- **Vendors**: ComfortZone HVAC (HVAC repair), Apex Plumbing & Mechanical LLC (plumbing)
- **Landlord entity**: Greenleaf Residential LLC

---

## Toolchain

- **reportlab 4.x** — PDF generation
- **Pillow 12.x** — image rendering and photo-effect pipeline (skew, noise, contrast reduction)
- **numpy** — pixel-level noise injection

All installed in `.venv/` inside this folder (Python 3.13).
