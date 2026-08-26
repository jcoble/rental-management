# Lane mob-c receipt

- Started: 2026-08-25T17:10:48-04:00
- Scope: consolidate the mobile tenant-receipt command and models.

## Item 1 — STOP condition met

Compared `PaymentsRepository.recordReceipt` and `TenantLedgerRepository.recordReceipt` against the API request DTO and controller mapping. The API reads every field listed below, including `allocateOldestCharges` (`RentalCommand.Api/DTOs/TenantMoneyDtos.cs:6-18`, `RentalCommand.Api/Controllers/TenantAccountMoneyController.cs:219-228`).

### Request body field diff

| Field | Payments implementation | Tenant-ledger implementation | Difference |
| --- | --- | --- | --- |
| `amount` | Sends `input.amount` | Sends `amount` from `toJson()` | None |
| `effectiveOn` | Formats as explicit zero-padded `yyyy-MM-dd` | Takes the date portion of `toIso8601String()` | Same for the sheet's ordinary `DateTime` values; implementation differs |
| `description` | Trims before sending | Sends the supplied value unchanged | Yes; server reads this field |
| `paymentMethodSummary` | Trims before sending | Sends the supplied value unchanged | Yes; server reads this field |
| `externalReference` | Trims, then omits null/empty/whitespace | Includes every non-null value unchanged, including empty/whitespace | Yes; server reads this field |
| `payerName` | Trims, then omits null/empty/whitespace | Includes every non-null value unchanged, including empty/whitespace | Yes; server reads this field |
| `checkNumber` | Trims, then omits null/empty/whitespace | Includes every non-null value unchanged, including empty/whitespace | Yes; server reads this field |
| `bankName` | Trims, then omits null/empty/whitespace | Includes every non-null value unchanged, including empty/whitespace | Yes; server reads this field |
| `sourceStoredFileId` | Omits when null | Omits when null | None |
| `targetChargeEntryId` | Always includes the key, with null when absent | Omits when null | Yes in JSON shape; both bind to null on the server |
| `allocateOldestCharges` | Omits the key, relying on the server DTO default of `true` | Always includes the key; model default is `true` but callers may set `false` | Yes; server reads this field |

Sources: payments serialization at `mobile/lib/features/payments/payments_repository.dart:642-667`; retained serialization at `mobile/lib/features/money/tenant_ledger_models.dart:317-355` and dispatch at `mobile/lib/features/money/tenant_ledger_repository.dart:183-193`.

### Result parser field diff

| Field/behavior | Payments parser | Tenant-ledger parser | Difference |
| --- | --- | --- | --- |
| Envelope | Requires fields beneath `json['value']` | Accepts `json['value']` or an unwrapped object | Tenant-ledger parser is more tolerant |
| `found` | Not represented | Parses `found`, defaulting to `false` | Yes |
| `tenantAccountId` | Required numeric value | Numeric value defaults to `0` | Missing/malformed response behavior differs |
| `ledgerEntryId` | Required numeric value | Numeric value defaults to `0` | Missing/malformed response behavior differs |
| `paymentAttemptId` | Required numeric value | Numeric value defaults to `0` | Missing/malformed response behavior differs |
| `amount` | Required numeric value | Numeric value defaults to `0` | Missing/malformed response behavior differs |
| `allocatedAmount` | Required numeric value | Numeric value defaults to `0` | Missing/malformed response behavior differs |
| `allocationCount` | Required numeric value | Numeric value defaults to `0` | Missing/malformed response behavior differs |
| `replayed` | Reads root value, defaults to `false` | Reads root value, defaults to `false` | None |

Sources: payments result at `mobile/lib/features/payments/payments_repository.dart:413-443`; retained result at `mobile/lib/features/money/tenant_ledger_models.dart:544-577` and envelope helper at `mobile/lib/features/money/tenant_ledger_models.dart:664-667`.

Decision: STOP before items 2–3, as required by item 1, because payloads differ in fields the server reads. No production or test source was changed.

- Item 1 evidence commit: `c05ac251`.

## Verification

- `flutter pub get` — exit 0.
- `flutter analyze` — exit 1 with 23 pre-existing diagnostics in files outside this lane's named files. The final diagnostics were an unused `_toast` in `tenant_detail_screen.dart` and an unnecessary import in `app_theme_test.dart`; the analyzer ended with `23 issues found. (ran in 11.5s)`.
- `flutter test` — not run because the required `flutter analyze` command failed and the lane instructions require stopping on an unfixable failure unrelated to this change.
- Receipt-class `rg` check — not run after the required analyze failure.
- Receipt-call-site `rg` check — not run after the required analyze failure.

No tests changed.
