# Live Verification — Needs-User Bucket (Run 2026-06-28-001)

**Tester:** verify-nu (live verification on the running stack)
**Date:** 2026-06-29
**Environment:** Web https://localhost:5667 · API https://localhost:5666 (mkcert TLS) · shared dev Postgres (`rentalcommand`)
**Auth:** `admin@rentalcommand.local` / dev login. API bearer token used out-of-band for setup + assertions.
**Test data marker:** `QA-VNU-*` (property #141 "QA-VNU-… OP2", units A1/B1/B2/B3/C1/C2, tenants/leases, deposits #184–#187, tenants #280/#281, scan drafts #279/#280). Dev DB is shared/volatile — recreate if vanished.
**Notifications:** all tenant emails used `@example.com` (reserved domain — no real delivery).

## Summary

| # | Item | Result |
|---|------|--------|
| 1 | OP-2 — reuse a soft-deleted unit number | **PASS** |
| 2 | "Withheld" deposit state | **PASS** |
| 3 | Over-deduction UI warning | **PASS** |
| 4 | Portal-access provisioning (full flow) | **PASS** |
| 5 | Client validation parity | **PASS** (logic/messages/wiring confirmed live; UI click-through not reproducible via CLI — manual spot-check advised) |
| 6 | Stray `target_entity_type` gone from scan review | **PASS** |
| 7 | Money KPI "All-time" label | **PASS** |
| 8 | Schedule-E vs owner-statement basis note | **PASS** |
| 9 | Unit status help text | **PASS** |
| 10 | Unit "outstanding" current-lease scope | **COVERED BY UNIT TEST** (not attempted live) |
| 11 | Owner-statement mgmt-fee rounding | **COVERED BY UNIT TEST** (not attempted live) |
| 12 | Auth resilience on transient `/auth/me` failure | **COVERED BY UNIT TEST** (not attempted live) |

No FAILs. No BLOCKED.

---

## Detail

### 1 — OP-2: reuse a soft-deleted unit number — PASS
Via API on a fresh property (#141):
- `POST /units` `{propertyId:141, unitNumber:"A1"}` → **HTTP 201** (unit #237).
- `DELETE /units/237` → **HTTP 204** (soft delete).
- `POST /units` `{propertyId:141, unitNumber:"A1"}` again → **HTTP 201** (unit #238). `GET /units/238` → 200, exists.
- DB proof of root-cause fix — the partial unique index now carries the `DeletedAt` filter:
  `CREATE UNIQUE INDEX "IX_Units_PropertyId_UnitNumber" ON public."Units" USING btree ("PropertyId","UnitNumber") WHERE ("DeletedAt" IS NULL)`.

Before the fix the second create 409'd because the unique index was unfiltered.

### 2 — "Withheld" deposit state — PASS
Created three deposit holdings (each held $1,000) on three QA leases and drove each via API:
| Deposit | Action | API status | Grid badge | Detail badge |
|---|---|---|---|---|
| #184 | deduction $1,000 (full), then return; net $0 | `Withheld` | **Withheld** | **Withheld** |
| #185 | deduction $400 (partial), then return; net $600 | `PartiallyReturned` | **Partially Returned** | — |
| #186 | no deduction, return; net $1,000 | `Returned` | **Returned** | — |

`/deposits` grid screenshot shows all three rows with the correct, visually distinct badges (Withheld is red/error tone, distinct from amber Partially Returned). `/deposits/184` detail shows the **Withheld** badge in the header.
Evidence: `output/playwright/verify-nu-02-deposits-grid-badges.png`, `verify-nu-02-withheld-detail.png`.

### 3 — Over-deduction UI warning — PASS
Deposit #187 (held $500) + a $500-exceeding deduction ($800, total deductions $800 > $500 held). `/deposits/187` detail shows the warning:
> "Deductions exceed the deposit held by **$300.00** — the net refund is $0.00 and the tenant will owe the difference."

(`data-testid="deposit-over-deduction-warning"`.) Evidence: `verify-nu-03-over-deduction.png`.

### 4 — Portal-access provisioning (full flow) — PASS
- Tenant #280 with email `tenant-vnu-030822@example.com`. Tenant detail page shows enabled **Grant portal access** button (`data-testid="tenant-detail-grant-portal-access"`).
- Click → success toast: **"Portal access granted — QAVNU Portal-030822 can sign in with their email."**
- DB proof: `AspNetUsers` row **id 61** created (`UserName`/`Email` = the tenant email, `EmailConfirmed=t`), role **Tenant**. Pre-check was 0 rows.
- Idempotent: second `POST /tenants/280/portal-access` → `{"status":"AlreadyExisted","alreadyExisted":true}` HTTP 200.
- Negative case: tenant #281 (no email) → button **disabled**, `title="Add an email to this tenant before granting portal access."`
Evidence: `verify-nu-04-grant-portal-toast.png`, `verify-nu-04-noemail-disabled.png`.

### 5 — Client validation parity — PASS
The running web bundle's `propertySchema`/`unitSchema` (served at `/src/lib/schemas/index.ts`) carry the bounds. Running the **app's own** `parseForm` in the live browser produced the friendly client messages for every sub-case:
| Input | Client message |
|---|---|
| Property Name = 201 chars | "Name must be 200 characters or fewer" |
| Unit number = 51 chars | "Unit number must be 50 characters or fewer" |
| Bedrooms = 100 | "Bedrooms cannot exceed 99" |
| Bathrooms = 100 | "Bathrooms cannot exceed 99" |
| Market rent = 100,000,000 | "Market rent cannot exceed 99,999,999" |
| Valid unit | no errors |

The served module is the **same URL the page imports** (`/src/lib/schemas/index.ts`, no `?v=` cache-bust divergence) — so this is the actual code the form runs, not a stale copy.

The inline-render path is confirmed live: submitting the New Property form with an empty/invalid field renders inline `*-error` messages (e.g. "Name is required", "Address is required") **before any server call** — `parseForm` short-circuits the mutation in `submitProperty`.

**Caveat (automation limitation, not a confirmed defect):** I could not get a clean end-to-end *click-through* of the over-limit message. The New-Property / Add-Unit forms are Bits-UI dialogs whose inputs are nested Svelte 5 `$bindable`s; the headless playwright-cli's `fill`/`type`/synthetic-event injection did not reliably land a >limit value in the bound `form.*` state (results were inconsistent across attempts, including after the post-submit re-render replaces the input node). So while the schema + messages + inline-wiring + 16 passing unit tests (`web/src/lib/schemas/property-unit-max-length.test.ts`) all confirm the fix is present and correct, the user-facing over-limit path was **not** positively reproduced through the UI. **Recommend a ~30s manual spot-check** (type a 201-char name in the New Property dialog and confirm the inline "Name must be 200 characters or fewer" appears). Evidence: `verify-nu-05-property-form-inline-validation.png`.

### 6 — Stray `target_entity_type` gone from scan review — PASS
LLM extraction is live in this environment (drafts carry real model output). Created two **post-fix** drafts by re-uploading the source PDFs:
- New Application draft **#279** → 14 fields, names = email, phone, unit_id, employer, id_last4, last_name, first_name, property_id, applying_for, date_of_birth, co_signer_name, monthly_income, current_address, desired_move_in_date — **no `target_entity_type`**.
- New WorkOrder draft **#280** → 11 fields (notes, title, unit_id, category, lease_id, priority, tenant_id, vendor_id, description, property_id, estimated_cost) — **no `target_entity_type`**.
- Review UI `/scan/279`: page text contains no "target_entity_type" / "target entity type".
- Contrast: the **pre-fix** draft #277 (created earlier today) still had 15 fields including the stray `{"name":"target_entity_type","value":"Application"}`. The draft's internal `targetEntityType` property is correctly retained (fixed from the upload choice, not extracted).
Evidence: `verify-nu-06-scan-review-no-target-entity-type.png`.

### 7 — Money KPI "All-time" label — PASS
`/accounting` KPI strip is labeled **"All-time"** immediately above Total Collected / Outstanding / Overdue / Expenses. Evidence: `verify-nu-07-accounting-all-time.png`.

### 8 — Schedule-E vs owner-statement basis note — PASS
- `/tax`: "**Accrual basis:** expenses count when incurred (any status), by the date incurred. The statement uses cash basis (paid expenses only), so the same property can show a different expense total there."
- `/owners-report`: "**Cash basis:** only paid expenses count, by the date paid. The Tax (Schedule E) page uses accrual basis (all incurred expenses), so the same property can show a different expense total there."
Complementary cross-links on both pages. Evidence: `verify-nu-08a-tax-basis-note.png`, `verify-nu-08b-owners-report-basis-note.png`.

### 9 — Unit status help text — PASS
`/properties/141`: the misleading phrase "change the status of individual units" is **absent** from the live page. The Details card help tooltip (hover) reads:
> "Unit count and occupancy are read-only — unit count updates as you add or remove units, and **occupancy updates automatically as leases move tenants in and out**."
Occupancy is described as lease-driven. Evidence: `verify-nu-09-occupancy-help.png`.

### 10 — Unit "outstanding" current-lease scope — COVERED BY UNIT TEST
Covered by `RentalCommand.Api.Tests/Domain/UnitDashboardServiceTests.cs` (10/10 per the implementation note). Not attempted live: a faithful repro needs a unit with a current lease **and** a prior ended lease carrying leftover (unpaid, materialized) rent charges, then reconciliation against the Accounting KPI — not "easy" to stand up deterministically on the shared dev DB. Deferred to the unit suite per the run guidance.

### 11 — Owner-statement mgmt-fee rounding — COVERED BY UNIT TEST
Covered by `RentalCommand.Api.Tests/Domain/OwnerStatementServiceTests.cs`. Latent/sub-cent per-line rounding; requires crafted fractional fees to trigger. Not attempted live per guidance.

### 12 — Auth resilience on transient `/auth/me` failure — COVERED BY UNIT TEST
Covered by `web/src/lib/server/jwt-claims.test.ts` (8/8 helper tests — SSR decodes the first-party token's claims honoring `exp`). Not attempted live: a safe repro would require inducing a transient API failure (bouncing the API), which would disrupt the shared stack. Skipped per guidance.

---

## Notes / housekeeping
- Created QA-VNU data left in place (dev DB is shared/volatile; data is marked). Includes `AspNetUsers` id 61 (a `@example.com` tenant login) and scan drafts #279/#280.
- No real notifications sent (all `@example.com`).
</content>
</invoke>
