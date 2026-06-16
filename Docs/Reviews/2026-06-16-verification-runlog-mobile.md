# Mobile Lane Run Log — 2026-06-16 Full-System Verification

**Device:** emulator-5554 · **App:** com.rentalcommand.rental_command (debug build) · **API:** https://10.0.2.2:5666
**flutter run pid:** 83122 (attached, left alive) · Dart VM Service was available.

## Identifiers (used / observed)
- Verify portfolio id (PID): **6** ("Verify Landlord's Portfolio", IsSandbox=f)
- Tenant A user: AspNetUsers **Id=30** (tenant-alice@rc.local, EmailConfirmed=t, role Tenant) — pre-created; login confirmed working on mobile.
- Tenant A entity: Tenants **Id=52** (Alice)
- Tenant A lease: **32** (VERIFY-A-2026-001, MonthlyRent 950, Active, Unit 33, Property 41 Maple Court)
- Conversation: **5** ("Welcome")
- Properties: Maple Court (41), Birch Plaza (42)
- Baseline max ids before run: Expense 687, RentalApplication 12, WorkOrder 41, ScanDraft 141, conv5 msgs 1.

## Tenant-login creation method
Already created out-of-band as AspNetUsers Id=30 (role Tenant, linked to Tenant entity 52, EmailConfirmed=true). This run only CONFIRMED login works on mobile — it does (lands on the tenant shell). No new account created.

## PASS/FAIL/BLOCKED — M1–M13

| Step | Result | Notes |
|---|---|---|
| M1 Landlord login | PASS | "Good afternoon, Verify!"; tabs Today/Money/Work/Messages + Capture FAB. |
| M2 Capture FAB sheet | PASS | Tiles: Scan, Gallery, PDF/file, Tell me, Type it, Scan a lease, Scan an application. |
| M3 Scan receipt→expense | **FAIL** | Scan extraction FAILS (ScanDraft 142 & 143 both Status=Failed). Root cause: API/Engine uploads-dir cwd mismatch (see FINDING-1). App offers manual-entry fallback (works), but the scan→expense extraction itself is broken. |
| M4 Browse menu + parity | PASS | Browse groups RENTALS/WORK/MONEY/Admin present. Properties list shows Maple Court + Birch Plaza (cross-surface parity). |
| M5 Mobile dashboard KPIs | PASS | Collected $950 / Spent $185 / Kept $765; open WO=0; past-due $2,000 — all match DB-side queries for PID 6. |
| M6 Scan application | **FAIL** | ScanDraft 144 (Application) Failed — same uploads-dir bug (FINDING-1). Also FINDING-2: failed-extraction fallback form shows Expense fields (Vendor/Total/Tax…) even though target=Application / button "Create Application". |
| M7 Tenant login | PASS | tenant-alice@rc.local → TENANT shell (tabs Home/Messages/Maintenance/More, no Capture FAB). |
| M8 Tenant Home dashboard | **FAIL** | "Could not load your dashboard. type 'Null' is not a subtype of type 'num' in type cast." Root cause FINDING-3 (Payment.portfolioId non-nullable vs DTO that omits portfolioId). |
| M9 Tenant Lease view | **FAIL** | "Could not load your lease. type 'Null' is not a subtype of type 'num'…" — same root cause FINDING-3 (lease screen consumes the portal snapshot → Payment.fromJson). |
| M10 Pay rent (Stripe) | **BLOCKED** | "Pay now" lives on the crashed Home dashboard (M8) — unreachable. Stripe config not exercised. |
| M11 Tenant submits maintenance | PASS | WorkOrder **42** created: Title "Bathroom fan", Status=0(New), TenantId=52, PropertyId=41, PortfolioId=6. (Title shortened from "Bathroom fan noisy" — adb input dropped trailing words; the create path itself works.) |
| M12 Tenant reply to conv 5 | PASS | New ConversationMessages row id 57, SenderRole='Tenant', Body "Thanks" on ConversationId=5. Visible in-thread. Satisfies W37. |
| M13 Tenant notifications | **BLOCKED** | Data present (Notifications id 8 "Welcome", id 9 "Lease Renewal", user 30, unread) and `/notifications/unread-count` returns 200 — but the in-app notifications LIST renders on the crashed Home dashboard (M8). No standalone tenant notifications screen. FCM push is device-credential-dependent (not tested). |

## Captures
- Receipt→Expense id: **none** (scan extraction failed; M3 FAIL). Manual fallback exists but no expense confirmed.
- Application→RentalApplication id: **none** (scan extraction failed; M6 FAIL).
- M11 tenant WO id: **42**.
- M12 message confirmation: ConversationMessages id **57**, SenderRole=Tenant, ConversationId=5, Body "Thanks".
- Tenant login worked? **YES** (Tenant A, user 30).

## Environment notes
- E-sign / Stripe / SMS: not exercised on mobile. Engine log shows SMS outbox failing (Twilio trial unverified-number 21608; SignalWire space "verify-test" 404) — provider creds are test placeholders, unrelated to mobile lane.
- Repeated Android ANR ("rental_command isn't responding") while typing on the Maintenance screen — debug build + slow emulator (frame times ~500ms, one 40s stall). Environmental, recovered each time; not a product crash. No Dart exception in flutter log.
- adb `input text` splits on spaces (multi-word strings truncate); cosmetic only — affected the WO title and reply text, not the create paths.

## FINDINGS

### FINDING-1 (HIGH) — Scan extraction broken: API saves upload where Engine can't read it
- **Symptom:** Every mobile scan (receipt 142/143, application 144) → ScanDraft Status=Failed, FailureReason "extraction failed (provider or processing error)", ModelId empty (fails before the LLM).
- **Root cause:** Engine worker throws `System.IO.FileNotFoundException: Could not find file '.../RentalCommand.Engine/uploads/<id>_scan-...'`. The uploaded files actually land in `/Users/blackcolours/dev/work/rental-management/uploads/` (repo root = API process cwd). Both API and Engine use `BasePath: "./uploads"` (relative) in their appsettings, resolved via `Path.GetFullPath` relative to each process's **current working directory** — but the two processes have different cwds, so they don't share an uploads dir.
  - Code: `RentalCommand.Api/Scanning/DiskFileStorage.cs:15` `_basePath = settings.BasePath ?? "./uploads"`; `:59-60` `Path.GetFullPath(_basePath)`. Configs: `RentalCommand.Api/appsettings*.json` and `RentalCommand.Engine/appsettings.json` both `"BasePath": "./uploads"`. Failure caught at `RentalCommand.Engine/Workers/ScanProcessingWorker.cs:215-222`.
- **Note:** Drafts 140/141 succeeded earlier (a prior run where both processes shared an uploads dir). The relative BasePath is a latent fragility; fix should make BasePath an absolute shared path (or content-root-relative) for both processes.
- **Impact:** Blocks scan-IN front door on mobile (M3, M6) and would equally affect web scan-IN whenever API/Engine cwds diverge.

### FINDING-2 (MEDIUM) — Failed-extraction fallback form is hardcoded to the Expense schema
- **Symptom:** On a Failed Application scan (draft 144, target=Application, button "Create Application"), the manual-entry form shows Expense/receipt fields: Vendor, Total, Subtotal, Tax, Transaction date, Category, Payment method, Notes. Filling them and tapping "Create Application" would create a malformed application.
- **Expected:** When extraction fails for an Application target, the fallback fields should be applicant fields (first/last/email/DOB/income), not expense fields.
- **Where:** mobile Scan Review screen (the "Extraction failed. You can still enter the values below and confirm." branch) — the field set is not switched by TargetEntityType.

### FINDING-3 (HIGH) — Tenant portal crashes: `Payment.portfolioId` non-nullable vs DTO that omits it
- **Symptom:** Tenant Home dashboard (M8), Lease view (M9), and Account history all render "type 'Null' is not a subtype of type 'num' in type cast". All API calls return 200; the crash is client-side during JSON deserialization.
- **Root cause:** `mobile/lib/core/models/payment.dart:61` parses `portfolioId: (json['portfolioId'] as num).toInt()` — **non-nullable**. But the server's `PortalPaymentResponse` (`RentalCommand.Api/DTOs/PortalDtos.cs:32-45`) has **no PortfolioId field** — `/portal/payments` JSON contains only id, leaseId, paymentType, status, amount, dueDate, paidDate, method, testId. So `json['portfolioId']` is null → `null as num` throws.
- **Path:** `mobile/lib/features/portal/tenant_portal_repository.dart:99-114` `snapshot()` fetches `/portal/payments` → `.map(Payment.fromJson)`; the whole snapshot (consumed by Home, Lease, Account history) throws on the first payment.
- **Fix options (for fixer agent):** make `Payment.portfolioId` nullable in the mobile model (`as num?`), or add `PortfolioId` to `PortalPaymentResponse`. The mobile-side nullable fix is the lower-risk one since the portal payment view doesn't need portfolioId.
- **Impact:** Breaks the entire tenant shell except Messages + the Maintenance create form. Blocks M8, M9, M10, M13.

## Leftovers in DB (created this run)
- ScanDrafts 142, 143 (Expense, Failed), 144 (Application, Failed) — harmless, not confirmed.
- WorkOrder 42 (tenant-submitted, New) — intended (M11).
- ConversationMessages 57 (Tenant "Thanks" on conv 5) — intended (M12).
