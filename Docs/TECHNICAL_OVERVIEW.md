# Rental Command — Technical Overview

*A developer-facing map of the whole system: architecture, data model, the flagship scan
pipeline, the background Engine, realtime, messaging, AI, auth, dev/deploy, and the hard-won
gotchas. Companion to `HOW_RENTAL_COMMAND_WORKS.md` (the plain-language guide).*

---

## 1. What it is

Rental Command is a residential property-management platform for a **non-technical landlord**
(~15–40 units, runs the business from a phone), then co-owners/employees, then a potential SaaS
for other small landlords. Design rule: **simple on the surface, full management system
underneath.** The flagship is *"the computer does the typing for you"* — scan/capture a document
→ an LLM extracts fields with per-field confidence → the user confirms a **draft** → a real
record is created.

It mirrors the sister project **EdiPlatform**: .NET 10 multi-project on PostgreSQL, ASP.NET
Identity + JWT/rotated-refresh auth, light messaging (DB outbox + Engine workers + SignalR — **no
RabbitMQ**), a SvelteKit web app, and a Flutter mobile app over the same API.

## 2. Stack at a glance

| Layer | Tech |
|---|---|
| API | .NET 10 / ASP.NET Core, EF Core + Npgsql, ASP.NET Identity, SignalR |
| Engine | .NET 10 Generic Host (background workers, no HTTP port) |
| Database | PostgreSQL only (Npgsql). No SQLite/SQL Server in production |
| Web | SvelteKit 5 (runes) + TanStack Query + Tailwind v4 + shadcn-svelte + `@microsoft/signalr` + Zod |
| Mobile | Flutter, Riverpod 3, Dio, go_router, `signalr_netcore`, `flutter_secure_storage` |
| LLM | OpenAI `gpt-4o` (default) or Anthropic — `ILlmProvider`, config-switchable; no-op when no key |
| Email / SMS | SendGrid (working) / Twilio (gated on A2P) via a DB outbox + routing channel |
| Realtime | SignalR hub, portfolio-scoped groups, entity-update broadcasts |

## 3. System architecture

Three runtime processes share one PostgreSQL database and one upload directory:

```
                ┌──────────────┐        SignalR (wss)        ┌──────────────┐
  Web (Svelte)  │              │◀───────────────────────────│              │
  Mobile (Dart) │  REST /api/  │   EntityUpdated/Deleted     │   API host   │
  ───────────▶  │     v1       │────────────────────────────▶│  (.NET/HTTP) │
                └──────┬───────┘                             └──────┬───────┘
                       │  JWT (Bearer)                                │ EF Core / Npgsql
                       ▼                                              ▼
                 ┌───────────────────────  PostgreSQL  ───────────────────────┐
                 │  domain tables · Identity · ScanDrafts · OutboxMessage ·    │
                 │  Conversations · AtomicAuditLog · advisory lock (single-Engine) │
                 └───────────────────────────┬─────────────────────────────────┘
                                             │  shared upload dir
                                  ┌──────────▼───────────┐
                                  │   Engine host        │  background workers:
                                  │  (.NET, no HTTP)      │  Outbox · Scan · RentCharge ·
                                  └──────────────────────┘  LateFee · LeaseExpiry + watchdog
```

- **Web ↔ API**: SvelteKit calls `/api/v1/*` over mkcert HTTPS with a Bearer JWT (refresh-and-retry
  on 401). Tokens live in **httpOnly cookies first-party to the SvelteKit origin**; SSR validates
  via `GET /auth/me` and guards route groups.
- **API ↔ DB**: EF Core / Npgsql. The API self-migrates on startup and seeds admin/roles/portfolio.
- **Engine ↔ DB**: a *separate* process sharing the same DB and the same **upload directory**
  (`Upload:BasePath` — so API-written scan blobs are readable by the Engine). It owns all
  background side-effects.
- **API → clients realtime**: SignalR `DataUpdateHub` at `/api/v1/hubs/updates` broadcasts
  `EntityUpdated`/`EntityDeleted` to `portfolio-{id}` groups; the web (`invalidate.ts`) and mobile
  (`realtime_providers.dart`) bridges turn those into TanStack/Riverpod cache invalidations.
- **Light messaging (no broker)**: a DB-backed **outbox** (`OutboxMessage`) is enqueued by
  `IMessagePublisher` and drained by the Engine's `OutboxDispatchWorker` with retry/backoff; an
  `IMessagePublisher` seam allows swapping in a real broker later.

## 4. Repository layout

| Path | Role |
|---|---|
| `RentalCommand.Core` | Entities, enums, **all service interfaces**, config option classes. No infra deps. |
| `RentalCommand.Data` | `RentalCommandDbContext : IdentityDbContext<ApplicationUser, IdentityRole<int>, int>` + `Migrations/` |
| `RentalCommand.Api` | Controllers under `/api/v1/*`, auth, SignalR hubs, scan pipeline + LLM providers, per-entity domain services |
| `RentalCommand.Engine` | Background-worker host (Outbox, Scan, RentCharge, LateFee, LeaseExpiry + resilience); references `.Api` to reuse the LLM providers / disk storage |
| `RentalCommand.*Tests`, `IntegrationTests`, `TestCommon` | xUnit suites + scaffolding (Testcontainers Postgres + InMemory) |
| `web/` | SvelteKit web app |
| `mobile/` | Flutter app |
| `mcp/` | Legacy TypeScript MCP server (to be repurposed for the Portfolio Q&A moat) |
| `Docs/` | `superpowers/specs/` (vision), `superpowers/plans/` (per-phase), this overview |

## 5. Data model

All entities in `RentalCommand.Core/Entities/`. Enums serialize as **string names** over HTTP and
SignalR (`JsonStringEnumConverter` registered on both), but are stored in Postgres as **ints**
(`HasConversion<int>()`). Most domain entities carry `PortfolioId`, `CreatedAt`/`UpdatedAt`, and a
nullable `DeletedAt` soft-delete with a global query filter.

- **Tenancy/ownership**: `Portfolio` (the tenant boundary; `TimeZone`, `Currency`, JSON `Settings`),
  `OwnerEntity` (legal owner — Person / LLC / **Trust**), legacy `Owner` (lightweight contact).
- **Real estate**: `Property` → `Unit` (unique `(PropertyId, UnitNumber)`) → `Lease` (joins
  Property+Unit+Tenant; `LeaseStatus`, `RentDueDay`, check constraints `StartDate<EndDate`,
  `RentDueDay 1–31`), `Tenant`.
- **Money**: `Payment` (money in — `PaymentType`/`PaymentStatus`, `ExternalReference` for bank
  dedupe), `Expense` (money out — `Category` = the 14 IRS **Schedule E** lines, `Subtotal`/`TaxAmount`,
  `ReceiptData` jsonb for non-promoted scan details + line items), `SecurityDepositHolding`
  (+ deductions; `SecurityDepositStatus` enum).
- **People/ops**: `Vendor` (`Is1099Eligible`/`W9OnFile`/`TaxId`), `WorkOrder`, `Appointment`,
  `Inspection`.
- **Intake/safety**: `StoredFile` (polymorphic attachment, re-keyed to the created record on
  confirm), `ScanDraft` (the flagship — `Status` string state machine, `ExtractedFields` jsonb of
  `{value, confidence}`, provenance `ModelId`/`TokensUsed`/`CostUsd`), `AtomicAuditLog` (append-only,
  old/new jsonb), `ActivityLog` (UI feed), `OutboxMessage` (sms/email payload + retry).
- **Messaging**: `Conversation` (per-topic thread: `TenantId`, `Subject`, per-side
  `LandlordUnreadCount`/`TenantUnreadCount`, `LastMessageAt`/preview) + `ConversationMessage`
  (`ConversationSenderRole` Landlord/Tenant, `Body`, `Channels` comma-separated).
- **Auth**: `ApplicationUser : IdentityUser<int>` (**int PK**; nullable `PortfolioId`/`OwnerEntityId`/
  `TenantId`), `RefreshToken`, `UserAccount` (domain login row with `UserRole`).
- **Stripe**: `PaymentTransaction`, `StripeWebhookEvent` (idempotent by event id).

## 6. API surface

All under `/api/v1`. Management controllers resolve the selected workspace, property scope, and
capabilities from the server-validated canonical access context. Route/query workspace ids are
never authority. **IDOR guard**: inbound FK references are validated in-scope via
`PortfolioScopeGuards` before assignment, and PostgreSQL RLS provides the database boundary.

Controllers: `Auth`, `Portfolio` (+ `{id}/dashboard`), `Property`, `Unit`, `OwnerEntity`, `Vendor`,
`Tenant`, `Lease`, `Payment` (+ `{id}/mark-paid`), `Expense` (+ `{id}/receipt`), `Accounting`
(`/summary`), `WorkOrder`, `Appointment`, `Inspection`, `Activity`, `Scan` (upload / `{id}` /
list / `{id}/file` / `{id}/confirm` / `{id}/reject`), `Documents`, `Conversations` (+ tenant
`/portal/conversations`), `Portal`, `AdminUsers`, `Ai` (`/briefing`, `/ask`), `Analytics`,
`SecurityDeposits`, `Devices`, `StripeWebhook`. Machine callers require a future canonical
service-principal authority; the removed portfolio-claim API-key scheme is not supported.

## 7. Auth & security

- **ASP.NET Identity with int keys** (matches int-keyed domain entities). Password policy, 5-attempt
  lockout, unique email. Workspace access uses scoped assignments and capability presets; Owner and
  Tenant are relationship-scoped experiences rather than team roles.
- **JWT access tokens** (~15 min) identify the session. Mutable workspace authority is resolved from
  the current access context and access revision rather than embedded role or portfolio claims.
  **Single-use rotated refresh tokens**: a reused/revoked token **revokes the whole token family**
  (theft/replay defense).
- **Web** keeps tokens in app-namespaced httpOnly cookies (`rc_access_token`, `rc_refresh_token`);
  client refresh goes through the same-origin SvelteKit proxy with single-flight dedup.
- **Mobile** stores the refresh token in the OS secure enclave and sends it as a `Cookie` header
  (the API's `/auth/refresh` reads it from that cookie); a single-flight interceptor refreshes on 401.
- **TLS**: real mkcert certs for web + API; Node trusts the API cert via `NODE_EXTRA_CA_CERTS`. TLS
  verification is **never disabled** — the mkcert CA is trusted instead.

## 8. The flagship scan pipeline

State machine: `Pending → Processing → Reviewing → Confirming → Confirmed` (or `Rejected` / `Failed`).

1. **Upload (sync, fast)**: `POST /scans` (multipart) → validate (size/MIME/filename) → `DiskFileStorage`
   writes a Guid-prefixed blob under `Upload:BasePath` → commit a `StoredFile` row (DB-before-disk) →
   create a `ScanDraft` (`Status="Pending"`). The slow LLM call never runs on the request path.
2. **Engine claims it**: `ScanProcessingWorker` atomically claims `Pending → Processing`, reads the
   real ContentType off the `StoredFile`, and calls the LLM.
3. **LLM extraction** (`OpenAiLlmProvider`, gpt-4o): born-digital PDFs → text-layer extraction;
   images → vision (`image_url`); a **forced tool call** returns structured JSON with a sibling
   `_confidence` (0–1) per field. **Grounding context** (the portfolio's vendors/tenants/properties/
   units) is built by the worker and passed so the model normalizes names to your records.
4. **Reviewing**: persist `{name:{value,confidence}}`, provenance (model/tokens/cost), flip to
   `Reviewing`. In-process failure/timeout → terminal `Failed` (visible, retry/reject); on Engine
   restart, drafts stranded in `Processing` are reset to `Pending` (startup recovery, under the
   advisory lock).
5. **Confirm** (`POST /scans/{id}/confirm`): the **entire confirm-and-create runs in one DB
   transaction** — conditional claim (`Reviewing→Confirming`), create the Expense/Payment, re-key
   the `StoredFile`, flip to `Confirmed`, write an `AtomicAuditLog`. Any failure/cancellation **rolls back
   all of it** (no orphaned record, draft restored to `Reviewing`). Rejects `$0`/blank amounts and
   confirms on non-`Reviewing` drafts. Document-kind routing: RentCheck → Payment, else → Expense.

## 9. The Engine

Generic Host, no HTTP port. **Single-instance safety**: at startup it self-migrates, then takes a
PostgreSQL **advisory lock** (key `59484`) on a dedicated non-pooled connection — a newer Engine
**terminates the old holder's backend** and takes over (so a redeploy never gets stuck behind a
zombie). `AdvisoryLockWatcherService` triggers graceful shutdown if a still-newer Engine takes the
lock.

Workers (all on `EngineWorkerBase`: fresh DI scope per cycle, per-cycle `StepTimeout`,
error-isolated): `OutboxDispatchWorker`, `ScanProcessingWorker`, `RentChargeWorker` (hourly),
`LateFeeWorker` / `LeaseExpiryReminderWorker` (6-hourly), `AccountingPullWorker` (15-min;
imports each Connected + PullEnabled accounting connection's deltas into the domain via
`AccountingImportService`, per-connection Postgres advisory lock), `AccountingTokenRefreshWorker`
(5-min; proactively rotates accounting OAuth tokens within ~10 min of expiry via the shared
`AccountingTokenService` — also reused by the import path's refresh-on-401; a dead refresh token
flips the connection to `NeedsReconnect`). The financial workers wrap the
business record + its tenant notification in **one transaction**, and compute period/due-day in the
landlord's **local timezone** (`App:TimeZone`), not UTC.

`WorkerWatchdogService` flags a worker stuck (no heartbeat past a threshold, or `Error` status) and
calls `StopApplication()` after N strikes. **Critical**: `EngineWorkerBase` emits an **idle
keep-alive heartbeat every 2 min** while sleeping between long cycles, so the watchdog only fires on
a *genuine* hang (liveness is decoupled from work cadence). Notifications fan out via
`RoutingNotificationChannel` (SendGrid email working; Twilio SMS gated; logs when unconfigured).

## 10. Realtime

`DataUpdateHub` (`[Authorize]`; websocket transports pass the JWT via `?access_token=`). On connect,
clients join `user-{id}` and `portfolio-{id}` groups. `DataUpdateService.BroadcastEntityUpdateAsync`
sends `EntityUpdated`/`EntityDeleted` (`{EntityType, EntityId, Data, Timestamp}`) to the portfolio
group after each create/update; failures are swallowed so a realtime hiccup never breaks the write.

Gotchas that bit us (now fixed, worth knowing):
- **Web**: the Vite dev proxy must set **`ws: true`** on `/api`, or the SignalR WebSocket can't
  upgrade. The conversation `channels` field is a **comma-separated string**, not an array.
- **Mobile**: the self-signed-cert bypass must be gated on **`!kReleaseMode`**, *not* an `assert()` —
  asserts are stripped in **profile** builds, so the override was never installed and SignalR
  couldn't connect (REST worked because Dio guards its bypass with `!kReleaseMode`).
- **Engine→clients**: the Engine can't reach the API's in-memory hub directly; the API broadcasts on
  its own writes, and clients refetch. (Scan status during processing is also covered by client
  polling.)

## 11. Messaging

Threaded conversations **by topic** (a tenant can have several: Rent, Maintenance, …). Landlord side:
web two-pane messenger + mobile "Messages" tab; sender-aware bubbles; a per-message **channel picker**
(Portal / Email / SMS) pre-filled from per-portfolio **Settings defaults** (stored in
`Portfolio.Settings` JSON), with the inline choice overriding per send. On a landlord send, the
message is stored and fanned out to the chosen channels via the outbox (email `{to,subject,body}`,
sms `{to,message}`). Tenants reply from the **portal** (in-app only). All of it updates live via the
`Conversation` SignalR event mapped on both clients.

## 12. AI

- **Daily Briefing** (`/ai/briefing`): a prioritized plain-English morning summary with
  severity-coded bullets that link to the referenced record. Works rule-based even without an LLM key.
- **Portfolio Q&A** (`/ai/ask`): tool-calling over the live DB (list tenants/properties/recent
  payments/expenses/upcoming events/vendors, financial summary, …); the answer shows which tools it
  consulted + token usage.

## 13. Accounting model

Deliberately simple: **cash-basis, single-entry** (no double-entry GL). Two ledgers — money in
(`Payment`), money out (`Expense`); categories = IRS **Schedule E** lines; scoped by Property *and*
OwnerEntity. Security deposits tracked as a liability. Outputs: accounting summary (collected /
outstanding / overdue / expenses), Schedule E tax view (+ CSV), per-owner statements (+ "Email to
owner"). Explicitly **not** a tax-filing engine.

## 14. Web app (SvelteKit)

Route groups: public auth pages, `(protected)` landlord/staff app, `(admin)`, `(portal)` tenant/owner.
`AppShell` = role-filtered sidebar + collapsible/mobile-drawer nav + portfolio selector; the shared
**DataGrid** renders a desktop table / mobile cards from per-column "roles". Realtime auto-refresh via
SignalR. Money is `Intl.NumberFormat` USD; **dates render in UTC** so a calendar day doesn't drift.
Flagship scan pages, full CRUD + document hub on detail pages, accounting/tax/owner-reports/deposits/
analytics, work orders/appointments/inspections/activity, the two-pane messenger + Settings, the
dashboard + AI page, admin user management, and the role-aware portal. (Tenant online card payments
are stubbed pending Stripe Elements.)

## 15. Mobile app (Flutter)

Bottom tabs: **Home · Scan · Properties · Messages · More** (Payments, Maintenance, Assistant/AI,
Tenants, Leases, Appointments, Insights, Security Deposits, Owner Reports, Team live in **More**).
Dio + auth interceptor (single-flight refresh, secure-enclave refresh token), `signalr_netcore`
realtime, `image_picker` capture. `kApiBaseUrl` is compile-time via `--dart-define=API_BASE_URL=…`.
The dev cert bypass is active in debug **and profile** (gated on `!kReleaseMode`), enforced in
release. Validated on a real Galaxy S22+ over Wi-Fi.

## 16. Local dev & deploy

```bash
./scripts/start-dev.sh
```
Generates mkcert certs, reuses/starts Postgres on `:5432`, runs Engine + API + web. URLs: **web
https://localhost:5667**, **API https://localhost:5666** (http `5665`), **DB `rentalcommand`**.

- DB connection + keys (OpenAI/SendGrid/Twilio/Stripe) come from **.NET User Secrets** in Development
  (shared between Api + Engine via the same `UserSecretsId`). Do **not** export
  `ConnectionStrings__DefaultConnection` (it overrides the secret).
- Seeded dev admin: **`admin@rentalcommand.local` / `Admin123!`**. The login page has a dev-only
  "Fill login" button.
- **LAN / phone**: bind the API to `0.0.0.0` (`API_HTTPS_URL=https://0.0.0.0:5666`) so the phone
  reaches it over Wi-Fi; the web SSR target stays cert-valid `localhost` (`WEB_API_URL`); `WEB_HOST=0.0.0.0`
  serves the UI on the LAN. (These are decoupled in `start-dev.sh` — binding the API to `0.0.0.0`
  must **not** point the web SSR at `0.0.0.0`, whose cert the mkcert cert doesn't cover.)
- **Mobile to device**: use the side-by-side dev flavor so it installs next to the prod app:
  `flutter build apk --profile --flavor dev --dart-define=FLAVOR=dev --dart-define=API_BASE_URL=https://<LAN-IP>:5666/api/v1 --target-platform android-arm64`
  then `adb install -r build/app/outputs/flutter-apk/app-dev-profile.apk`. Rebuild after any
  API-surface change (old builds 404 against renamed endpoints). Prod release builds should pass
  `--flavor prod --dart-define=FLAVOR=prod`.

## 17. Testing

Per project direction: a **few** UI/E2E + targeted unit tests now, broad regression deferred until
stable. Playwright E2E (`web/e2e/`) over seeded data; .NET unit tests use `RentalCommand.TestCommon`
(Testcontainers Postgres + EF InMemory; the new transaction-using services run fine on the relational
provider). Run heavy review **per phase**, not per task.

## 18. Conventions & gotchas

- **Commits**: clear subject + body only — **no `Co-Authored-By` / AI-attribution trailer**.
- **Enums** stay string-on-the-wire (HTTP + SignalR), int-in-DB. Keep new enums working as strings.
- **PostgreSQL only** (Npgsql). One baseline + additive migrations; self-applied on API startup.
- Hard-won gotchas (all fixed): web LAN login (decouple SSR target from API bind); Vite proxy
  `ws: true`; conversation `channels` is a string; mobile cert bypass via `!kReleaseMode` not
  `assert`; Engine watchdog idle keep-alive; scan-confirm transaction + stuck-`Processing` recovery;
  cross-tenant `GetPortfolioId()` must throw, not return 0.

## 19. Roadmap

Phase 0 (re-platform) → 1+2 (upload + scan→draft→confirm, the wedge) → 3 (real AI: Daily Briefing +
Portfolio Q&A moat) → 4 (automation/notifications/lease lifecycle) → 5 money (scan-the-check, Plaid
reconciliation, optional online payments) → 6 applications/screening/e-sign → 7 clean books +
ownership/entity modeling + deposits → 8 inspections + field maintenance → 9 onboarding/migration
(the SaaS gate). Plans in `Docs/superpowers/plans/`; master spec in `Docs/superpowers/specs/`.
