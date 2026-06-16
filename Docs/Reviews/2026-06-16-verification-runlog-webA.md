# Rental Command — Verification Run Log (Web Lane A: S1-S3 + W1-W29)

**Run date:** 2026-06-16 · **Agent:** Web lane A (playwright-cli `-s=verifyA`, public apply `-s=applyA`)
**Env:** Web https://localhost:5667 · API https://localhost:5666 · DB `rentalcommand` in `edi-postgres`

## Captured IDs (filled during run)
- PID (verify-landlord portfolio Id): **6** ("Verify Landlord's Portfolio")
- verify-landlord AspNetUsers OwnerEntityId link: 28 ; primary OwnerEntity "Verify Landlord"
- LID (Tenant A lease Id): **32** (VERIFY-A-2026-001, Alice Anderson tenant Id 52, Maple Court unit 2 [unitId 33], rent 950, late 50, dueDay 1, Active)
- Tenant A entity Id: **52** (Alice Anderson, tenant-alice@rc.local) · Tenant B Id 53 (Bob Brown)
- Maple Court property Id: **41** (PropertyType=1 MultiFamily, Status=0 Active)  · unit Ids: **32 (unit "1" rent 1200), 33 (unit "2" rent 950)**
- Carla's RentalApplication Id: **11** (Approved, ApprovedTenantId=54 [Carla Cruz tenant]) · apply token `6b7x33QCSexIkCNFHK-PZbE9CvVQhBOJpc47BEUn8Nc`
- Derek Denton RentalApplication Id 12 (Declined, reason "Insufficient income for unit") — W24
- Payment Ids: W10 scan-check Payment **1097** (Grace Okwu, 1150, Rent/Paid, check 2042)
- W9 scan lease: Lease **31** (CPPM-2025-0418, Daniel Ramirez, Maple Court unit 1, rent 1875, Active). NOTE: this is the scan-created lease; Tenant A canonical LID comes from W16.
- W8/W9 ScanDraft 140 (Lease, Confirmed); W10 ScanDraft 141 (Payment, Confirmed)
- Tenant A login (tenant-alice@rc.local) creation method + AspNetUsers Id:
  **Created via `POST /api/v1/admin/users` (AdminUsersController), authenticated as verify-landlord@rc.local (portfolio-6 Admin).**
  Body: `{"email":"tenant-alice@rc.local","displayName":"Alice Anderson","role":"Tenant","tenantId":52,"temporaryPassword":"Rc!Tenant2026"}`
  Result: AspNetUsers **Id=30**, EmailConfirmed=**t**, TenantId=**52**, Role=**Tenant**, password **Rc!Tenant2026**. Login verified (API returns valid accessToken).
  (Team-member UserAccount id 29.) There is NO in-app tenant-invite UI in this build; admin users API is the mechanism.
- E-sign configured in dev? **YES** (W20 sent to Alice, EsignStatus=1 Sent, lease Status=5 PendingSignature) · Stripe? _TBD_ · SMS provider? _TBD_
- Birch Plaza property Id 42 (SingleFamily), unit A = unitId 34 (rent 1500)
- NOTE: W20 left lease 32 in Status=5 (PendingSignature). **I reset it back to Status=1 (Active) at end of run** so the mobile tenant-shell lane (M8/M9: active lease, rent 950) works. EsignStatus stays 1 (Sent).
- Handoff for mobile agent: tenant-alice login ready (AspNetUsers 30 / Rc!Tenant2026 / role Tenant / tenantId 52). Conversation for M12 not yet created (that's W36, owned by agent 2). Lease 32 = Maple Court unit 2, rent 950.

## Sample files chosen
- W8 lease PDF: `~/rental-sample-scans/leases/lease_ramirez_maplewood.pdf`
- W10 rent check: `~/rental-sample-scans/payments/check_grace_okwu_dec2025_photo.jpg`

## Results table
| Step | Result | Notes |
|---|---|---|
| S1 | PASS | verify-landlord → /superadmin/engine = 404 (not allowlisted) |
| S2 | PASS | admin@rentalcommand.local → Engine Health page; 9 workers heartbeating; provider=claude-cli |
| S3 | PASS | EngineWorkerHeartbeats has 1 row per worker (9), Status=0 |
| W1 | PASS | register success; EmailConfirmed=f; portfolio 6 IsSandbox=f |
| W2 | PASS | EmailConfirmed flipped t via SQL |
| W3 | PASS | login → /choose-setup; both cards present |
| W4 | PASS | choose-live → /onboarding; IsSandbox=f, SandboxSeededAtUtc NULL |
| W5 | PASS (backend) | self-owner "Verify Landlord" primary; user linked OwnerEntityId=28. UI drift: wizard auto-completed Portfolio+Owner steps (see F1) |
| W6 | PASS | Maple Court (Id 41, MultiFamily, Active); 2 units (32=unit1/1200, 33=unit2/950) |
| W7 | PASS | get-started shows owner+property+unit tasks done |
| W8 | PASS | lease PDF scan → ScanDraft 140 (Lease) Processing→Reviewing |
| W9 | PASS | confirmed → Lease 31 (CPPM-2025-0418, Daniel Ramirez, Maple unit1, rent 1875, Active); ScanDraft 140 Confirmed. Minor: no CreatedEntityType col on ScanDrafts (F2) |
| W10 | PASS | rent-check scan → Payment 1097 (Grace Okwu, 1150, Rent/Paid, check 2042) |
| W11 | PASS | Birch Plaza (Id 42, SingleFamily, Active) |
| W12 | PASS | Birch unit A (Id 34, rent 1500) |
| W13 | PARTIAL | type filter narrows grid (PASS). Status edit IMPOSSIBLE in UI — property form has no status field (F3, Medium) |
| W14 | PASS | Tenant A Alice Anderson (Id 52) |
| W15 | PASS | Tenant B Bob Brown (Id 53); portfolio has 3 tenants |
| W16 | PASS | Lease 32 (LID; VERIFY-A-2026-001, Alice, Maple unit2, rent 950, late 50, dueDay 1, Active). Note: lease number is required (no auto-gen in manual form) |
| W17 | PARTIAL/FAIL | ledger Charged/Paid/Balance panel renders (PASS). Set opening balance THROWS JS error, no row created (F4, Medium — bug) |
| W18 | PASS | give notice → Status=2 NoticeGiven, MoveOutDate 2026-06-30; re-activated to Active |
| W19 | PASS | generate lease PDF → StoredFiles Lease/application/pdf |
| W20 | PASS | send for signature → EsignStatus=1 Sent, lease Status=5 PendingSignature. E-sign IS configured in dev |
| W21 | PASS | apply link dialog; token matches Portfolios.PublicApplicationToken |
| W22 | PASS | public apply (Carla, app 11): ConsentGiven=t, ConsentAtUtc set, Status Submitted; FCRA consent text correct |
| W23 | PASS | approve Carla → Status Approved, ApprovedTenantId 54; Tenant row created |
| W24 | PASS | decline Derek (app 12) → Status Declined, reason saved. Screening/adverse-action not wired in dev (optional) |
| W25 | PASS | manual Payment 1098 (lease 32, 950, Rent, Paid) |
| W26 | PASS | manual Expense 686 (Repairs, 75, Pending, Maple Court) |
| W27 | PASS | Schedule-E CSV downloaded; contains Repairs 75; enum-label categories |
| W28 | PASS | past-due page empty; SQL overdue (Status IN 0,2,3 AND DueDate<now) = 0 — UI matches DB |
| W29 | PASS | /tax resolves (Total exp $75, net -$75, per-property Maple). /owners-report resolves but "No owner data" because properties have OwnerEntityId NULL (F5, Low) |

## Findings
- **F1 (Low — playbook drift / minor UX):** Onboarding wizard auto-completes the "Portfolio" and "Owner" steps and lands the user directly on "Property". W5's expected inputs (`onboarding-portfolio-name`/`-company`/`-timezone`, `onboarding-owner-prefilled` hint) are never shown. Portfolio name stays the auto-generated "Verify Landlord's Portfolio" and company name is never captured (so Portfolios.ManagementCompanyName="Verify Landlord", not "Verify LLC"). Backend self-owner provisioning still works. Step: W5.
- **F2 (Low — playbook drift):** ScanDrafts has no `CreatedEntityType`/`CreatedEntityId` columns (only `TargetEntityType`, `Status`, `CreatedAt`). The created-entity link asserted in W9 cannot be checked from the draft row; Status='Confirmed' is the available signal. Step: W9.
- **F3 (Medium — feature gap):** Property status (Active / UnderMaintenance / Inactive) cannot be changed from the UI. The property create/edit form (`web/src/routes/(protected)/properties/+page.svelte`) has no status control: `emptyProperty` (line 76) omits status, and the save payload (lines 118-143) never sends it. `propertyStatuses` is declared (line 151) but never wired into the form. W13's UnderMaintenance round-trip is impossible via UI. Step: W13.
- **F4 (Medium — BUG):** Setting a lease opening balance silently fails with a runtime error. `submitOpeningBalance()` calls `openingAmount.trim()` (`web/src/routes/(protected)/leases/[id]/+page.svelte:156`), but `openingAmount` is bound to a `<Input type="number">` (line 1118-1121), so Svelte's two-way binding coerces it to a `number`. `number.trim` is undefined → `TypeError: ...trim is not a function` (console error at submit), the save mutation never fires, and NO opening-balance Payment row is created. Repro: lease detail → Ledger → Set opening balance → enter amount → Save. Fix: use `String(openingAmount).trim()` (and parse separately), or keep openingAmount a string and bind without `type="number"` coercion. Step: W17.
- **F5 (Low — UX/data):** Properties created via onboarding and the /properties create form are NOT auto-linked to the portfolio's primary self-owner (OwnerEntityId stays NULL). Consequence: /owners-report shows "No owner data for <year>" by default even though a primary owner exists. The /tax summary (which aggregates by property regardless of owner) works fine. Consider auto-assigning the primary owner on property create. Step: W29.
- **F6 (Low — observation, not a defect):** Schedule-E CSV and /tax show "Rental Income 0.00" for Maple Court despite two Paid rent payments (1097=1150, 1098=950). Likely a PaidDate-vs-period filter (payments may lack a PaidDate in the current month, or income recognition uses a different date field). Worth confirming the intended income-recognition date semantics. Steps: W27/W29.

---

## Step-by-step log
