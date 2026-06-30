# Exploratory Test Report: Scan → Draft → Confirm (flagship capture flow)
Date: 2026-06-28
Tester: tester1
Duration: ~3.5h (much of it lost to / spent diagnosing two environment outages, see "Environment blockers")

## Scenario
Test the product's flagship — *"the computer does the typing for you"*: upload/scan a document → the
LLM extracts fields with confidence → review a **draft** → confirming creates the real record. Exercise
across record types (Expense, Payment, Lease, Application, WorkOrder); watch for wrong-field mapping,
persistence gaps, duplicate/no-op confirms, source-file retrieval, and silently-accepted low confidence.

## Summary
I exercised the full scan→draft→confirm chain for **all five** record types end-to-end and verified every
created record against the API/DB. The core flow is **fundamentally solid**: extraction (local `claude-cli:sonnet`)
mapped fields into the **right** places in every case, confirm **persisted** the reviewed values, the
duplicate/confirm-state guards all hold, and the source document follows the record. **After the environment was
fixed mid-run (see below), I re-drove all five types through the real browser** — Expense (draft 276 → expense
1037, with a UI property override + 4 line items persisted), Payment (270 → 3548), Lease (271 → 244 + new
property/unit/tenant), Application (review UI + a correct duplicate-guard + the reject dialog), and WorkOrder
(278 → 201, with a UI property selection persisted) — plus the in-app stored-file preview proxy. (During the
outages I had verified Expense/Application/WorkOrder via the same API endpoints the UI calls; the browser pass
re-confirmed them with no new defects.) I found **one clear bug** (`?full=1` → HTTP 400) and **one medium product
gap** (photographed/original documents are not retrievable at full resolution through the app). Field-mapping,
persistence, and the confirm/reject guards are **clean** — no wrong-field, no persistence-gap, no
duplicate-on-double-confirm, no orphan-on-reject.

> **Two environment blockers dominated this run** (details in their own section). Both were **resolved mid-run by
> stack restarts** (not by me). They are almost certainly env/config, not product defects, but they fully blocked
> browser E2E for a long stretch and likely hit the other scan tester too — surfaced here and via SendMessage so
> the orchestrator can stabilize the env.

---

## Bugs Found

### BUG-1: `GET /api/v1/scans/{id}/file?full=1` returns HTTP 400 (the documented way to fetch the original is broken)
**Severity:** Low–Medium
**Location:** API `GET /api/v1/scans/{id}/file` (scan source-file endpoint)
**Expected:** Per the endpoint's own comment — *"The original is available on `?full=1`"* — `?full=1` should
return the full-resolution original (HTTP 200).
**Actual:** `?full=1` → **HTTP 400** `{"errors":{"full":["The value '1' is not valid."]}}`. Only `?full=true`
works (200). `?full=0` also 400s. The parameter is a C# `bool`, and ASP.NET's bool model-binder accepts only
`true`/`false`, not `1`/`0`.
**Evidence:**
```
GET /api/v1/scans/275/file?full=true → 200 image/jpeg 370533 bytes (original)
GET /api/v1/scans/275/file?full=1    → 400 {"errors":{"full":["The value '1' is not valid."]}}
GET /api/v1/scans/269/file?full=1    → 400  (same)
```
**Code Reference:** `RentalCommand.Api/Controllers/ScanController.cs:535` (`[FromQuery] bool full`) and its
comment at `:547-548` documenting `?full=1`.
**Suggested Fix:** Make the param accept `1`/`0` — change to `[FromQuery] string? full` and treat `"1"`/`"true"`
(case-insensitive) as true — **or** correct the comment (and any client convention) to `?full=true`. The first
is better because it also fixes the contract for the planned mobile client.
**Why This Matters:** Any caller that follows the documented `?full=1` convention (the Flutter app, a dev, a
script) 400s instead of getting the original. It is also the only escape hatch to the full-resolution original
for image uploads (see BUG-2), so it being broken makes that gap unrecoverable.

### BUG-2: Original/photographed documents are not retrievable at full resolution through the app
**Severity:** Medium
**Location:** Web source-file proxy `web/src/routes/scan-file/[id]/+server.ts` + the review page preview/"Open in
new tab" + API `GET /scans/{id}/file` default behavior.
**Expected:** For a product whose core value is capturing source documents (photographed receipts/leases for
Schedule‑E/audit), the landlord should be able to view/download the **original** they captured.
**Actual:** For **image** uploads the app only ever serves a **downsized JPEG preview**, with no in-app path to
the original:
- The API `GET /scans/{id}/file` serves a re-encoded ~1000px JPEG thumbnail by default; the original is only on
  `?full=true`.
- The SvelteKit proxy `scan-file/[id]` forwards to `/scans/{id}/file` with **no** `full` param, and the review
  page's preview `<img>`/iframe and **"Open in new tab"** both use that bare proxy URL — so they always get the
  thumbnail.
- The documented escape hatch `?full=1` is broken (BUG‑1).
**Evidence:** Uploaded a 370,533-byte JPEG receipt photo (draft 275):
```
default /scans/275/file        → 200 image/jpeg 109,417 bytes  (Content-Disposition: "scan-275-preview")
        /scans/275/file?full=true → 200 image/jpeg 370,533 bytes  (the original)
        /scans/275/file?full=1    → 400
```
(PDF uploads are unaffected — they have no thumbnail, so the default already returns the original, verified on
drafts 269/270/271.)
**Code Reference:** `web/src/routes/scan-file/[id]/+server.ts:31` (fetches `/scans/{id}/file` with no `full`);
`web/src/routes/(protected)/scan/[draftId]/+page.svelte:607` (`fileUrl` has no `full`) used by the preview and the
"Open in new tab" link (~`:1055`); `RentalCommand.Api/Controllers/ScanController.cs:548-561` (thumbnail-by-default).
**Suggested Fix:** Give the user a real path to the original — e.g. have the "Open in new tab" link (and/or a
"View original" affordance) request `?full=true`, and/or let the proxy accept a `full` flag and forward it. (May
be partly a product decision, but at minimum the original must be reachable somewhere in the UI.)
**Why This Matters:** The landlord can't re-read fine print on a photographed receipt or produce the original
document for taxes/disputes — only a compressed 1000px copy. The original IS retained server-side; it's just not
reachable through the app for the most common capture method (phone photos).

---

## Potential Issues (need investigation)

### P-1: A transient `/auth/me` failure logs an authenticated user out of every protected page (SSR)
`web/src/hooks.server.ts:67-87` — when `GET /auth/me` returns a non-401 error or the fetch throws (network/cert),
the catch path sets `event.locals.accessToken` but **not** `event.locals.user`. The `(protected)` layout guard
checks `locals.user`, so the user is **303-bounced to `/login`** despite holding a valid token — even though the
comment says *"preserving existing token … rather than logging the user out."* I hit repeated `/login` bounces
each time the API was briefly bounced during the env fixes. **Caveat:** I could not cleanly separate this from the
genuine env instability (the API really was down/restarting), so I can't claim it fires under stable operation —
hence "potential". If intended (can't confirm identity ⇒ deny), the comment is misleading; if not, a brief API
hiccup shouldn't drop authenticated SSR sessions. Code ref: `web/src/hooks.server.ts:67-87` + the `(protected)`
`+layout.server.ts` guard.

### P-2: Extraction emits a stray `target_entity_type` field into the review set
Application (272) and WorkOrder (273) drafts came back with an extra extracted field `target_entity_type`
(value "Application"/"WorkOrder", conf 1.0) alongside the real data fields. It's harmless (the confirm builders
ignore unknown keys), but it surfaces in the draft's `fields` list and could render as a junk field in a generic
field view. Worth confirming it's filtered from the review UI for those types. Likely originates in the
extraction schema/prompt (`RentalCommand.Api/Scanning/ApplicationExtractionSchema.cs` /
`WorkOrderExtractionSchema.cs`). Not reproduced as a visible UI defect (those types use curated field lists).

---

## Observations (work as built; could be better)
- **Payment `DueDate` = the check's transaction date.** `ConfirmAsPaymentAsync` sets both `DueDate` and
  `PaidDate` to the extracted transaction date (`ScanService.cs:597-598`), not the rent-period due date. Fine for
  a received check marked Paid, but the "due date" is then cosmetic/misleading. Acknowledged in code; minor.
- **Confidence is surfaced, not blocked — and that's reasonable.** Low (<0.5) fields get a red label + autofocus,
  medium (0.5–0.8) a muted label (`[draftId]/+page.svelte:530-541`). A *fully* empty/garbage extraction is failed
  loudly (never stored as a blank "Reviewing" draft) by `ScanProcessingWorker.GetExtractionFailureReason`
  (`:406-421`). I did not observe a meaningful zero-confidence value being silently accepted — most fields came
  back at conf 1.0 on the born-digital samples.
- The review page correctly **gates confirm**: Payment needs a lease, WorkOrder/Loan need a property, Lease needs
  property+unit (or create-new + address + unit#), Application needs first/last name, Expense needs amount > 0.
  All gates fired (button disabled + helper text) as the code says.

---

## Clean areas (verified, no defects found)
- **Expense** (draft 269 → Expense 1034): subtotal 322.50 / tax 24.19 / total 346.69 all **separated correctly**,
  category Repairs, status Paid, `PaidAt` = transaction date 2026-04-12, payment method Visa + card ...4417, 3
  line items with correct qty/price/amount, `receiptData` jsonb present, **vendor auto-created** (id 65 "Apex
  Plumbing Co." with phone). No wrong-field; no persistence gap.
- **Payment** (draft 270 → Payment 3548, *browser-driven*): payer "Marcus Williams", check #1042,
  Huntington National Bank, $1500, date 2026-05-01, linked to the selected lease 239, Type=Rent/Status=Paid.
  UI showed "Payment recorded — $1,500.00". (Screenshot below.)
- **Lease import** (draft 271 → Lease 244, *browser-driven*): the empty-portfolio **bootstrap** created
  Property 138 "Westview Four-Plex" (4812 Westview Drive, Columbus OH), Unit 234, Tenant 270 "Marcus Williams",
  and an Active lease with rent 1250 / deposit 1250 / late fee 75 / due day 1 / term 2025-08-01→2026-07-31. The
  "When you confirm, this lease will: Create … / Create …" proposal matched the outcome.
- **Application** (draft 272 → Application 20): first/last/email/phone/DOB/employer/income/current-address all
  persisted; status Submitted.
- **WorkOrder** (draft 273 → WorkOrder 197): title/description/category(Plumbing)/priority(High)/estimatedCost
  $210 persisted; `property_id` 138 and `vendor_id` 65 were **grounded** to the right in-portfolio records by the
  LLM and validated in-portfolio on confirm; unit left unlinked (Unit 1 not in portfolio) — correct.
- **Confirm/duplicate guards:** confirming a **Pending** draft → 400 "must be reviewed first"; **double-confirm**
  of a confirmed draft → 400 "already confirmed" with **exactly one** record created (no duplicate); confirming a
  **Rejected** draft → 400 "already rejected".
- **Reject/abandon** (draft 274 via API; draft 277 via the **browser reject dialog**): status → Rejected, **no
  orphan record** created (`createdEntityId` null).
- **Source file follows the record** after confirm (StoredFile re-key): `/expenses/1034/receipt` → 200 PDF
  (2465 b), `/leases/244/scan` → 200 PDF (3895 b), `/work-orders/197/scan` → 200, `/scans/269/file` → 200 PDF;
  and via the **browser same-origin proxy** `/scan-file/276` → 200 application/pdf (what the review-page preview uses).

### Browser re-verification pass (after the env was fixed — all five types driven through the UI)
- **Expense in-browser** (draft 276 → Expense 1037): full UI — vendor/subtotal 140.96/tax 10.57/total 151.53 all
  correct, 4 line items shown with a reconciling "$140.96" running total (no mismatch warning), "Low confidence"
  labels on empty fields, paid/unpaid toggle defaulted to "Already paid", I selected **Westview Four-Plex** in the
  property dropdown → the override **persisted** (`propertyId 138`), all 4 line items persisted, category Supplies,
  Paid, paidAt 2026-04-02. Success card "Expense created — $151.53".
- **WorkOrder in-browser** (draft 278 → WorkOrder 201): required property selector gated confirm; I picked Westview
  Four-Plex → persisted `propertyId 138`; title/Plumbing/High/$210 persisted; this extraction was *more
  conservative* than draft 273 — it left property/unit/vendor unassigned and wrote its grounding reasoning into
  Notes ("Unit 1 is NOT in the grounding list… vendor names differ enough that vendor_id was not assigned — flag
  for human review") — good human-in-the-loop behavior. Source `/work-orders/201/scan` → 200 PDF.
- **Application in-browser** (draft 277): applicant form correctly pre-filled (Jasmine/Carter/email/phone/employer/
  income); confirm correctly **blocked by the duplicate guard** because I'd already created Application #20 from the
  same person — a **clear, specific toast** surfaced: *"An application for jasmine.carter@email.com already exists
  as application #20. Review the existing application before creating another."* (good UX; not a bug — my own data
  collision). Then rejected the dup via the browser reject dialog.

---

## Environment blockers encountered (resolved mid-run; almost certainly env/config, not product bugs)
These are flagged for the orchestrator (also sent via SendMessage). They blocked browser E2E for a long stretch.
1. **API served a self-signed cert the web SSR didn't trust → every protected route bounced to `/login`.** A plain
   `curl https://localhost:5667/scan` with a valid `rc_access_token` cookie returned **303→/login with zero API
   calls**: `hooks.server.ts` calls `GET https://localhost:5666/api/v1/auth/me`, but the API leaf cert was
   `DEPTH_ZERO_SELF_SIGNED_CERT` (rejected even with `NODE_EXTRA_CA_CERTS=…/mkcert/rootCA.pem`), so the fetch threw,
   `locals.user` stayed null, and the guard bounced. **Resolved** when the API was relaunched with the mkcert cert
   (`Kestrel__Certificates__Default__Path=web/.cert/api-cert.pem`).
2. **After the API relaunch, `RentalCommand.Engine` did not come back → scan extraction was dead.** With no
   `ScanProcessingWorker` (Engine `Program.cs:159`) running, every uploaded draft sat in **Pending** forever
   (draft 270 was stuck ~6 min). **Resolved** when the Engine was restarted (~23:19) and drained the queue.
- Secondary symptom (while #1 was active): the headless browser rejected the login action's `Secure` cookies
  (zero cookies stored after a 200 login), consistent with cert trust not being established browser-side either;
  it cleared up once certs were regenerated and the **real UI login worked** (landed on Dashboard, 3 auth cookies).

## What was tested (repro steps)
1. Login `admin@rentalcommand.local` (UI once certs were fixed; API bearer for the API-driven confirms).
2. `/scan` → pick doc type → upload sample → `/scan/{draftId}` → wait for Reviewing → review fields vs the
   document → confirm → open/verify the created record → fetch the source file. Reject path on a separate draft.
3. Samples used: `Docs/sample-scans/01-receipt-apex-plumbing.pdf` (Expense),
   `Docs/sample-scans/11-rent-check-marcus-williams.pdf` (Payment),
   `samples/scans/lease_westview_unit2_marcus_williams.pdf` (Lease),
   `samples/scans/rental_application_jasmine_carter.pdf` (Application),
   `samples/scans/work_order_westview_unit1_sink_leak.pdf` (WorkOrder),
   `Docs/sample-scans/02-receipt-handy-pro.pdf` (Expense, rejected),
   `samples/scans/repair_receipt_apex_plumbing_dec2025_photo.jpg` (image, file-retrieval test).
4. Records created during this run (all NEW, inherently safe): drafts 269–278; Expense 1034 & 1037 (+Vendor 65);
   Payment 3548; Lease 244 (+Property 138, Unit 234, Tenant 270); Application 20; WorkOrder 197 & 201; drafts 274 &
   277 rejected; draft 275 left in Reviewing (image file-retrieval test).

## Evidence
- Screenshots (browser): `output/playwright/tester1-scan01-payment-confirmed.png` (confirmed Payment);
  `output/playwright/tester1-scan-confirmed-list.png` (scan history).
- `?full=1` 400 + image thumbnail/original sizes: inline above (BUG-1, BUG-2).

## Coverage note (browser vs API)
- **All five record types were ultimately driven through the real browser** (Expense 276→1037, Payment 270→3548,
  Lease 271→244, Application 277 review/guard/reject, WorkOrder 278→201), once the env was fixed. The earlier
  API-driven confirms (Expense 269→1034, Application 272→20, WorkOrder 273→197) happened during the two outages and
  were re-confirmed by the browser pass — no behavior differed between the UI path and the API path.
- No flow was blocked by the scan/LLM itself — local `claude-cli:sonnet` extraction worked well once the Engine
  was back (born-digital PDFs extracted in ~2–12s with high confidence and correct field mapping).
