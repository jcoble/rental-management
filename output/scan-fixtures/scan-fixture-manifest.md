# Scan Fixture Pack Manifest

All files are synthetic, fake, and intended for non-production testing of the scan/upload pipeline.

## Files

| File | Intended target record | Expected extracted targets | Primary test purpose |
| --- | --- | --- | --- |
| `scan-plumbing-receipt-paid.pdf` | Expense | `document_kind=Receipt`, `vendor_name`, `receipt_number`, `transaction_date`, `subtotal`, `tax`, `total`, `payment_method`, `line_items`, `category=CleaningMaintenance` | Paid receipt ingestion, totals/tax parsing, and paid-vs-unpaid classification fallback. |
| `scan-utility-bill-unpaid.pdf` | Expense | `document_kind=UtilityBill`, `vendor_name`, `transaction_date`, `due_date`, `subtotal`, `tax`, `total`, `receipt_number`, `category=Utilities` | Unpaid utility bill extraction including due-date flow. |
| `scan-property-tax-bill.pdf` | Expense | `document_kind=PropertyTax`, `vendor_name`, `transaction_date`, `due_date`, `total`, `category=Taxes` | Property tax bill parsing and tax-category routing. |
| `scan-mortgage-statement.pdf` | Expense/Other | `vendor_name`, `transaction_date`, `total`, `notes`, `document_kind=Other` (if mapped) | Mortgage statement shape that should be treated as a non-standard scan document. |
| `scan-rent-check-image.png` | Payment artifact | `payee`, `check_number`, `transaction_date`, `amount`, `memo` | Hand-written/printed check-style image OCR path. |
| `scan-deposit-slip-image.png` | Payment artifact | `payee`, `transaction_date`, `amount`, `slip_number` | Bank deposit slip parsing and cash line-item text extraction. |
| `scan-vendor-w9-like.pdf` | Vendor/KYC source | `vendor_name`, `vendor_address`, `vendor_tax_id`, `document_kind=Other` | W-9-like tax identifier extraction for vendor capture workflows. |
| `scan-lease-excerpt.pdf` | Lease source | `lease_name`, `property_address`, `tenant_name`, `rent_amount`, `security_deposit`, `lease_term_start`, `lease_term_end` | Lease text extraction and legal-section capture from dense paragraph blocks. |
| `scan-insurance-certificate.pdf` | Expense/Insurance | `vendor_name`, `policy_number`, `effective_date`, `expiration_date`, `coverage`, `document_kind=Other` | Insurance certificate OCR extraction and policy metadata parsing. |
| `scan-maintenance-invoice.pdf` | Expense | `document_kind=Invoice`, `vendor_name`, `receipt_number`, `transaction_date`, `due_date`, `subtotal`, `tax`, `total`, `line_items`, `category=Repairs` | Unpaid invoice handling and line-item extraction in maintenance context. |
| `scan-edgecase-struck-total.png` | Expense | `document_kind=UtilityBill`, `vendor_name`, `transaction_date`, `due_date`, `line-items` and **final** `total` despite struck text | Edge-case OCR test for crossed-out previous totals and correction lines. |

## Notes

- Keep these as starter fixtures only. They use clearly synthetic tax IDs/names:
  - EIN-like values: fake pattern values (e.g. `12-3456789`).
  - Bank/account-like references are mock values.
- Suggested ingestion targetEntityType during manual testing: `Expense` where applicable, otherwise use a document-type-specific validation lane.
