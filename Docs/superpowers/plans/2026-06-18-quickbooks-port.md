# Implementation Plan — Accounting-Integration Backbone (provider-agnostic, pull-first), QuickBooks = provider #1

**Date:** 2026-06-18
**Repo (target):** Rental Command (`RentalCommand.Api` + `RentalCommand.Engine`, SvelteKit `web/`)
**Repo (study / port-from):** EdiPlatform (`/Users/blackcolours/dev/work/EdiPlatform`)
**Notion task:** [Port QuickBooks integration from EdiPlatform](https://app.notion.com/p/37c394b0689d812392aff3dee4eac2ca)
**Status:** Research + plan complete. Ready for sonnet sub-agent execution. **Building starts now; ships post-Father's-Day. Provider-agnostic from day one.**
**Scope discipline:** RESEARCH + PLANNING produced this doc. Phases below WRITE feature code; that is execution, not this doc.

---

## 0. What this builds (locked framing — do NOT relitigate)

A **provider-agnostic accounting-integration backbone** for Rental Command — a competitive must-have for the commercial product — with **QuickBooks Online as the first provider**. Xero, FreshBooks, Wave, etc. are future drop-ins behind the same abstraction.

**The primary value is PULL (accounting → Rental Command), not push.** A landlord who already runs their books in an accounting system *already has their rent payments and expenses there*. The point of connecting is so that money flows **into** Rental Command — so they don't re-enter everything twice. Push (Rental Command → accounting) is the secondary capability the same backbone supports; a landlord enables whichever direction(s) they need.

**Synergy with SCAN (the existing front door):** scanning a lease builds the rental *skeleton* fast (Property → Unit → Tenant → Lease). The accounting pull then **fills the money onto that skeleton** — historical and ongoing payments/expenses matched to the right lease/property/vendor. Skeleton from scan + money from accounting = a fully populated portfolio with minimal manual entry.

### Why this is a near-port, not a from-scratch build
EdiPlatform **already built this as a pluggable architecture**: `IErpProvider` (the provider contract) + an `ErpProvider` enum + a generic per-tenant `ErpConnection` entity + an `ErpProviderResolver`, with `QuickBooksErpProvider` as **one implementation** and `NetSuiteErpProvider` as a second. We port that abstraction wholesale into Rental Command as `IAccountingProvider` + an `AccountingProvider` enum + a per-portfolio `AccountingConnection` entity, and bring `QuickBooksErpProvider` across as provider #1.

**The one thing EdiPlatform did NOT do: it only ever pulled into a read-only *cache* and reconciled payments — it never used pull to *populate the domain*, and its push is the mature path.** So the genuinely-new engineering for Rental Command is:
1. The **pull-into-the-domain** flow: pull the landlord's accounting Customers / Vendors / Accounts+Classes / Payments / Expenses, then **create or link real `Payment` / `Expense` records** in Rental Command (EdiPlatform pulls a read-only cache; we go further and write domain rows).
2. The **entity-mapping layer**: auto-suggest matches (accounting Customer → RC Tenant/Lease; Account/Class → RC Property + Schedule-E category; Vendor → RC Vendor) by name, the **landlord confirms**, and the confirmed mapping drives idempotent create/link. EdiPlatform has the *cache + a normalize-name reconcile* (`ErpUpsertService` + `ErpCustomerMapping` + `ErpPaymentReconService`) — we extend that into a confirm-driven mapping UI over RC's own fuzzy-match engine (which already exists in `BankingService`).

### First-class, testable acceptance criteria

> MANDATORY. A phase is not "done" until its AC holds.

- **AC-1 — Provider-agnostic backbone.** No QuickBooks-specific type is referenced by the connection/OAuth/sync-orchestration layer. Adding a 2nd provider = implement `IAccountingProvider` + add an enum value + register it; zero changes to the backbone, the `AccountingConnection` entity, the workers' orchestration, or the settings shell. (Mirrors EdiPlatform's `ErpProviderResolver` dispatch.)
- **AC-2 — Pull-first, into the domain.** Connecting QBO and running an import creates/links real RC `Payment` and `Expense` rows (not just a read-only cache), matched to the right lease/property/vendor via confirmed mappings.
- **AC-3 — Per-portfolio isolation + RLS.** Every new table is `PortfolioId`-scoped and added to the RLS policy set via the CRITICAL separate RLS migration; cross-portfolio reads/writes are forbidden at the DB layer.
- **AC-4 — Tokens encrypted at rest.** OAuth access+refresh tokens are stored only as `*CipherText` columns via `IDataProtector` (the `BankConnection` pattern); plaintext never hits the DB.
- **AC-5 — Idempotent import (no duplicates).** Re-importing the same accounting transaction never creates a second RC row, and re-pushing the same RC row never creates a second accounting object. Enforced by an external-id mapping ledger (`AccountingSyncMap`) + provider-side duplicate query.
- **AC-6 — Mapping is confirm-driven and transparent.** Auto-suggested entity matches are surfaced to the landlord with their confidence/reason; nothing is silently created from an unconfirmed guess (same "see exactly what's going in" principle as the scan front-door). Unmatched transactions land in a review queue, never dropped.
- **AC-7 — Token auto-refresh + reconnect prompt.** A background worker refreshes access tokens before expiry; on `invalid_grant` it flips the connection to `NeedsReconnect` and the UI prompts a reconnect — never a silent failure.
- **AC-8 — Fail-closed when unconfigured.** With no provider app-creds on the box, that provider shows "not configured — set server secrets" (the Plaid `Configured` gate); nothing throws.

---

## 1. Architecture facts this plan depends on (VERIFIED this session — file:line)

### 1.1 EdiPlatform source — the pluggable abstraction we port FROM

Shipped across `origin/feat/erp-1-schema` … `origin/feat/erp-5-cleanup` + hardening branches (now merged to main; the branch diffs vs main are empty — the code below IS the hardened version).

**The provider abstraction (this is the backbone we lift):**
- `EdiPlatform.Core/Interfaces/Erp/IErpProvider.cs` — the provider contract. Note it is **already split into auth + pull + push**, exactly the generic surface we want:
  - `Provider` discriminator (`:15`); `BuildAuthorizeUrlAsync` / `ExchangeCodeAsync` / `RefreshTokenAsync` / `RevokeAsync` (auth, `:24-51`).
  - **Pull half (the primary direction for RC):** `PullCustomersAsync(connection, since, ct)` (`:58-59`), `PullItemsAsync` (`:61-62`), `PullWarehousesAsync` (`:64-65`), `PullInventoryAsync` (`:67-69`), `PullPaymentsAsync` (`:71-72`). Each returns `ErpPullResult<T>` and pages until exhausted or sets `MoreAvailable`.
  - Push half: `PushOrderAsync` (`:79-82`), `PushInvoiceAsync` (`:90-93`).
- `EdiPlatform.Engine/Services/Erp/ErpProviderResolver.cs` — maps the `ErpProvider` enum → the registered `IErpProvider` (DI `IEnumerable<IErpProvider>` → dictionary by `.Provider`). **This is the whole multi-provider seam — ~25 lines.** Port verbatim as `AccountingProviderResolver`.
- `EdiPlatform.Core/Enums/ErpProvider.cs` — `{ QuickBooks, NetSuite }`. Becomes `AccountingProvider { QuickBooks, /* Xero, FreshBooks … */ }`.

**The generic connection + state + status:**
- `EdiPlatform.Core/Entities/ErpConnection.cs` — one row per tenant per provider; `Provider`, `Status`, `ExternalAccountId` (realmId), `ExternalCompanyName`, encrypted `AccessToken`/`RefreshToken`, `TokenExpiresAt`, `LastError`, `LastSyncAt`, `LastPulledAtJson` (per-resource delta cursors, `:46-47`).
- `EdiPlatform.Core/Entities/OAuthState.cs` — single-use CSRF state row (`StateToken`, `RedirectUri`, `ExpiresAt`, optional `CodeVerifier` for PKCE — QBO doesn't use PKCE, `:32`).
- `EdiPlatform.Core/Enums/ErpConnectionStatus.cs` — `Pending`, `Connected`, `NeedsReconnect`, `Error`, `Disconnected`.

**The connection lifecycle (provider-agnostic):**
- `EdiPlatform.Engine/Services/Erp/ErpConnectionService.cs` — `StartConnectAsync` (state row + provider authorize URL, `:48-127`), `CompleteCallbackAsync` (consume state, exchange code via the resolved provider, persist tokens, flip Connected, then run an initial pull, `:174-269`), `DisconnectAsync` (`:342-367`), `LookupStateAsync` (`:448-460`). Note `RunInitialPullAsync` (`:271-340`) already pulls customers→items→warehouses→payments on connect — the template for "import on connect."

**The pull engine (the primary-direction core we extend):**
- `EdiPlatform.Engine/Workers/ErpPullWorker.cs` — scheduled (15-min, `:27`), per-connection Postgres advisory lock `hashtext('erp_pull:'||id)` (`:224-239`), **per-resource isolation** (one resource failing doesn't abort the others, `PullOneResourceAsync` `:107-131`), delta cursors parsed from `LastPulledAtJson` (`:200-216`). Port as `AccountingPullWorker`.
- `EdiPlatform.Engine/Services/Erp/ErpUpsertService.cs` — the upsert/cache/promote/reconcile engine. Key reusable ideas:
  - Upsert-by-`ExternalId` into a per-connection cache with `NormalizeName` for fuzzy keys (`UpsertCustomersAsync` `:33-125`, uses `ErpNormalization.NormalizeName`).
  - **Promote cache → domain** (`PromoteItemsToProductCatalogAsync` `:241-414`) — EdiPlatform promotes pulled items into the product catalog. This is the precedent for "pull → create real RC `Payment`/`Expense`."
  - **Auto-link heuristic** (`:282-292`): when a connection has exactly ONE confirmed customer mapping, auto-link; otherwise require explicit mapping. We reuse this "auto when unambiguous, else confirm" rule.
  - Per-run + per-record audit (`ErpSyncRun` + `ErpSyncActivity`, `AddActivity` `:661-696`) and delta-cursor write-back (`UpdateCursor` `:650-659`).
- `EdiPlatform.Engine/Services/Erp/ErpPaymentReconService.cs` (referenced by `RecordPaymentsRunAsync` `:605-635`) — reconciles pulled payments into platform Payment rows. The closest existing analog to "pulled accounting payment → RC Payment."

**The mapping entity (confirm-driven):**
- `EdiPlatform.Core/Entities/ErpCustomerMapping.cs` — manual 1:1 `TradingPartner → ErpCustomer`, set by the user in a wizard, `ConfirmedAt` + `ConfirmedBy` for audit; pushes/links for unmapped entities are blocked individually while mapped ones proceed. **This is the template for `AccountingEntityMapping`** (Tenant/Lease ↔ accounting Customer, Property ↔ Class/Account, Vendor ↔ accounting Vendor).

**The pull DTOs + envelope:**
- `ErpPullResult<T>` (`EdiPlatform.Core/Models/Erp/ErpPullResult.cs`) — `Items` + `MaxLastUpdatedAt` (high-water mark) + `MoreAvailable`.
- `ErpCustomerDto` (`ExternalId`, `DisplayName`, `IsActive`, `ErpUpdatedAt`, `MetadataJson` jsonb for addresses/email/phone) — `ErpCustomerDto.cs`.
- `ErpPaymentDto` (`ExternalPaymentId`, `CustomerExternalId`, `Amount`, `PaymentDate`, `PaymentMethod`, `ReferenceNumber`, `InvoiceExternalIds`) — `ErpPaymentDto.cs`.

**The QuickBooks provider (provider #1 we bring across):**
- `EdiPlatform.Engine/Services/Erp/QuickBooksErpProvider.cs` (752 lines):
  - OAuth endpoints/scope/bases as constants (`:29-39`): authorize `appcenter.intuit.com/connect/oauth2`, token `oauth.platform.intuit.com/oauth2/v1/tokens/bearer`, revoke `developer.api.intuit.com/v2/oauth2/tokens/revoke`, scope `com.intuit.quickbooks.accounting`; API base `quickbooks.api.intuit.com` vs sandbox `sandbox-quickbooks.api.intuit.com`; `minorversion=70`.
  - `BuildAuthorizeUrlAsync` (`:67-82`, no PKCE), `ExchangeCodeAsync` (`:84-129`, Basic-auth `base64(clientId:clientSecret)`, **requires `realmId`** from the callback), `RefreshTokenAsync` (`:131-170`, **both tokens rotate**), `RevokeAsync` (`:172-209`, best-effort).
  - **PULL implementations to bring across (the v1 priority):** `PullCustomersAsync` (`:231-283`, QBO `SELECT * FROM Customer` with `MetaData.LastUpdatedTime` delta + STARTPOSITION/MAXRESULTS paging), `PullPaymentsAsync` (`:363-442`, parses `TotalAmt`, `TxnDate`, `CustomerRef`, linked-invoice ids). We add `PullVendorsAsync` + accounts/classes + a Purchase/Bill pull (QBO `Vendor`, `Account`, `Class`, `Purchase`, `Bill` — same `QueryAsync` shape).
  - `QueryAsync` (`:722-742`) — the QBO query endpoint; `ToQuickBooksDocNumber` (`:637-652`, 21-char cap + SHA hash); `ExtractQuickBooksFault`/`BuildQuickBooksHttpError` (`:654-707`).
  - Push: `PushInvoiceAsync` (`:444-487`) + idempotency `CheckForDuplicateAsync` (`SELECT Id FROM Invoice WHERE DocNumber=...`, `:533-554`).
  - Credentials (`:211-229`): platform creds from a settings store + `IConfiguration` fallback → **we replace with a config POCO** (no `SystemSettings` table in RC).

**Token-refresh + push workers (port near-verbatim):**
- `EdiPlatform.Engine/Workers/ErpTokenRefreshWorker.cs` — 5-min poll, 10-min horizon, per-connection advisory lock, `invalid_grant` → `NeedsReconnect`.
- `EdiPlatform.Engine/Workers/ErpOrderPushWorker.cs` — `FOR UPDATE SKIP LOCKED` batch, exponential backoff to 12 attempts, per-attempt `ErpSyncRun`/`ErpSyncActivity`.
- Unit test worth porting: `QuickBooksErpProviderTests.cs` (`ToQuickBooksDocNumber`).

### 1.2 Rental Command target — patterns we MUST follow

(Load-bearing facts, VERIFIED this session.)

- **.NET 10, EF Core 10 + Npgsql/Postgres, nullable ON.** Projects `RentalCommand.{Api,Core,Data,Engine}` + `web/` (SvelteKit 5 / Svelte 5 runes). Tenant key = **`PortfolioId`** (the `CustomerId` analog); entities implement `IPortfolioScoped` + `IAuditable`.

- **External-connection precedent = `BankConnection` (Plaid) — copy THIS, not EdiPlatform's DataProtection-in-DbContext:**
  - Entity `RentalCommand.Core/Entities/BankConnection.cs` — `PortfolioId`, `Provider` string, **`*CipherText` columns**, `Status` string, `LastSyncedAt`.
  - Service `RentalCommand.Api/Services/Domain/BankingService.cs` — `IDataProtectionProvider.CreateProtector("RentalCommand.Banking.v1")` (`:27`) + `ProtectNullable`/`UnprotectNullable` (`:644-660`). **The OAuth token-exchange analog is `ExchangePlaidPublicTokenAsync` (`:106-156`)**: call external API → `ProtectNullable(tokens)` onto the connection → `SaveChanges`.
  - **The fuzzy-match engine for the mapping layer ALREADY EXISTS here:** `SuggestMatch` (`:715-768`), `ScorePayment`/`ScoreExpense` (`:774-803`), `CombineScore` (date proximity + name, `:811-832`), `NameMatchStrength` (token containment + shared-token fraction with a banking stop-word list, `:839-902`), and the load-candidates-once-per-batch shape `MapTransactionsWithSuggestionsAsync` (`:692-713`). **Reuse this for accounting-Customer→Tenant, Vendor→Vendor, Account/Class→Property matching** — same problem (match an external string to an internal entity by name + amount + date).
  - External REST client shape: `RentalCommand.Api/Services/Domain/PlaidBankingProvider.cs` — injected `HttpClient`, `record` settings with a `Configured` gate (`:13-21`), typed request/response, `AddHttpClient<IPlaidBankingProvider, PlaidBankingProvider>`. **This is the shape of `QuickBooksAccountingProvider`'s HTTP client.**
  - EF config `RentalCommand.Data/RentalCommandDbContext.cs:283-298` — `*CipherText` `HasMaxLength(4000)`; portfolio query filter `HasQueryFilter(e => e.Portfolio!.DeletedAt == null)` (`:1342`).

- **Config POCO + gate:** `RentalCommand.Core/Configuration/StripeConfig.cs` (`SectionName` const + nullable secret props + `Enabled => !IsNullOrWhiteSpace(SecretKey)`); bound in `RentalCommand.Api/Program.cs:57-58`. `PlaidOptions` is identical. Our per-provider config POCOs follow this — **REPLACES EdiPlatform's `SystemSettings` credential lookup.**

- **DI / Program.cs:** `AddDbContext<RentalCommandDbContext>` (scoped, NOT `AddDbContextFactory`) WITH `AuditSaveChangesInterceptor` + `RlsConnectionInterceptor` (`:141-147`). Services take `RentalCommandDbContext` directly (like `BankingService`), **unlike EdiPlatform's `IDbContextFactory`** — when porting workers, swap factory-create for the scoped context the cycle already gets (`EngineWorkerBase` gives a fresh scope per cycle). `AddHttpClient<TI,TImpl>(c => …)` idiom at `:68,97,107`. `AddHostedService<T>()` at `:284`. Config bindings cluster `:46-63`.

- **Controllers:** MVC `[ApiController]`, route `api/v1/<resource>`, derive from `ManagementControllerBase` (roles `Admin,Manager,Agent,Owner`) → `AuthenticatedPortfolioControllerBase` exposing `GetPortfolioId()`/`GetUserId()` from JWT claims (`AuthenticatedPortfolioControllerBase.cs:30-66`). `BankingController.cs` (`Admin,Manager`) is the sibling. **No per-controller rate-limiting attribute in this app** (drop EdiPlatform's `[EnableRateLimiting]`).

- **RLS (CRITICAL):** `RlsConnectionInterceptor` sets `app.current_portfolio_id`/`app.is_admin` GUCs per connection. Policies live in migration `20260615155023_AddRlsTenantIsolation…cs` with explicit table arrays (`DirectPortfolioTables` `:35-46`, idempotent helpers `EnableForce`/`CreatePolicy` `:190-209`). The migration header says it was "modeled on EdiPlatform's per-CustomerId RLS" — **the port is the intended lineage.** New portfolio-scoped tables get RLS via a NEW migration calling those helpers.

- **Background workers:** `RentalCommand.Engine/Workers/EngineWorkerBase.cs` — `BackgroundService`, fresh DI scope per cycle, `PollInterval`+`StepTimeout`, heartbeat to `EngineStatusReporter`, single-instance via a startup advisory lock in Engine `Program.cs`. Existing financial workers (`RentChargeWorker`, `LateFeeWorker`, `AutopayChargeWorker`) are the pattern; register `AddHostedService<T>()` in Engine `Program.cs`.

- **DTOs:** one file per feature under `RentalCommand.Api/DTOs/` (e.g. `BankingDtos.cs`), POCOs with `= string.Empty;`/`= [];` defaults.

- **Migrations:** `dotnet ef migrations add <Name> --project RentalCommand.Data --startup-project RentalCommand.Api`; API+Engine auto-migrate on startup behind an advisory lock. Latest: `20260616231236_AddPaymentAmountPaid`.

- **Web settings:** `web/src/routes/(protected)/settings/+page.svelte` is a tabbed hub; sub-pages at `settings/<name>/+page.svelte` (+ `+page.server.ts`). Server actions post via `serverPost(endpoint, locals.accessToken!, body)`, loads via `serverGet` (`web/src/lib/api/server-fetch.ts:100-114`). **Gotcha (documented in `settings/security/+page.server.ts:1-9`):** never set an explicit `Authorization` header on `event.fetch` — `handleFetch` injects it; use `serverPost`/`serverGet` (direct API fetch, token once) or you get a malformed `Bearer X, Bearer Y` → silent 401.

### 1.3 RC domain entities we read/write (the pull targets)

- `Payment` (`Payment.cs`): `PortfolioId`, `LeaseId`, `PaymentType` (`Rent`/`SecurityDeposit`/`LateFee`/`Utility`/`Other`), `Status` (`Scheduled`/`Paid`/`Partial`/`Late`/`Waived`/`Failed`/`Refunded`), `Amount`, `AmountPaid`, `DueDate`, `PaidDate`, `Method`, `ExternalReference`, `PayerName`. Lease→Tenant/Property via `Payment.Lease.Tenant`/`.Property`. **No external-id column → mapping ledger needed.** (`PeriodKey` is the existing idempotency precedent for auto-generated rows.)
- `Expense` (`Expense.cs`): `PortfolioId`, `PropertyId?`, `VendorId?`, `Category` (`ScheduleECategory`, 14 IRS values), `Amount`, `IncurredAt`, `PaidAt?`, `Status` (`Pending`/`Approved`/`Paid`/`Rejected`/`Draft`), `Subtotal?`, `TaxAmount?`, `Description`, `LineItems`. Soft-deleted via `DeletedAt`.
- `Tenant` (`FirstName`/`LastName`/`Email`/`Phone`), `Vendor` (`Name`/`Email`/`Phone`/`TaxId`/`Is1099Eligible`/`W9OnFile`), `Property` (`Name`/address/`OwnerEntityId?`/`ManagementFeePercent?`), `Lease` (`LeaseNumber`/`MonthlyRent`/`TenantId`/`PropertyId`/`UnitId`).
- `ScheduleECategory`: `Advertising`, `AutoTravel`, `CleaningMaintenance`, `Commissions`, `Insurance`, `LegalProfessional`, `ManagementFees`, `MortgageInterest`, `Repairs`, `Supplies`, `Taxes`, `Utilities`, `Depreciation`, `Other`.

---

## 2. FROZEN CONTRACTS (define in Phase 1; everything imports these)

### 2.1 `IAccountingProvider` (the provider-agnostic backbone seam — Core)

Direction-agnostic: every provider can pull and/or push; the orchestration layer never sees a provider-specific type.

```csharp
// RentalCommand.Core/Interfaces/Accounting/IAccountingProvider.cs
public interface IAccountingProvider
{
    AccountingProvider Provider { get; }
    AccountingCapabilities Capabilities { get; }      // CanPull/CanPush per resource — drives the UI + orchestration

    // OAuth (PKCE optional; QBO=false)
    string BuildAuthorizeUrl(AccountingAppSettings s, string redirectUri, string state, string? codeChallenge);
    Task<AccountingTokenResult> ExchangeCodeAsync(AccountingAppSettings s, AccountingCallback cb, CancellationToken ct);
    Task<AccountingTokenResult> RefreshTokenAsync(AccountingAppSettings s, string refreshToken, CancellationToken ct);
    Task RevokeAsync(AccountingAppSettings s, string refreshToken, CancellationToken ct);

    // PULL (primary direction) — generic DTOs, paged, delta via `since`
    Task<AccountingPullResult<ExtCustomerDto>> PullCustomersAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct);
    Task<AccountingPullResult<ExtVendorDto>>   PullVendorsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct);
    Task<AccountingPullResult<ExtAccountDto>>  PullAccountsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct); // accounts + classes
    Task<AccountingPullResult<ExtPaymentDto>>  PullPaymentsAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct); // money-in
    Task<AccountingPullResult<ExtExpenseDto>>  PullExpensesAsync(AcctCallCtx ctx, DateTime? since, CancellationToken ct); // money-out (Purchase/Bill)

    // PUSH (secondary) — caller passes a stable external key; provider is idempotent
    Task<AcctPushResult> UpsertIncomeAsync(AcctCallCtx ctx, AcctIncomeDoc doc, CancellationToken ct);   // SalesReceipt/Invoice
    Task<AcctPushResult> UpsertExpenseAsync(AcctCallCtx ctx, AcctExpenseDoc doc, CancellationToken ct); // Purchase/Bill
}
public sealed record AcctCallCtx(string Realm, string AccessToken, bool UseSandbox);
public sealed record AccountingTokenResult(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc, string? ExternalAccountId, string? CompanyName);
public sealed record AccountingPullResult<T>(IReadOnlyList<T> Items, DateTime? MaxUpdatedAtUtc, bool MoreAvailable); // = ErpPullResult<T>
public enum AcctPushOutcome { Created, AlreadyExisted, Updated }
```

`AccountingProviderResolver` (port of `ErpProviderResolver`) maps the enum → impl from `IEnumerable<IAccountingProvider>`. **A 2nd provider = new class implementing this + an enum value + a `Configure<>`/`AddHttpClient<>` line. Nothing else changes (AC-1).**

### 2.2 `AccountingProvider` enum + per-provider config POCO

```csharp
// RentalCommand.Core/Enums/AccountingProvider.cs
public enum AccountingProvider { QuickBooks /*, Xero, FreshBooks, Wave */ }

// RentalCommand.Core/Configuration/QuickBooksOptions.cs  (mirrors StripeConfig/PlaidOptions)
public class QuickBooksOptions {
    public const string SectionName = "QuickBooks";
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string Environment { get; set; } = "sandbox"; // "sandbox" | "production"
    public string? RedirectUri { get; set; }             // must byte-match the Intuit app config
    public bool Configured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    public bool UseSandbox => !string.Equals(Environment, "production", StringComparison.OrdinalIgnoreCase);
}
```
`AccountingAppSettings` is the provider-neutral struct the connection service hands to a provider (resolved from that provider's options POCO), so the backbone doesn't read `QuickBooksOptions` directly.

### 2.3 Import (pull → domain) contract

Per pulled transaction, the import service produces one of: **AutoLinked** (unambiguous mapping existed → RC row created/linked), **NeedsReview** (suggested mapping(s), awaiting landlord confirm), or **Unmatched** (no candidate → review queue). The `AccountingSyncMap` ledger keys every imported transaction by `(PortfolioId, AccountingConnectionId, Direction, ExternalType, ExternalId)` → the RC row it created/linked, so re-import is idempotent (AC-5). Mapping decisions live in `AccountingEntityMapping` (port of `ErpCustomerMapping`), confirm-driven (AC-6).

### 2.4 Web API surface (provider-agnostic; `{provider}` route param)

- `POST /api/v1/integrations/accounting/{provider}/connect` → `{ authorizeUrl }`
- `GET  /api/v1/integrations/accounting/callback?code&state&realmId&error` → 302 → `/settings#accounting`
- `POST /api/v1/integrations/accounting/{provider}/disconnect` → 204
- `GET  /api/v1/integrations/accounting/status` → `[{ provider, configured, status, companyName, connectedAt, lastSyncAt, lastError, pullEnabled, pushEnabled, pendingReviewCount, importedCount, capabilities }]`
- `POST /api/v1/integrations/accounting/{provider}/import` → `{ fromDate, toDate }` → runs/queues a pull
- `POST /api/v1/integrations/accounting/{provider}/direction` → `{ pullEnabled, pushEnabled }`
- `GET  /api/v1/integrations/accounting/{provider}/mappings` → suggested + confirmed entity mappings
- `POST /api/v1/integrations/accounting/{provider}/mappings/confirm` → `{ localType, localId, externalType, externalId }`
- `GET  /api/v1/integrations/accounting/{provider}/review-queue` → unmatched/needs-review imported transactions
- (push side) `POST /api/v1/integrations/accounting/{provider}/push` → `{ fromDate, toDate }` (secondary)

---

## 3. Entity-mapping layer (the genuinely-new core)

### 3.1 Direction: accounting → Rental Command (PULL, primary)

| Accounting object (QBO) | matches → RC entity | match signal | on confirm |
|---|---|---|---|
| **Customer** | **Tenant** (and thereby its active **Lease**) | name (tenant first+last) via `NameMatchStrength`; optional email | future imported **payments** for that customer link to that lease |
| **Vendor** | **Vendor** | name via `NameMatchStrength`; optional `TaxId` | future imported **expenses** for that vendor link to that vendor |
| **Account** / **Class** | **Property** (Class) and/or **Schedule-E category** (Account) | name; the §3.3 account-name map seeds category | imported expenses get `PropertyId` (from Class) + `Category` (from Account) |
| **Payment** (money-in) | create/link **`Payment`** (`Paid`, `PaidDate=TxnDate`, `Amount`, `PaymentType=Rent`, `ExternalReference`) | via the Customer→Tenant→Lease mapping | idempotent by `AccountingSyncMap` |
| **Purchase / Bill** (money-out) | create/link **`Expense`** (`Paid`, `PaidAt=TxnDate`, `Amount`, `Category` from account map, `PropertyId`/`VendorId` from mappings) | via Vendor + Account/Class mappings | idempotent by `AccountingSyncMap` |

**Auto vs confirm rule (ported from `ErpUpsertService:282-292`):** if exactly one RC entity is an unambiguous match (the only lease for a matched tenant, or a name-containment match with confidence ≥ 0.8 and no rival within 0.15), auto-link; otherwise surface for confirm. Unmatched → review queue (AC-6). **Reuse `BankingService`'s `SuggestMatch`/`CombineScore`/`NameMatchStrength` verbatim** for the scoring.

**Skeleton synergy:** if no Tenant/Property/Vendor matches a pulled Customer/Class/Vendor, the review-queue item offers "create it" (a thin create) — but the happy path is that SCAN already built the skeleton, so most pulls auto-link.

### 3.2 Direction: Rental Command → accounting (PUSH, secondary)

| RC event | accounting object | when | idempotency |
|---|---|---|---|
| `Payment` → `Paid`/`Partial` | Sales Receipt (D-6) | landlord enabled push | `DocNumber="RCP-{id}"` + `AccountingSyncMap` + provider duplicate-query |
| `Expense` → `Paid` | Purchase (D-7) | landlord enabled push | `DocNumber/PrivateNote="EXP-{id}"` + ledger + duplicate-query |
| `Tenant`/`Vendor`/`Property` | Customer/Vendor/Class (`Ensure*`) | resolved before the doc | name query |

### 3.3 Schedule-E category ↔ QBO account name (bidirectional seed map; user-overridable — D-4)

`Advertising`→`Advertising` · `AutoTravel`→`Auto and Travel` · `CleaningMaintenance`→`Cleaning and Maintenance` · `Commissions`→`Commissions` · `Insurance`→`Insurance` · `LegalProfessional`→`Legal and Professional Fees` · `ManagementFees`→`Management Fees` · `MortgageInterest`→`Mortgage Interest` · `Repairs`→`Repairs` · `Supplies`→`Supplies` · `Taxes`→`Taxes` · `Utilities`→`Utilities` · `Depreciation`→`Depreciation` · `Other`→`Other Rental Expenses`. On **pull**, an unmapped QBO account → `Other` + a review-queue hint to map it; on **push**, missing account → catch-all or prompt (D-4).

### 3.4 Idempotency (AC-5)

Two layers, both from EdiPlatform:
1. **`AccountingSyncMap` ledger** — unique `(PortfolioId, AccountingConnectionId, Direction, ExternalType, ExternalId)` → `LocalEntityType`+`LocalEntityId`+`Status`. Import SKIPs any external txn already mapped `Imported`; push SKIPs any local row already mapped `Pushed`. (Generalizes EdiPlatform's stamp-external-id-on-the-row; RC's `Payment`/`Expense` have no such column so a side ledger is cleaner.)
2. **Provider-side duplicate query** before a push create (`SELECT Id FROM SalesReceipt WHERE DocNumber=...`, port of `CheckForDuplicateAsync`). Survives a lost ledger row (DB restore) without duplicating in QBO. `DocNumber` via `ToQuickBooksDocNumber` (21-char cap).

---

## 4. Data-model changes (entities + migrations)

### 4.1 Enum `AccountingConnectionStatus`
`Pending`, `Connected`, `NeedsReconnect`, `Error`, `Disconnected` (verbatim from `ErpConnectionStatus`).

### 4.2 Entity `AccountingConnection` (generic; the `ErpConnection`/`BankConnection` hybrid)
`RentalCommand.Core/Entities/AccountingConnection.cs`, `IPortfolioScoped, IAuditable`:
```
Id, PortfolioId
Provider (AccountingProvider)
ExternalAccountId   (realmId / company id)
CompanyName?
AccessTokenCipherText?, RefreshTokenCipherText?, TokenExpiresAt?     (IDataProtector; never plaintext)
Status (AccountingConnectionStatus = Pending)
PullEnabled (bool=true), PushEnabled (bool=false)                    (per-direction toggles — landlord enables what they need)
LastPulledAtJson?   (per-resource delta cursors, = ErpConnection.LastPulledAtJson)
LastError?, LastSyncedAt?, ConnectedAt?, DisconnectedAt?
CreatedAt, UpdatedAt, Portfolio? (nav)
```
(One row per portfolio per provider — a portfolio could connect QBO now and Xero later; both rows coexist.)

### 4.3 Entity `OAuthState` (CSRF; port of EdiPlatform's)
`Id, PortfolioId, Provider (AccountingProvider), StateToken, RedirectUri, CodeVerifier? (PKCE-ready, null for QBO), ExpiresAt, CreatedAt`. Unique index on `StateToken`.

### 4.4 Entity `AccountingEntityMapping` (confirm-driven; port of `ErpCustomerMapping`)
`RentalCommand.Core/Entities/AccountingEntityMapping.cs`, `IPortfolioScoped`:
```
Id, PortfolioId, AccountingConnectionId
LocalEntityType ("Tenant"|"Lease"|"Vendor"|"Property"|"ScheduleECategory"), LocalEntityId  (or LocalEnumValue for category)
ExternalType ("Customer"|"Vendor"|"Account"|"Class"), ExternalId, ExternalDisplayName
ConfirmedAt?, ConfirmedByUserId?   (null while only-suggested)
Confidence?                        (the suggester's score when surfaced)
CreatedAt, UpdatedAt
Unique index (PortfolioId, AccountingConnectionId, ExternalType, ExternalId)
```

### 4.5 Entity `AccountingSyncMap` (idempotency + import/push ledger)
`RentalCommand.Core/Entities/AccountingSyncMap.cs`, `IPortfolioScoped`:
```
Id, PortfolioId, AccountingConnectionId
Direction ("Import"|"Push")
ExternalType ("Payment"|"Purchase"|"Bill"|"SalesReceipt"), ExternalId
LocalEntityType ("Payment"|"Expense"), LocalEntityId
Status ("Imported"|"Pushed"|"NeedsReview"|"Unmatched"|"Failed"), AttemptCount, LastError?, LastAttemptAt
CreatedAt, UpdatedAt
Unique index (PortfolioId, AccountingConnectionId, Direction, ExternalType, ExternalId)
```
*(Optional `AccountingSyncActivity` append log — port of `ErpSyncActivity` — deferred to Phase 5 unless the user wants the activity feed up front.)*

### 4.6 EF config (`RentalCommandDbContext.OnModelCreating`)
`*CipherText` `HasMaxLength(4000)`; portfolio query filter on each new entity (`:1342` pattern); the unique indexes; DbSets `AccountingConnections`, `OAuthStates`, `AccountingEntityMappings`, `AccountingSyncMaps`.

### 4.7 Migration A — schema; **Migration B — RLS (CRITICAL, AC-3)**
- A: `dotnet ef migrations add AddAccountingIntegrationBackbone …`.
- B: `AddAccountingRls` — call the idempotent `EnableForce`+`CreatePolicy` helpers (copy from `20260615155023…cs:190-209`, `PortfolioPredicate`) for `AccountingConnections`, `OAuthStates`, `AccountingEntityMappings`, `AccountingSyncMaps`, with a `Down` that DROP POLICY + DISABLE. **Without B the new tables are not RLS-protected and AC-3 fails.** (Check for a meta-test asserting "every `IPortfolioScoped` table has a policy"; if present, B satisfies it.)

---

## 5. Phases

### Phase 1 — The provider-agnostic backbone (no provider logic, no sync) — AC-1, AC-3, AC-4, AC-8
**Goal:** the pluggable substrate exists and is provider/direction-agnostic.

1. `IAccountingProvider` + `AccountingCapabilities` + the generic DTOs/records (§2.1); `AccountingProvider` enum; `AccountingProviderResolver` (port `ErpProviderResolver`).
2. `AccountingAppSettings` neutral struct + a per-provider options resolver (reads the right `*Options` POCO by enum). `QuickBooksOptions` POCO (§2.2) bound in `Program.cs`; empty `"QuickBooks"` appsettings section.
3. Entities §4.1–4.5 + DbSets + EF config §4.6. **Migration A (schema) + Migration B (RLS).**
4. `AccountingConnectionService` (port `ErpConnectionService`, provider-agnostic via the resolver): `StartConnectAsync`, `CompleteCallbackAsync` (exchange via resolved provider, encrypt+persist tokens via `IDataProtector.CreateProtector("RentalCommand.Accounting.v1")` + the `ProtectNullable`/`UnprotectNullable` helpers copied from `BankingService:644-660`), `DisconnectAsync`, `GetStatusAsync`, `LookupStateAsync`, plus direction toggles (`SetPullEnabled`/`SetPushEnabled`).
5. `AccountingIntegrationsController : ManagementControllerBase` (route `api/v1/integrations/accounting`, `{provider}` param), endpoints §2.4 connect/callback/disconnect/status/direction. Callback 302s to `/settings#accounting`.
6. DTOs `AccountingDtos.cs`. (No provider yet, so no new tests this phase.)

**Verify:** `dotnet build` clean; the resolver throws a clean "no provider registered" for an unregistered enum; with no creds, status returns `configured:false` and connect 422s.

**Commit:** `feat(accounting): provider-agnostic integration backbone — connection, OAuth lifecycle, encrypted tokens (Phase 1)`

---

### Phase 2 — QuickBooks provider + PULL/import + mapping layer (the primary direction) — AC-1, AC-2, AC-5, AC-6
**Goal:** connect QBO and pull the landlord's money into RC, confirm-driven, idempotent.

1. `QuickBooksAccountingProvider : IAccountingProvider` — bring across `QuickBooksErpProvider`'s OAuth + `QueryAsync` + `ToQuickBooksDocNumber` + `ExtractQuickBooksFault`, and the **pull** methods (`PullCustomersAsync`/`PullPaymentsAsync` ported; add `PullVendorsAsync` over QBO `Vendor`, `PullAccountsAsync` over `Account`+`Class`, `PullExpensesAsync` over `Purchase`+`Bill`). `Capabilities = { CanPull: all, CanPush: income+expense }`. Register `AddHttpClient<IAccountingProvider, QuickBooksAccountingProvider>(c => c.Timeout = TimeSpan.FromSeconds(60))` (resolver picks it up). Port the `ToQuickBooksDocNumber` unit test.
2. `AccountingImportService` (the genuinely-new engine; informed by `ErpUpsertService`+`ErpPaymentReconService`):
   - For pulled Customers/Vendors/Accounts/Classes: upsert a light per-connection cache, run the **suggester** (reuse `BankingService.NameMatchStrength`/`CombineScore`) against RC Tenants/Vendors/Properties, write `AccountingEntityMapping` rows as **suggested** (auto-confirm only when unambiguous per §3.1).
   - For pulled Payments/Expenses: resolve the entity mappings; if resolved → create/link a real RC `Payment`/`Expense` (`Paid`, cash-basis dates, `ExternalReference`, `Category`/`PropertyId`/`VendorId` from mappings) and write an `AccountingSyncMap` `Imported` row; if not → `NeedsReview`/`Unmatched` ledger row (review queue). **All idempotent via the unique ledger index.**
   - **HARD RULE — DB-side:** dedupe/eligibility via `NOT EXISTS` against `AccountingSyncMaps` in the query; no load-then-loop. The mapping suggester loads candidate Tenants/Vendors/Properties **once per batch** (as `BankingService.MapTransactionsWithSuggestionsAsync:692-713` does) — not per-transaction.
3. `AccountingPullWorker : EngineWorkerBase` (port `ErpPullWorker`): scheduled pull for every `Connected` + `PullEnabled` connection, per-connection advisory lock `hashtext('acct_pull:'||id)`, per-resource isolation, delta cursors in `LastPulledAtJson`. Swap `IDbContextFactory` for the scoped `RentalCommandDbContext` (the worker already gets a fresh scope/cycle). Register in Engine `Program.cs`.
4. Import-on-connect: `AccountingConnectionService.CompleteCallbackAsync` kicks an initial pull (port `RunInitialPullAsync`), failure non-fatal.
5. `POST {provider}/import` (date-range one-time/backfill), `GET {provider}/mappings`, `POST mappings/confirm`, `GET review-queue` endpoints + service methods. Confirming a mapping re-runs the import for that entity's pending ledger rows.

**Verify (sandbox):** connect the Intuit sandbox company; seed it with a customer-payment + a vendor-purchase; run import → a suggested Tenant/Vendor mapping appears; confirm it → an RC `Payment`/`Expense` is created with the right lease/property/vendor + `Paid` + correct date; re-run import → no duplicate (ledger gates it); an unmatchable txn lands in the review queue.

**Commit:** `feat(accounting): QuickBooks provider + pull-into-domain import + confirm-driven entity mapping (Phase 2)`

---

### Phase 3 — Token refresh + reconnect, and the PUSH direction (secondary) — AC-5, AC-7
**Goal:** connections stay alive; landlords who want it can also push RC → accounting.

1. `AccountingTokenRefreshWorker : EngineWorkerBase` — port `ErpTokenRefreshWorker` (5-min poll, 10-min horizon, advisory lock `hashtext('acct_refresh:'||id)`, `invalid_grant` → `NeedsReconnect`). Inline single-refresh-on-401 in the providers' call path.
2. `AccountingPushService` + `AccountingPushWorker` (port `ErpOrderPushWorker`): for `Connected` + `PushEnabled` connections, claim eligible `Paid` `Payment`/`Expense` rows lacking a `Pushed` ledger row (`FOR UPDATE SKIP LOCKED`, DB-side `NOT EXISTS`), resolve Customer/Vendor/Class via `Ensure*`, build the income/expense doc (cash-basis), `Upsert*Async` (provider duplicate-query inside), write `AccountingSyncMap` `Pushed`. Exponential backoff to 12 attempts.
3. `POST {provider}/push` (manual range) + the `PushEnabled` toggle in status/settings.

**Verify:** force-expire a token → refresh rotates both; revoke in sandbox → `NeedsReconnect`. Enable push; mark a Paid payment → a SalesReceipt appears in QBO once; re-run → no duplicate.

**Commit:** `feat(accounting): proactive token refresh + reconnect + RC→accounting push direction (Phase 3)`

---

### Phase 4 — Settings "Connect your accounting" UI (provider-agnostic shell + QBO) — AC-6, AC-8 surfaced
**Goal:** a landlord-facing surface to connect a provider, choose direction(s), review/confirm mappings, and watch imports.

1. New "Accounting" tab/section in `settings/+page.svelte` (or sub-route `settings/accounting/+page.svelte` + `+page.server.ts`), mirroring `settings/security`. **The shell is provider-agnostic** — it renders a card per available provider from `GET status` (`configured`, `status`, `capabilities`); QBO is the only enabled card today, a 2nd provider needs zero UI code (AC-1 at the UI layer).
2. `+page.server.ts`: `load` → `serverGet('/integrations/accounting/status', locals.accessToken!)`; actions `connect`/`disconnect`/`import`/`confirmMapping`/`setDirection` via `serverPost(..., locals.accessToken!, body)`. **`serverPost`/`serverGet` only — never `event.fetch` + `Authorization` (double-inject 401, `settings/security/+page.server.ts:1-9`).** `connect` returns `{ authorizeUrl }` → full-page browser redirect to Intuit.
3. `+page.svelte` (Svelte 5 runes): per-provider connect card; on connect, a direction chooser ("Bring my books into Rental Command" [pull, default-on] / "Send Rental Command into my books" [push, default-off]); a **mapping review panel** (suggested Tenant/Vendor/Property matches with a "from your books" hint + confidence, confirm/adjust — same transparency language as the scan front-door); a **review queue** for unmatched transactions; last-sync + imported/pending-review counts; a "Reconnect" button on `NeedsReconnect`; a "not configured — ask your admin" state when `configured:false`. `data-testid`s on key controls. Use `frontend-design` aesthetics + the Banking/Settings visual language.
4. Callback returns to `/settings#accounting?connected=1|error=…`; page toasts accordingly.

**Verify:** `cd web && npx svelte-check --threshold error` → 0. Walk with playwright-cli on a local stack: connect (sandbox), choose pull, run import, confirm a mapping, see the RC payment appear, see an unmatched item in the queue, disconnect.

**Commit:** `feat(web): provider-agnostic "Connect your accounting" settings + mapping review (Phase 4)`

---

### Phase 5 — Prove the abstraction + polish (post-v1)
- **Add a 2nd provider (Xero or FreshBooks)** as the real test of AC-1: a new `IAccountingProvider` impl + enum value + `Configure<>`/`AddHttpClient<>` + its own dev-portal app — and confirm zero changes to the backbone, workers, ledger, mapping layer, or settings shell. (Xero uses OAuth2 **with PKCE** + a tenant-id header instead of realmId — the `CodeVerifier` column and the neutral `ExternalAccountId` already accommodate this; a good genericity forcing-function.)
- `AccountingSyncActivity` append log + a "recent imports" feed.
- Help/KB doc (port + re-skin `accounting-quickbooks-netsuite.md`).
- Continuous-sync toggle (vs one-time import) if D-1 chooses it.

**Commit:** `feat(accounting): second provider proves the abstraction + sync activity feed (Phase 5)`

---

### Phase 6 — User setup per provider (Intuit Developer Portal + box secrets) — USER task
1. Intuit app at https://developer.intuit.com → scope `com.intuit.quickbooks.accounting`; separate Development (sandbox) + Production keys.
2. Redirect URI (byte-exact) added to the app: local `https://<api>/api/v1/integrations/accounting/callback`; staging/prod `https://<rental-domain>/api/v1/integrations/accounting/callback`. The `redirectUri` RC sends must match or Intuit rejects.
3. Sandbox QBO company is auto-provisioned for end-to-end testing.
4. Box secrets (section `QuickBooks`): `QuickBooks__ClientId`, `QuickBooks__ClientSecret`, `QuickBooks__Environment`, `QuickBooks__RedirectUri`. Dev: `dotnet user-secrets set "QuickBooks:ClientId" "…" --project RentalCommand.Api`.
5. Production review: Intuit gates production keys behind an app review — ship on sandbox first.
6. Each future provider repeats this with its own portal (Xero/FreshBooks).

---

## 6. What ports as-is vs. what is new

**Ports almost verbatim (the backbone + plumbing):**
- The pluggable provider abstraction itself: `IErpProvider`→`IAccountingProvider`, `ErpProvider` enum, `ErpProviderResolver`→`AccountingProviderResolver`, generic `ErpConnection`→`AccountingConnection`, `OAuthState`, status enum, `ErpPullResult<T>`→`AccountingPullResult<T>`.
- Connection lifecycle (`StartConnect`/`CompleteCallback`/`Disconnect`/`LookupState` + initial-pull-on-connect).
- **The pull worker** (`ErpPullWorker`: scheduled, per-connection advisory lock, per-resource isolation, delta cursors) and the upsert/audit/cursor mechanics (`ErpUpsertService`).
- Token-refresh worker, push worker skeleton, Intuit OAuth2 (authorize/exchange/refresh/revoke), `ToQuickBooksDocNumber` + test, fault parsing, the QBO `query` endpoint, duplicate-check idempotency.

**Re-skinned to RC conventions (mechanical):**
- Token storage → `*CipherText` columns + service-layer `IDataProtector` (`BankConnection`/`BankingService`), not EdiPlatform's DbContext value-converter.
- Credentials → per-provider config POCO (`StripeConfig`/`PlaidOptions`), not `SystemSettings`.
- Tenant key `CustomerId`→`PortfolioId`; `IDbContextFactory`→scoped `RentalCommandDbContext`; drop `[EnableRateLimiting]`; controllers on `ManagementControllerBase`.
- RLS via a new migration using the existing idempotent helpers.

**Genuinely new (RC-specific — must be written fresh):**
- **Pull-into-the-domain:** creating/linking real RC `Payment`/`Expense` rows from pulled accounting transactions (EdiPlatform pulls a read-only cache + reconciles; it never populated the domain from pull — this is the headline new work and the product's main value).
- **The confirm-driven entity-mapping layer:** accounting Customer→Tenant/Lease, Vendor→Vendor, Account/Class→Property+Schedule-E, with auto-suggest (reusing `BankingService`'s fuzzy-match engine), landlord confirm, and a review queue for unmatched. (`ErpCustomerMapping` is the seed; the suggester + queue + multi-entity-type breadth are new.)
- Provider pull methods for **Vendors / Accounts / Classes / Purchases / Bills** (EdiPlatform's QBO provider pulls Customers + Payments + Items; we add the rental-relevant resources).
- The Schedule-E ↔ QBO-account bidirectional map.
- The provider-agnostic "Connect your accounting" settings shell + mapping review UI.
- The scan-skeleton ↔ accounting-money synergy (offer "create" from the review queue when no skeleton entity matches).

---

## 7. Product decisions (DECIDED — locked by the user; implemented in Phase 2)

- **D-1 — One-time import vs continuous pull. DECIDED: both.** On-demand date-range backfill import (`POST {provider}/import`) AND the scheduled `AccountingPullWorker` doing ongoing deltas. `PullEnabled` defaults true (continuous-on after connect); the worker only pulls Connected + PullEnabled connections, so toggling pull off makes it manual/backfill-only.
- **D-2 — Direction defaults / is push in v1. DECIDED: pull-only v1.** Pull ON, push OFF by default. The full PULL side ships in v1. The provider's two push methods (`UpsertIncomeAsync`/`UpsertExpenseAsync`) are implemented as real ports (`Capabilities.CanPush* = true`), but the push worker/service/endpoint/UI are deferred to v1.1.
- **D-3 — Unmatched transactions. DECIDED: review queue.** Never dropped, never silently created. Unmatched/needs-review land in `AccountingSyncMap` (`Unmatched`/`NeedsReview`) and surface via `GET {provider}/review-queue`; from there the landlord confirms a mapping or creates the missing Tenant/Vendor/Property.
- **D-4 — Account/category mapping UX. DECIDED: auto-map by name, surface for confirm.** Auto-map QBO Account → Schedule-E category via the §3.3 name table; an unmapped account → `Other` + a review hint. (The one-time confirm UI is Phase 4.)
- **D-5 — Auto-link threshold. DECIDED: unambiguous only.** Auto-link when there is a single viable candidate, OR confidence ≥ 0.8 with no rival within 0.15; otherwise surface as a suggestion for the landlord to confirm. (`AccountingImportService.ResolveBest`.)
- **D-6 — Push income object. DECIDED: Sales Receipt** (cash already collected; one object; avoids double-count vs a bank feed). Implemented in `QuickBooksAccountingProvider.UpsertIncomeAsync`; wired by v1.1.
- **D-7 — Push expense object. DECIDED: Purchase** (we only push Paid). Implemented in `UpsertExpenseAsync`; wired by v1.1.
- **D-8 — Money-in scope on pull. DECIDED: pull all, auto-create only tenant-mapped.** The pull pulls ALL money-in, but auto-creates an RC `Payment` ONLY for a transaction whose Customer maps (confirmed or unambiguously auto-linked) to a known Tenant→active Lease. Everything else (owner contributions, transfers, unmapped) → review queue.
- **D-9 — Security deposits / non-cash. DECIDED.** Import money-in as `PaymentType.SecurityDeposit` when the source account maps to a deposit/liability account; otherwise default tenant-mapped money-in to `Rent`. Skip Depreciation on both directions (non-cash). Deposit detection is deliberately light (default Rent; truly-ambiguous lands in review).
- **D-10 — Callback lands on API. DECIDED: API.** The Intuit callback hits the API controller (owns token exchange) → 302 into `/settings#accounting`. (Shipped in Phase 1.)
- **D-11 — Status enum. DECIDED: enum.** `AccountingConnectionStatus` (matches EdiPlatform's `ErpConnectionStatus`). (Shipped in Phase 1.)
- **D-12 — 2nd provider (Phase 5).** Still open — Xero (OAuth2 + PKCE + tenant header; a good genericity stress test) is the recommended candidate, decided at Phase 5 time. Not needed for v1.

---

## 8. Testing approach (deliberately light — RC norm: don't over-test during churn)

- **Unit (port):** `ToQuickBooksDocNumber`. **Unit (cheap/high-value):** the Schedule-E↔account name map (table-driven); `ProtectNullable`/`UnprotectNullable` round-trip; the auto-link-threshold decision (given candidate scores → AutoLink/NeedsReview/Unmatched).
- **Integration (Phase 2 — the headline path):** against a faked `IAccountingProvider`, prove import (a) creates an RC `Payment` from a pulled payment with a confirmed Customer→Tenant mapping, (b) does NOT duplicate on re-import (ledger), (c) routes an unmatchable txn to the review queue. Mirror EdiPlatform's `FakeErpProviderForPull` + `ErpPullWorkerTests`.
- **Integration (Phase 3):** push idempotency (one SalesReceipt, no dup on re-run) — mirror `FakeErpProviderForPush` + `ErpOrderPushWorkerTests`.
- **Manual / sandbox is the real signal:** the Intuit sandbox company end-to-end (connect → import → confirm mapping → RC rows appear; enable push → objects appear in QBO). Walk the settings UI with playwright-cli.
- **RLS guard:** Migration B keeps any "every `IPortfolioScoped` table has a policy" meta-test green; else a one-line check that the four new tables carry `tenant_isolation` (add only if quick).
- **Pre-merge gates:** backend → `dotnet build` + focused tests 0/0; web → `npx svelte-check --threshold error` 0 errors.

---

## 9. Effort sense (AI-time, not human-time)

| Phase | Scope | Rough AI-time |
|---|---|---|
| 1 | Backbone: `IAccountingProvider`+resolver+enum, 4 entities, config, connection lifecycle, schema+RLS migrations | ~3–4 h |
| 2 | QBO provider (OAuth+pull incl. new resources) + import-into-domain + mapping/suggester + pull worker + review queue + endpoints | ~5–7 h (the bulk — the genuinely-new mapping/import engine) |
| 3 | Token-refresh worker + push service/worker + direction toggles | ~2–3 h |
| 4 | Provider-agnostic settings shell + mapping-review UI + review queue | ~2.5–3.5 h |
| 5 | 2nd provider + activity feed + help (post-v1) | ~3–5 h |
| 6 | Intuit-portal setup (USER) | n/a |

Core (1–4): **~13–18 h** focused sub-agent work. Phase 2 depends on 1; 3 and 4 depend on 1+2; 4 can overlap 3 once the status/mapping endpoints exist. Serialize `dotnet build`/`dotnet test` (one at a time) per the machine's RAM limits.

---

## 10. Execution notes for the sub-agent(s)

- **Provider-agnostic from line one.** The connection service, workers, ledger, mapping layer, and settings shell must never name QuickBooks in a type/branch — they go through `IAccountingProvider`/`AccountingProviderResolver`/`Capabilities`. (This mirrors EdiPlatform's discipline and is what makes provider #2 cheap.) QuickBooks specifics live ONLY in `QuickBooksAccountingProvider` + `QuickBooksOptions`.
- **Near-port:** open the cited EdiPlatform file and mirror it, then re-skin to the RC pattern cited alongside. Don't re-invent OAuth/refresh/pull-orchestration/idempotency — they're solved in `ErpConnectionService`/`ErpPullWorker`/`ErpUpsertService`/`QuickBooksErpProvider` + the workers.
- **The mapping suggester is `BankingService`'s existing engine** (`NameMatchStrength`/`CombineScore`/`SuggestMatch`) — extract/reuse it, don't write a new matcher.
- **HARD RULE — all aggregation/eligibility/dedupe is DB-side** (one SQL / EF-translated, `NOT EXISTS` + `FOR UPDATE SKIP LOCKED`); load mapping candidates ONCE per batch, never per-transaction; no load-then-loop.
- **Resilient backend:** every `HttpClient` has a `Timeout`; thread `CancellationToken` everywhere; never `catch {}`; let `EngineWorkerBase` own cancellation/timeout.
- **Secrets:** never log tokens; `*CipherText` only.
- **SOT/diagrams:** update any RC architecture diagram / master plan enumerating workers/entities/controllers in the same commit (check `Docs/` + meta-tests).
- **Branch:** include the Notion task id if RC's GitHub→Notion sync keys off it.
