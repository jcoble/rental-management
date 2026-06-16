# Rental Command — Full‑System End‑to‑End Verification Playbook

**Authored:** 2026‑06‑16 · **Venue:** LOCAL DEV · for execution by Sonnet agents driving the LIVE app.

This playbook is a coverage master. It exercises EVERY major surface (onboarding → scan → properties → tenants → leases → applications → payments/accounting → work‑orders → messaging → notices/automation → notifications → dashboard → settings → tenant portal) across a WEB lane (playwright‑cli) and a MOBILE lane (adb on an Android emulator), with a small cast of users.

Each step is: `[ ] N. ACTOR — SURFACE — action → expected → VERIFY (UI assertion + SQL)`. Run steps in order; later steps depend on records created earlier. All `data-testid` values, routes, API endpoints, enum values, and table/column names below were read from source; treat them as exact.

---

## 0. Environment

| Thing | Value |
|---|---|
| Stack start | `./scripts/start-dev.sh` (from repo root `/Users/blackcolours/dev/work/rental-management`) |
| Web | `https://localhost:5667` |
| API | `https://localhost:5666` (http `http://localhost:5665`); mounts under `/api/v1/*` |
| DB | Postgres `rentalcommand` in container `edi-postgres` |
| DB query | `docker exec edi-postgres psql -U postgres -d rentalcommand -tAc "<SQL>"` |
| API log | `/tmp/rentalcommand-api.log` |
| Engine log | `/tmp/rentalcommand-engine.log` |
| Seeded dev admin | `admin@rentalcommand.local` / `Admin123!` (EmailConfirmed=true; `IdentitySeeder.cs:137`) |
| LLM for scan | dev‑only `claude-cli` provider (`Assistant:Provider=claude-cli`, `Engine/Program.cs:94`). Extraction takes tens of seconds; the `ScanProcessingWorker` polls every 2s (`ScanProcessingWorker.cs:29`). |

**Identifiers are quoted + case‑sensitive in Postgres.** Table names are the EF default = pluralized DbSet name (`Portfolios`, `Properties`, `Units`, `Tenants`, `Leases`, `Payments`, `Expenses`, `WorkOrders`, `Vendors`, `VendorDispatches`, `Conversations`, `ConversationMessages`, `Notifications`, `NoticeDrafts`, `RentalApplications`, `OwnerEntities`). Columns are PascalCase (`"PortfolioId"`, `"MonthlyRent"`).

### Web driving (playwright‑cli)
```
playwright-cli -s=<name> open --headed https://localhost:5667
playwright-cli -s=<name> snapshot            # YAML to .playwright-cli/page-*.yml; gives refs
playwright-cli -s=<name> click <ref>
playwright-cli -s=<name> fill <ref> "<text>"
playwright-cli -s=<name> goto <url>
playwright-cli -s=<name> screenshot
playwright-cli -s=<name> close
```
Snapshot FIRST, then act by `ref`. Prefer locating by the `data-testid` shown in the snapshot. Parallel web work uses distinct `-s=<name>` sessions.

### Mobile driving (adb)
```
ADB="$HOME/Library/Android/sdk/platform-tools/adb -s emulator-5554"
$ADB shell uiautomator dump && $ADB pull /sdcard/window_dump.xml   # read the tree
$ADB shell input tap <x> <y>
$ADB shell input text "<text>"           # field must be focused first
$ADB shell input keyevent 4              # back
$ADB exec-out screencap -p > /tmp/screen.png   # then view it
```
Flutter exposes almost no testids — drive by VISIBLE TEXT (find the bounds in the uiautomator XML, tap the center). The mobile app points at `https://10.0.2.2:5666` (`mobile/lib/core/config/app_config.dart`, debug default).

### Single‑emulator constraint
**Only ONE agent drives `emulator-5554` at a time.** The MOBILE lane is serialized. WEB‑lane steps may run in parallel playwright sessions (different `-s` names) as long as they don't depend on each other's records. Where a WEB step depends on a MOBILE step (or vice‑versa) it is called out as `DEPENDS:`.

### Web ÷ Mobile split (per the brief: HALF the scan‑IN and major flows on each)
- **WEB lane owns:** signup/onboarding, scan‑IN of **lease (PDF)** + **rent‑check→payment**, properties/units, applications (apply‑link OUT + approve), payments/accounting + reports, work‑order **landlord triage→assign→dispatch→complete→pay provider**, notices + automation verification, dashboard/settings, landlord→tenant messaging.
- **MOBILE lane owns:** scan‑IN of **receipt→expense** + **rental application**, tenant‑shell portal end‑to‑end (lease view, pay rent, **tenant submits maintenance request**, **tenant→landlord message reply**, notifications), landlord mobile dashboard + capture FAB sanity.

---

## 1. Cast — accounts to create

| Role | Email | Password | How created | Notes |
|---|---|---|---|---|
| **Super admin (platform operator)** | `admin@rentalcommand.local` | `Admin123!` | already seeded | Becomes platform admin ONLY if its email is in the `PlatformAdmin:Emails` allowlist (see §2). |
| **Landlord/admin (fresh)** | `verify-landlord@rc.local` | `Rc!Verify2026` | WEB step 1 (register) | The primary actor; owns a fresh portfolio. |
| **Tenant A** | `tenant-alice@rc.local` | `Rc!Tenant2026` | created via lease/tenant + portal invite (step set 4/7) | Real app user; mobile tenant shell. |
| **Tenant B** | `tenant-bob@rc.local` | `Rc!Tenant2026` | second tenant/lease | For messaging + second lease. |
| **Vendor (provider)** | `vendor-vic@rc.local` (email) `+13305550147` (phone) | n/a (not a login) | Vendor entity (step set: work orders) | Contacted via tel:/sms:/dispatch — NOT an app login. |

> Tenant **login** accounts: a `Tenant` entity is a contact record, not automatically an app user. To exercise the tenant SHELL you need a tenant *user* account. Check whether the portal issues a tenant invite/credential (PortalController). If no invite UI exists in this build, create the tenant login via the seeded admin's Admin Users API (`AdminUsersController` creates EmailConfirmed=true users) OR set the tenant's portal credential directly. Record the actual mechanism you used in the run log.

---

## 2. Super‑admin gating — how it works + how to enable for this run

**Mechanism (no super‑admin role exists):** platform access is gated by a config email allowlist, fail‑closed.
- API policy: `RentalCommand.Api/Auth/PlatformAdminPolicy.cs:45` — `IsPlatformAdmin` passes iff the JWT `email` claim is in the allowlist; empty allowlist = nobody (`:47`). Applied via `[Authorize(Policy = PlatformAdminPolicy.Name)]` on `AdminEngineStatusController.cs:21`.
- Web shell guard: `web/src/routes/(superadmin)/+layout.server.ts:20` — non‑allowlisted authenticated user gets **404** (the shell's existence is hidden); unauth → `/login`.
- Allowlist source (web): `web/src/lib/server/platform-admin.ts:20` reads `PLATFORM_ADMIN_EMAILS` (comma‑separated, case‑insensitive) from `$env/dynamic/private`.
- Allowlist source (API): config section `PlatformAdmin:Emails` (`PlatformAdminOptions.SectionName = "PlatformAdmin"`); in containers bound as `PlatformAdmin__Emails__0` (`deploy/docker-compose.prod.yml:142`).

**To make `admin@rentalcommand.local` a platform admin for this run** (do this BEFORE `start-dev.sh`, since the allowlist is captured at startup, `PlatformAdminPolicy.cs:60`):
```bash
# Web (SvelteKit) — export before the web dev server starts:
export PLATFORM_ADMIN_EMAILS="admin@rentalcommand.local"
# API — user-secret so the policy registers it at boot:
dotnet user-secrets set "PlatformAdmin:Emails:0" "admin@rentalcommand.local" --project RentalCommand.Api
```

`[ ] S1. Super admin — WEB — goto /superadmin/engine as `verify-landlord@rc.local` → expected: HTTP 404 (not on allowlist). VERIFY: page shows "Not found", NOT engine health.`
`[ ] S2. Super admin — WEB — log in as `admin@rentalcommand.local`, goto /superadmin/engine → expected: Engine Health page lists per‑worker heartbeats (OutboxDispatchWorker, ScanProcessingWorker, RentChargeWorker, LateFeeWorker, etc.) + active LLM provider. VERIFY: rows present and "alive"; API GET /api/v1/admin/engine-status returns 200.`
`[ ] S3. Super admin — DB — confirm engine is heartbeating: `docker exec edi-postgres psql -U postgres -d rentalcommand -tAc "SELECT \"WorkerName\", \"Status\" FROM \"EngineWorkerHeartbeats\";"` (table name may differ — discover with `\dt` if needed) → expected: one row per worker.`

---

# WEB LANE

> Run from a clean DB if possible. The fresh landlord starts with an EMPTY Live‑shaped portfolio (registration does NOT seed demo data — `AuthService.cs:198`). The Sandbox‑vs‑Live fork happens on first login.

## W‑A. Onboarding / setup (fresh signup)

`[ ] W1. Landlord — /register — fill `register-displayname-input`="Verify Landlord", `register-email-input`="verify-landlord@rc.local", `register-password-input`="Rc!Verify2026", `register-confirm-password-input`="Rc!Verify2026"; click `register-submit` → expected: `register-success` ("Check your email"). VERIFY SQL: `SELECT "EmailConfirmed" FROM "AspNetUsers" WHERE "Email"='verify-landlord@rc.local';` → `f` (false; gate set at AuthService.cs:185). Also `SELECT "Name","IsSandbox" FROM "Portfolios" WHERE "ManagementCompanyName"='Verify Landlord';` → a fresh portfolio exists, IsSandbox=`f`.`

`[ ] W2. Landlord — email verify (DEV) — there is NO email transport in dev; the confirm token is returned by the register API AND a confirmation email is enqueued to the outbox. Fastest dev path: confirm directly. SQL: `UPDATE "AspNetUsers" SET "EmailConfirmed"=true WHERE "Email"='verify-landlord@rc.local';` → expected: 1 row. (Alternative, exercises the real flow: capture `userId`+`emailConfirmationToken` from the register response — AuthController.cs:129 — and POST /api/v1/auth/confirm-email, or open the link the web /verify-email route handles.) VERIFY: re‑query EmailConfirmed → `t`.`

`[ ] W3. Landlord — /login — fill `login-email-input` + `login-password-input`, click `login-submit` → expected: redirect to `/choose-setup` (first‑login fork). VERIFY: URL is /choose-setup; two cards `choose-sandbox` and `choose-live` visible.`

`[ ] W4. Landlord — /choose-setup — click `choose-live` ("Set up my real portfolio") → expected: POST /api/v1/portfolio/onboarding-choice {mode:"live"} then navigate to `/onboarding`. VERIFY SQL: `SELECT "IsSandbox","SandboxSeededAtUtc" FROM "Portfolios" WHERE "ManagementCompanyName"='Verify Landlord';` → IsSandbox=`f`, SandboxSeededAtUtc=NULL (live, not seeded).`

> NOTE: To ALSO cover Sandbox seeding + Go‑Live wipe, do that as a side check with a throwaway account (register a 2nd user, pick `choose-sandbox`, watch `/setting-up` seed demo data, then `/get-started` → `get-started-go-live` dialog → type "GO LIVE" in `get-started-confirm-input` → `get-started-confirm`; VERIFY IsSandbox flips false and seeded rows are wiped). Do NOT run Go‑Live on the main verify‑landlord account.

`[ ] W5. Landlord — /onboarding — step "Portfolio": fill `onboarding-portfolio-name`="Verify Portfolio", `onboarding-portfolio-company`="Verify LLC", set `onboarding-portfolio-timezone`; advance. Step "Owner": confirm `onboarding-owner-prefilled` hint shows (self‑owner). VERIFY (self‑owner provisioning — `RentalCommand.Api/Services/Domain/SelfOwnerProvisioner.cs:47`, called from `AuthService.cs:252`): SQL `SELECT "Name","IsPrimary" FROM "OwnerEntities" WHERE "PortfolioId"=<pid> AND "IsPrimary"=true;` → one primary owner auto‑created from the signup. Also `SELECT "OwnerEntityId" FROM "AspNetUsers" WHERE "Email"='verify-landlord@rc.local';` → non‑null (user linked to owner).`

`[ ] W6. Landlord — /onboarding — step "Property": fill `onboarding-property-name`="Maple Court", `onboarding-property-address`="100 Maple St", `onboarding-property-city`="Akron", `onboarding-property-state`="OH", `onboarding-property-zip`="44301", set `onboarding-property-type`="MultiFamily". Add 2 units via `onboarding-add-unit`: unit "1" beds 2 baths 1 rent 1200; unit "2" beds 1 baths 1 rent 950. Advance. VERIFY SQL: `SELECT COUNT(*) FROM "Properties" WHERE "Name"='Maple Court';`=1 and `SELECT "UnitNumber","MarketRent" FROM "Units" u JOIN "Properties" p ON u."PropertyId"=p."Id" WHERE p."Name"='Maple Court' ORDER BY "UnitNumber";` → 2 rows.`

`[ ] W7. Landlord — /onboarding — finish via `onboarding-finish-dashboard`. Then goto /get-started → expected: `get-started-page` checklist with task rows `get-started-task-owner|property|tenants|lease|...`. VERIFY: owner + property tasks show complete (checkmark) since W5/W6 satisfied them.`

## W‑B. Scan‑IN front door (WEB half: lease PDF + rent‑check→payment)

> The scan hub `/scan` (`scan-page`) has a doc‑type picker `scan-doc-type` with buttons `scan-doc-type-Lease|Payment|Expense|WorkOrder|Application` and an upload zone `scan-upload`. Drafts land in `ScanDrafts`; confirming creates the target record.

`[ ] W8. Landlord — /scan — click `scan-doc-type-Lease`, upload a sample lease PDF (use a file under `samples/` or `uploads/` if present, else any PDF) via `scan-upload` → expected: a draft row appears in `scans-list` (status "Processing" → "Ready to review"). VERIFY SQL (poll up to ~60s): `SELECT "Id","Status","TargetEntityType" FROM "ScanDrafts" ORDER BY "Id" DESC LIMIT 1;` → TargetEntityType='Lease', Status transitions Pending→Reviewing.`

`[ ] W9. Landlord — /scan/[draftId] — review extracted lease fields; assign property=Maple Court, unit=1, choose/create tenant; click the confirm button → expected: a Lease record is created. VERIFY SQL: `SELECT "Id","LeaseNumber","MonthlyRent","Status" FROM "Leases" ORDER BY "Id" DESC LIMIT 1;` and `SELECT "Status","CreatedEntityType","CreatedEntityId" FROM "ScanDrafts" WHERE "Id"=<draftId>;` → ScanDraft Status='Confirmed', CreatedEntityType='Lease'.`

`[ ] W10. Landlord — /scan — click `scan-doc-type-Payment`, upload a rent‑check image → confirm into a Payment. On /scan/[draftId] pick the lease, set amount + payment_date. VERIFY SQL: `SELECT "Id","Amount","PaymentType","Status","PayerName","CheckNumber" FROM "Payments" ORDER BY "Id" DESC LIMIT 1;` → a Payment row (PaymentType likely 'Rent', Status 'Paid'), with PayerName/CheckNumber populated from the check if extracted. (Receipt→expense scan is covered on MOBILE, step M‑*.)`

## W‑C. Properties & units (direct CRUD beyond onboarding)

`[ ] W11. Landlord — /properties — click `property-create-button`; fill name="Birch Plaza", type select="SingleFamily", addressLine1, city, state, postalCode; Save → expected: row in `properties-list`. VERIFY SQL: `SELECT "Name","Type","Status" FROM "Properties" WHERE "Name"='Birch Plaza';` → 1 row, Status default Active.`
`[ ] W12. Landlord — /properties/[id] — open Birch Plaza, add a unit via `property-add-unit` (unit "A", rent 1500). VERIFY SQL: unit row exists for Birch Plaza. Then occupancy: `SELECT COUNT(*) FROM "Units" u JOIN "Properties" p ON u."PropertyId"=p."Id" WHERE p."Name"='Birch Plaza';`=1.`
`[ ] W13. Landlord — /properties — edit property (`property-edit`) status to UnderMaintenance, then back to Active; filter by `property-type-filter` and `property-status-filter` → expected: filters narrow the grid. VERIFY: grid count matches filter.`

## W‑D. Tenants & leases (manual + lifecycle + template/signature)

`[ ] W14. Landlord — /tenants — click `tenant-create-button`; in `tenant-form` fill first/last/email/phone for Tenant A (alice / tenant-alice@rc.local / +13305550101); `tenant-form-save` → expected: row in `tenants-list`. VERIFY SQL: `SELECT "FirstName","Email","Phone" FROM "Tenants" WHERE "Email"='tenant-alice@rc.local';` → 1 row.`
`[ ] W15. Landlord — /tenants — repeat for Tenant B (bob / tenant-bob@rc.local / +13305550102). VERIFY SQL: 2 tenant rows total for this portfolio.`
`[ ] W16. Landlord — /leases — click `lease-create-button`; in `lease-form` set `lease-property-input`=Maple Court, `lease-unit-input`=2, `lease-tenant-input`=Tenant A; `lease-detail-rent`=950, `lease-detail-deposit`=950, `lease-detail-late-fee`=50, `lease-detail-due-day`=1, start/end dates, `lease-detail-status`=Active; `lease-form-save` → expected: lease in `leases-list`. VERIFY SQL: `SELECT "LeaseNumber","MonthlyRent","LateFeeAmount","RentDueDay","Status" FROM "Leases" WHERE "TenantId"=(SELECT "Id" FROM "Tenants" WHERE "Email"='tenant-alice@rc.local');` → Status='Active', LateFeeAmount=50, RentDueDay=1.`
`[ ] W17. Landlord — /leases/[id] — open the lease, Ledger tab (`lease-tab-ledger`): confirm `lease-ledger-charged`/`lease-ledger-paid`/`lease-ledger-balance`. Set an opening balance via `lease-opening-balance-set` (direction "Tenant owed", amount 100). VERIFY SQL: `SELECT * FROM "Payments" WHERE "LeaseId"=<lid>;` shows the opening‑balance charge; ledger balance reflects it.`
`[ ] W18. Landlord — /leases/[id] — lifecycle: if Draft use `lease-set-active` (confirm `lease-set-active-confirm`); then `lease-give-notice` → set `lease-move-out-date`, confirm `lease-give-notice-confirm` → expected: status NoticeGiven. VERIFY SQL: `SELECT "Status","MoveOutDate" FROM "Leases" WHERE "Id"=<lid>;` → Status='NoticeGiven', MoveOutDate set. (Re‑activate afterward if needed for later steps: edit status back to Active.)`
`[ ] W19. Landlord — /leases/[id] — Agreement tab (`lease-tab-agreement`): click `lease-generate-document` ("Generate lease agreement (PDF)") → expected: PDF generated (LeaseService.GenerateDocumentAsync, `RentalCommand.Api/Services/Domain/LeaseService.cs:402`). Click `lease-download-document` → PDF downloads. VERIFY SQL: `SELECT "EntityType","ContentType" FROM "StoredFiles" WHERE "EntityType"='Lease' AND "EntityId"=<lid>;` → a application/pdf row.`
`[ ] W20. Landlord — /leases/[id] — click `lease-send-for-signature` ("Send for signature") → expected: either signature request created (LeaseEsignService.SendForSignatureAsync) OR `lease-esign-not-configured` note if no e‑sign provider wired in dev. VERIFY: if configured, SQL `SELECT "EsignStatus","Status" FROM "Leases" WHERE "Id"=<lid>;` → EsignStatus='Sent', Status='PendingSignature'. If not configured, record "esign not configured in dev" in run log (HTTP 503 expected).`

## W‑E. Applications (apply‑link OUT + approve/convert)

`[ ] W21. Landlord — /applications — click `application-get-link-button` → expected: dialog with `application-link-url` (the public /apply/<token> link). Copy it. VERIFY SQL: `SELECT "PublicApplicationToken" FROM "Portfolios" WHERE "ManagementCompanyName"='Verify LLC';` → token non‑null and matches the URL.`
`[ ] W22. Applicant (public, no login) — /apply/<token> — in a fresh playwright session: `apply-page` loads; select `apply-property-select`=Maple Court, `apply-unit-select`=1; fill `apply-firstName-input`="Carla", `apply-lastName-input`="Cruz", `apply-email-input`="carla@example.com", `apply-phone-input`, DOB, address parts, `apply-employer-input`, `apply-monthlyIncome-input`=4200; CHECK the FCRA consent box `apply-consent-checkbox` (label cites FCRA + consumer reports); click `apply-submit` → expected: `apply-success`. VERIFY SQL: `SELECT "FirstName","ConsentGiven","ConsentAtUtc","Status" FROM "RentalApplications" WHERE "Email"='carla@example.com';` → ConsentGiven=`t`, ConsentAtUtc non‑null, Status='Submitted'.`
`[ ] W23. Landlord — /applications/[id] — open Carla's app; (optional) `application-run-screening` if a screening provider is wired; then `application-approve` → confirm `application-approve-confirm` → expected: a Tenant is created and `application-tenant-banner` shows. VERIFY SQL: `SELECT "Status","ApprovedTenantId" FROM "RentalApplications" WHERE "Email"='carla@example.com';` → Status='Approved', ApprovedTenantId non‑null; and a matching `Tenants` row exists.`
`[ ] W24. Landlord — /applications/[id] — (separate app or new submission) test `application-decline` with `application-decline-reason-input`; if screening exists, `application-adverse-action-open` → generate adverse‑action notice PDF. VERIFY SQL: `SELECT "Status","DecisionReason" FROM "RentalApplications" WHERE "Id"=<id>;` → Status='Declined'; AdverseActionNotices row if generated.`

## W‑F. Payments & accounting

`[ ] W25. Landlord — /accounting — create a manual Payment: open the payment form, set `accounting-payment-lease-input`=Tenant A's lease, `accounting-payment-amount-input`=950, `accounting-payment-due-date-input`, `accounting-payment-type-input`=Rent, `accounting-payment-status-input`=Paid; save. VERIFY SQL: `SELECT "Amount","PaymentType","Status" FROM "Payments" WHERE "LeaseId"=<lid> AND "Status"='Paid' ORDER BY "Id" DESC LIMIT 1;` → 950/Rent/Paid.`
`[ ] W26. Landlord — /accounting — create a manual Expense: `accounting-expense-category-input`=Repairs, `accounting-expense-description-input`="Faucet parts", `accounting-expense-amount-input`=75, `accounting-expense-incurred-date-input`, `accounting-expense-property-input`=Maple Court, status; save. VERIFY SQL: `SELECT "Category","Amount","Status" FROM "Expenses" WHERE "Description"='Faucet parts';` → Repairs/75.`
`[ ] W27. Landlord — /accounting — Reports/ledger: open the ledger tab; export Schedule‑E CSV (GET /api/v1/accounting/schedule-e/export) → expected: CSV downloads. VERIFY: CSV contains the Repairs expense; categories are ScheduleECategory enum (Advertising, Repairs, Utilities, …).`
`[ ] W28. Landlord — /accounting/past-due — expected: any overdue leases listed, each linking to /leases/{id}. VERIFY SQL matches the page (see Data‑Integrity §).`
`[ ] W29. Landlord — /owners-report (+ /tax) — open owner report; expected: per‑owner net for the year resolves. VERIFY: numbers reconcile with §Data‑Integrity ledger totals.`

## W‑G. Work orders / maintenance — FULL lifecycle (landlord side)

> Status enum `WorkOrderStatus`: New, Scheduled, InProgress, WaitingParts, Completed, Cancelled, OnHold, Archived. Priority: Low, Normal, High, Emergency. Provider contact mechanism is BOTH (a) native links on the WO detail and (b) an in‑app dispatch text — see findings below.

`[ ] W30. Landlord — /vendors — click `vendor-create-button`; in `vendor-form` set `vendor-name-input`="Vic's Plumbing", `vendor-service-input`="Plumbing", `vendor-email-input`="vendor-vic@rc.local", `vendor-phone-input`="+13305550147"; `vendor-form-save`. VERIFY SQL: `SELECT "Name","ServiceType","Phone" FROM "Vendors" WHERE "Name"='Vic''s Plumbing';` → 1 row.`
`[ ] W31. Landlord — /maintenance — click `work-order-create-button`; in `work-order-form` set `work-order-property-input`=Maple Court, `work-order-title-input`="Kitchen sink leak", `work-order-description-input`, `work-order-priority-input`=High, `work-order-tenant-input`=Tenant A, `work-order-estimated-cost-input`=120; `work-order-form-save` → expected: WO created Status=New. VERIFY SQL: `SELECT "Id","Title","Status","Priority","EstimatedCost" FROM "WorkOrders" WHERE "Title"='Kitchen sink leak';` → New/High/120.`
`[ ] W32. Landlord — /maintenance/[id] — triage: `work-order-set-scheduled`, then `work-order-set-inprogress`. Assign vendor: edit (`work-order-edit`) set `work-order-detail-... vendor`=Vic's Plumbing, save. VERIFY SQL: `SELECT "Status","VendorId" FROM "WorkOrders" WHERE "Id"=<wid>;` → InProgress, VendorId set.`
`[ ] W33. Landlord — /maintenance/[id] — provider CONTACT: confirm `work-order-vendor-contact` block shows `work-order-vendor-call` (href tel:), `work-order-vendor-text` (href sms:), `work-order-vendor-email` (mailto:). Then in‑app DISPATCH: click `work-order-dispatch` ("Text a vendor") → dialog `work-order-dispatch-dialog`, pick `work-order-dispatch-vendor-<vendorId>`, fill `work-order-dispatch-note`, confirm `work-order-dispatch-confirm` → expected: VendorDispatch created + SMS enqueued. VERIFY SQL: `SELECT "WorkOrderId","VendorId","DispatchedAt" FROM "VendorDispatches" ORDER BY "Id" DESC LIMIT 1;` → row for this WO/vendor. (Endpoint POST /api/v1/work-orders/{id}/dispatch — `WorkOrderController.cs:113`.)`
`[ ] W34. Landlord — /maintenance/[id] — complete: `work-order-set-completed`; edit `work-order-detail-actual-cost-input`=110 and `work-order-detail-completed-input`=today; save. VERIFY SQL: `SELECT "Status","ActualCost","CompletedAt" FROM "WorkOrders" WHERE "Id"=<wid>;` → Completed/110/today. Also `SELECT * FROM "WorkOrderStatusEvents" WHERE "WorkOrderId"=<wid> ORDER BY "Id";` → append‑only timeline has New→…→Completed.`
`[ ] W35. Landlord — pay the provider + receipt — /accounting create Expense linked to the WO: category=Repairs, amount=110, `accounting-expense-vendor-input`=Vic's Plumbing, set property=Maple Court, status=Paid; (link to the work order — the Expense entity carries `WorkOrderId`). VERIFY SQL: `SELECT "Amount","VendorId","WorkOrderId","Status","PaidAt" FROM "Expenses" WHERE "WorkOrderId"=<wid>;` → 110, VendorId + WorkOrderId set, Status=Paid. (Expenses.WorkOrderId / VendorId FKs confirmed in entity; create endpoint POST /api/v1/expenses — `ExpenseController.cs:47`.)`

## W‑H. Messaging (landlord → tenant)

`[ ] W36. Landlord — /messages — click `conversation-new-button`; pick `conversation-compose-tenant`=Tenant A; fill `conversation-compose-subject`="Welcome", `conversation-compose-body`="Hi Alice, welcome to Maple Court."; channels `conversation-channel-portal` (+ email/sms optional); `conversation-compose-send` → expected: conversation created. VERIFY SQL: `SELECT "Id","Subject","TenantId","StartedByLandlord" FROM "Conversations" ORDER BY "Id" DESC LIMIT 1;` → StartedByLandlord=`t`; and `SELECT "SenderRole","Body" FROM "ConversationMessages" WHERE "ConversationId"=<cid>;` → one Landlord message. (Send endpoint + live broadcast: `ConversationService.cs:120/175` broadcasts EntityUpdated EntityType="Conversation" on hub /api/v1/hubs/updates.)`
`[ ] W37. Landlord — /messages — after the tenant replies (MOBILE step M‑*), confirm the reply appears live (SignalR) without reload, and `conversation-unread-badge` updates. DEPENDS: tenant reply (MOBILE). VERIFY SQL: `SELECT COUNT(*) FROM "ConversationMessages" WHERE "ConversationId"=<cid>;` ≥ 2 with a Tenant‑role row.`

## W‑I. Notices & automation

### Manual notices
`[ ] W38. Landlord — /notices — click `generate-notices` ("Generate drafts") → expected: NoticeDraft rows appear (types RenewalOffer / LateRentNotice / MoveOutReminder). For one draft `notice-draft-<id>`: optionally edit `notice-edit-subject`/`notice-edit-body`, run `fair-housing-check`, then "Approve & send" (channels portal/email/sms). VERIFY SQL: `SELECT "NoticeType","Status","ApprovedChannels" FROM "NoticeDrafts" ORDER BY "Id" DESC;` → at least one Status='Approved' with ApprovedChannels set. (Endpoints: generate `NoticeDraftsController.cs:38`; approve `:59`.)`
`[ ] W39. Landlord — /tenants/[id] — alternative manual path: open Tenant A, `tenant-detail-create-notice`; force a type via `tenant-notice-force-RenewalOffer`; send via `tenant-notice-send-<draftId>`. VERIFY SQL: a NoticeDraft for that tenant transitions to Approved; an outbox/notification row is created.`

### Automatic / scheduled notices (CRITICAL — see Cross‑Cutting §)
`[ ] W40. Automation — verify LATE‑FEE auto‑assessment fires. Follow the recipe in §Cross‑Cutting "Scheduled notice verification". VERIFY SQL: a new `Payments` row with PaymentType='LateFee' for the overdue lease+period, the source Rent payment flipped to Status='Late', a `Notifications` row Type='LateFee', and an outbox row.`
`[ ] W41. Automation — verify RENT‑CHARGE generation fires (RentChargeService). VERIFY SQL: a `Payments` row PaymentType='Rent', Status='Scheduled', with a PeriodKey = current 'yyyy-MM' for each active lease within the lead window.`

## W‑J. Dashboard & reports

`[ ] W42. Landlord — / (dashboard) — confirm KPI cards render: Occupancy (`data.occupancy.occupancyRate`), Receivables past due (`data.accounting.overdueAmount`), Net this month (`data.accounting.netThisMonth`), Open work orders (`data.maintenance.openCount`). Money snapshot: `dashboard-money-collected-amount`, `dashboard-money-spent-amount`, `dashboard-money-net-amount`. VERIFY: each clickable KPI navigates (past‑due → /accounting/past-due, work orders → /maintenance). Numbers cross‑checked in §Data‑Integrity. (Dashboard endpoint GET /api/v1/portfolios/{id}/dashboard; aggregates are DB‑side via GroupBy/SumAsync — `DashboardService.cs:71,108,142,166,189`.)`
`[ ] W43. Landlord — /analytics + /reports — open the reports catalog (GET /api/v1/reports/catalog); open rent‑roll, delinquency, occupancy, cash‑flow → expected: each report loads with data. VERIFY: occupancy report unit counts match §Data‑Integrity.`

## W‑K. Settings

`[ ] W44. Landlord — /settings (Notifications tab) — set `settings-notification-email-input`="verify-landlord@rc.local", `settings-notification-email-save`. Toggle channel matrix `settings-channel-LateFee-email`/`-inapp`/`-sms` etc.; `settings-notification-channels-save`. VERIFY SQL: `SELECT * FROM "NotificationSettings" WHERE "PortfolioId"=<pid>;` and `SELECT "NotificationType","EnableInApp","EnableEmail","EnableSms" FROM "NotificationPreferences" WHERE "PortfolioId"=<pid>;` → reflect the toggles.`
`[ ] W45. Landlord — /settings (Automations tab) — enable Rent Charges + Late Fees; set `settings-rent-lead-days`=5, `settings-late-grace-days`=5, `settings-lease-reminder-days`=60; `settings-notification-delivery-save`. VERIFY SQL: `SELECT "EnableRentCharges","EnableLateFees","LateFeeGraceDays","RentChargeLeadDays" FROM "NotificationSettings" WHERE "PortfolioId"=<pid>;` → flags true, days set. (These gate the Engine workers — required for W40/W41.)`
`[ ] W46. Landlord — /settings (Messaging/SMS tab) — set `settings-sms-provider`=SignalWire (or available), `settings-sms-from`="+13302933081", credentials `settings-sms-cred-a/b/c`; `settings-sms-provider-save`; send test via `settings-sms-test-button` (`settings-sms-test-number`) → `settings-sms-test-result`. VERIFY: result shows success or a clear provider error; SQL `smsProvider`/`smsCredentialASet` reflect saved state.`

---

# MOBILE LANE  (serialized — ONE agent on emulator‑5554)

> The Flutter app serves a landlord shell AND a tenant shell (role from JWT: `isTenant = roles has 'Tenant' && not staff`). Drive by visible text. Launch: `$ADB shell am start -n com.rental.command/.MainActivity` (verify package via `$ADB shell pm list packages | grep rental`).

## M‑A. Landlord mobile sanity + scan‑IN (receipt→expense)

`[ ] M1. Landlord — Login — on the login screen tap "Email", type verify-landlord@rc.local; tap "Password", type Rc!Verify2026; tap "Sign In" → expected: lands on the landlord home shell with bottom tabs "Today / Money / Work / Messages". VERIFY: screencap shows those four tab labels.`
`[ ] M2. Landlord — Capture FAB — tap the center FAB ("Scan / Add") → sheet shows tiles "Scan", "Gallery", "PDF / file", "Tell me", "Type it", "Scan a lease", "Scan an application".`
`[ ] M3. Landlord — Scan receipt → expense — tap "Scan" (or "Gallery"/"PDF / file"), provide a receipt image → app uploads and opens the Scan Review screen with extracted Vendor/Amounts/Details groups. Edit category, confirm ("Confirm"/"Save") → expected: Expense created. VERIFY SQL: `SELECT "Category","Amount","DocumentKind" FROM "Expenses" ORDER BY "Id" DESC LIMIT 1;` → a new expense from the receipt scan (ReceiptData JSON populated).`
`[ ] M4. Landlord — Browse menu — tap the "Browse" grid icon in the Today app bar → confirm groups Rentals (Properties, Tenants, Leases, Applications), Work (Vendors, Notices, Inspections), Money (Payments, Expenses), Admin (Settings). Tap "Properties" → list shows Maple Court + Birch Plaza (created on web; same backend). VERIFY: properties visible (cross‑surface data parity).`
`[ ] M5. Landlord — Mobile dashboard KPIs — on "Today", confirm money snapshot + open work orders count match the web dashboard (same /portfolios/{id}/dashboard). VERIFY: open WO count = 0 after W34 completed the only WO (or matches current open count).`

## M‑B. Scan‑IN (rental application) on mobile

`[ ] M6. Landlord — Capture FAB → "Scan an application" — provide an application doc (photos or PDF) → uploads as Application draft → Scan Review with applicant fields (first/last/email/DOB/income). Confirm → expected: RentalApplication created. VERIFY SQL: `SELECT "FirstName","Status" FROM "RentalApplications" ORDER BY "Id" DESC LIMIT 1;` → new applicant row (status Submitted/UnderReview). (This is the scan‑IN counterpart to the web apply‑link OUT in W21‑W23.)`

## M‑C. Tenant shell / portal — end to end

> Prerequisite: a tenant LOGIN for Tenant A. If the build has no in‑app invite, create it via the seeded admin (AdminUsers create → EmailConfirmed=true, role Tenant, linked to the Tenant entity) OR set the portal credential. Record the method used.

`[ ] M7. Tenant A — Login — sign in as tenant-alice@rc.local → expected: TENANT shell with bottom tabs "Home / Messages / Maintenance / More" (NOT the landlord tabs). VERIFY: screencap shows tenant tabs; no Capture FAB.`
`[ ] M8. Tenant A — Home — confirm cards: "Next rent due" (amount/date from the active lease), "Overdue" (if any), "Account history", "Open maintenance". VERIFY: next‑rent amount = lease MonthlyRent (950).`
`[ ] M9. Tenant A — More → "Lease" — view own lease terms → expected: shows Maple Court unit 2, rent 950, dates. VERIFY: matches `Leases` row for Tenant A.`
`[ ] M10. Tenant A — Pay rent — on Home, tap "Pay now" for an unpaid charge → expected: opens hosted Stripe Checkout in browser (dev: may be unconfigured — record "Stripe not configured" if so). Tenant autopay "Set up" toggles available. VERIFY: if Stripe wired, a PaymentTransaction/checkout session is created; else note as device/config‑dependent.`
`[ ] M11. Tenant A — Maintenance — tap the "Maintenance" tab → fill "Issue title"="Bathroom fan noisy", "Description", "Priority"=Normal; optionally "Take photo"; tap "Submit Request" → expected: tenant‑submitted work order created. VERIFY SQL: `SELECT "Id","Title","Status","TenantId","PropertyId" FROM "WorkOrders" WHERE "Title"='Bathroom fan noisy';` → Status='New', TenantId = Tenant A, PropertyId from the tenant's lease. (Endpoint POST /api/v1/portal/tenant/work-orders — `PortalController.cs:260`.) This is the START of the work‑order lifecycle that the LANDLORD then triages on web (re‑run W31‑W35's triage steps against THIS WO to prove the cross‑surface loop).`
`[ ] M12. Tenant A — Messages — open the "Welcome" conversation from W36; type a reply in the message field and send → expected: reply delivered to landlord. VERIFY SQL: `SELECT "SenderRole","Body" FROM "ConversationMessages" WHERE "ConversationId"=<cid> ORDER BY "Id";` → a new row SenderRole='Tenant'. This satisfies W37 (landlord sees it live).`
`[ ] M13. Tenant A — Notifications — tap the notifications card/bell → expected: in‑app notifications list (e.g., the landlord's message, any late‑fee notice). VERIFY SQL: `SELECT "Type","Title","IsRead" FROM "Notifications" WHERE "UserId"=(SELECT "Id" FROM "AspNetUsers" WHERE "Email"='tenant-alice@rc.local') ORDER BY "Id" DESC;` → rows present; marking one read flips IsRead. (FCM PUSH delivery is device‑credential‑dependent — note as such; in‑app arrival is the verifiable signal.)`

---

## Cross‑Cutting / Automation — scheduled notice verification (CRITICAL)

**How scheduled/automatic notices are triggered (verified from code):**
- The **Engine** (`RentalCommand.Engine`) runs each automation as a `BackgroundService` polling on a fixed interval (`EngineWorkerBase.cs`): **LateFeeWorker every 6h** (`LateFeeWorker.cs:15`), **RentChargeWorker every 1h** (`RentChargeWorker.cs:14`), LeaseExpiryReminderWorker 6h, NoticeDraftWorker (Lifecycle Autopilot) 12h, RecurringMaintenanceWorker 24h, DailyBriefingDeliveryWorker 1h, OutboxDispatchWorker 10s, ScanProcessingWorker 2s.
- There is **NO manual HTTP trigger endpoint** for these workers. Each worker runs `ExecuteCycleAsync` **once immediately on startup**, then sleeps for its interval. So the reliable dev verification is: **seed the precondition, then (re)start the Engine and observe the first cycle** — no real‑time wait needed.
- Each automation is gated by per‑portfolio `NotificationSettings` (`EnableLateFees` / `EnableRentCharges`, grace/lead days). The OutboxDispatchWorker then delivers email/SMS; in‑app `Notifications` rows + SignalR broadcast happen inside the service transaction.

**Recipe A — Late‑fee auto‑assessment (LateFeeService.AssessAsync, `Engine/Services/LateFeeService.cs:63`):**
Preconditions the query requires (`:74‑84`): a Payment with PaymentType='Rent', a non‑null PeriodKey, Status in (Scheduled|Late|Partial), and DueDate strictly before "today" minus the portfolio's grace days; the lease must have LateFeeAmount>0; portfolio `EnableLateFees`=true; no existing LateFee for that (LeaseId, PeriodKey).
```bash
PID=<verify portfolio id>; LID=<Tenant A lease id>
# 1. Ensure automation on (or do it in Settings W45):
docker exec edi-postgres psql -U postgres -d rentalcommand -c "UPDATE \"NotificationSettings\" SET \"EnableLateFees\"=true, \"LateFeeGraceDays\"=5 WHERE \"PortfolioId\"=$PID;"
# 2. Ensure the lease charges a late fee:
docker exec edi-postgres psql -U postgres -d rentalcommand -c "UPDATE \"Leases\" SET \"LateFeeAmount\"=50 WHERE \"Id\"=$LID;"
# 3. Seed an OVERDUE rent charge (DueDate well in the past, with a PeriodKey):
docker exec edi-postgres psql -U postgres -d rentalcommand -c "INSERT INTO \"Payments\" (\"PortfolioId\",\"LeaseId\",\"PaymentType\",\"Status\",\"Amount\",\"DueDate\",\"PeriodKey\",\"CreatedAt\",\"UpdatedAt\") VALUES ($PID,$LID,'Rent','Scheduled',950,(now() - interval '40 days'),to_char(now() - interval '40 days','YYYY-MM'),now(),now());"
# 4. Restart ONLY the Engine process (Ctrl-C the engine in start-dev, or kill its PID and re-run RentalCommand.Engine). It assesses on the first cycle.
```
VERIFY (after the Engine's first LateFeeWorker cycle; watch `/tmp/rentalcommand-engine.log` for "Late fee ... assessed"):
```sql
-- a LateFee payment was created for that period:
SELECT "PaymentType","Amount","Status","PeriodKey" FROM "Payments" WHERE "LeaseId"=<LID> AND "PaymentType"='LateFee';
-- the source rent flipped to Late:
SELECT "Status" FROM "Payments" WHERE "LeaseId"=<LID> AND "PaymentType"='Rent' AND "PeriodKey"=<period>;   -- 'Late'
-- tenant in-app notification:
SELECT "Type","Title" FROM "Notifications" WHERE "RelatedEntityType"='Payment' AND "Type"='LateFee' ORDER BY "Id" DESC LIMIT 1;
-- outbox row enqueued (table name: discover with \dt *utbox*; typically "OutboxMessages"):
SELECT COUNT(*) FROM "OutboxMessages" WHERE "CreatedAt" > now() - interval '5 minutes';
```
Idempotency check: restart the Engine again → NO second LateFee row for the same (lease, period) (`LateFeeService` checks the existing‑fee set + DB unique index on (LeaseId, PaymentType, PeriodKey)).

**Recipe B — Rent‑charge generation (RentChargeService.GenerateAsync, `Engine/Services/RentChargeService.cs:53`):** ensure an Active lease with MonthlyRent>0, `EnableRentCharges`=true, and "today" within `[dueDate − RentChargeLeadDays … dueDate]` for the current month (set `RentDueDay` to today's day‑of‑month, or widen `RentChargeLeadDays`). Restart Engine; VERIFY a `Payments` row PaymentType='Rent', Status='Scheduled', PeriodKey=current 'yyyy-MM'. Idempotent per (lease, period).

---

## Data‑Integrity Checks (aggregates must match — DB‑side rule)

The dashboard/accounting numbers are computed DB‑side (`DashboardService.cs` GroupBy/SumAsync). Prove the UI matches the DB for the verify portfolio (`PID`).

**Occupancy** (units with an Active lease ÷ total units):
```sql
-- total units in portfolio:
SELECT COUNT(*) FROM "Units" u JOIN "Properties" p ON u."PropertyId"=p."Id"
  WHERE p."PortfolioId"=<PID> AND p."DeletedAt" IS NULL AND u."DeletedAt" IS NULL;
-- occupied units (distinct units on an active lease):
SELECT COUNT(DISTINCT l."UnitId") FROM "Leases" l
  WHERE l."PortfolioId"=<PID> AND l."Status"='Active' AND l."DeletedAt" IS NULL;
-- occupancyRate = occupied/total*100 must equal the dashboard Occupancy card.
```

**Receivables past due** (overdue amount on the dashboard / /accounting/past-due):
```sql
SELECT COALESCE(SUM("Amount"),0) FROM "Payments"
  WHERE "PortfolioId"=<PID> AND "Status" IN ('Scheduled','Late','Partial') AND "DueDate" < now();
-- must equal data.accounting.overdueAmount.
```

**Net this month** (paid rent − expenses this month):
```sql
SELECT
 (SELECT COALESCE(SUM("Amount"),0) FROM "Payments"
    WHERE "PortfolioId"=<PID> AND "Status"='Paid'
      AND date_trunc('month',COALESCE("PaidDate","DueDate"))=date_trunc('month',now()))
 -
 (SELECT COALESCE(SUM("Amount"),0) FROM "Expenses"
    WHERE "PortfolioId"=<PID>
      AND date_trunc('month',"IncurredAt")=date_trunc('month',now())) AS net_this_month;
-- compare to dashboard-money-net-amount (allow for the service's exact paid/incurred date semantics; if off, read DashboardService.cs:108-148 to mirror its date field choice).
```

**Open work orders** (dashboard Open WO count):
```sql
SELECT COUNT(*) FROM "WorkOrders"
  WHERE "PortfolioId"=<PID> AND "Status" NOT IN ('Completed','Cancelled','Archived') AND "DeletedAt" IS NULL;
-- must equal data.maintenance.openCount.
```

**Lease ledger balance** (per‑lease charged − paid on /leases/[id] ledger):
```sql
SELECT
 (SELECT COALESCE(SUM("Amount"),0) FROM "Payments" WHERE "LeaseId"=<LID> AND "PaymentType" IN ('Rent','LateFee','Utility','Other','SecurityDeposit')) AS charged,
 (SELECT COALESCE(SUM("Amount"),0) FROM "Payments" WHERE "LeaseId"=<LID> AND "Status"='Paid') AS paid;
-- balance shown in lease-ledger-balance should reconcile (read the ledger service for exact charge/credit semantics if it diverges).
```

> If any UI number disagrees with these queries, that is a FINDING — re‑read the relevant service (`DashboardService.cs`, the accounting summary, the lease ledger) to confirm the intended aggregation, and log the discrepancy.

---

## Coverage map (quick index)

| Flow | WEB steps | MOBILE steps |
|---|---|---|
| Super‑admin gate | S1–S3 | — |
| Onboarding/setup + self‑owner | W1–W7 | M1 |
| Scan‑IN | W8–W10 (lease PDF, rent check) | M3 (receipt→expense), M6 (application) |
| Properties & units | W11–W13 | M4 |
| Tenants & leases + template/signature | W14–W20 | M9 |
| Applications (OUT + approve + adverse) | W21–W24 | M6 (scan‑IN) |
| Payments & accounting + reports/CSV | W25–W29 | M3 |
| Work orders full lifecycle | W30–W35 | M11 (tenant submits → landlord triages) |
| Messaging both ways | W36–W37 | M12 |
| Notices + automation | W38–W41 + Cross‑Cutting | — |
| Notifications | (bell on dashboard) | M13 |
| Dashboard & reports | W42–W43 | M5 |
| Settings | W44–W46 | — |
| Tenant portal | (web /portal optional) | M7–M13 |

---

## Run log template (fill during execution)
- Verify portfolio id (`PID`): ____  · Tenant A lease id (`LID`): ____  · primary WO id: ____  · conversation id: ____
- Tenant login creation method used: ____
- E‑sign configured in dev? ____  · Stripe configured? ____  · SMS provider configured? ____
- Findings / mismatches: ____
