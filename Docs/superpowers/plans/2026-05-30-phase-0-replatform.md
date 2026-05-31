# Phase 0 — Re-platform foundation (EdiPlatform parity) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Split the single SQLite api/ project into four .NET projects (Core/Data/Api/Engine) + test projects; migrate Lifecycle→RentalCommand namespace; delete ~18 dead template entities + migrations; convert Minimal APIs into thin controllers + scoped services + DTOs; swap SQLite→PostgreSQL with re-baselined migrations; add soft-delete, enum HasConversion, JSON columns, central PortfolioId scoping from JWT claims, and append-only AuditTrail; introduce OwnerEntity (Person/LLC/Trust) in the data layer; replace custom 14-day token auth with ASP.NET Identity + JWT access + rotated-refresh httpOnly cookies + email verify + password reset + lockout + ApiKeyAuthenticationHandler; migrate existing users/roles (rehash-on-first-login); upgrade frontend with SSR auth + route guards + fetch layer with single-flight refresh + Zod validators + toasts + data-testid; convert SSE→SignalR; stand up TestCommon + Tier-2 tests + Playwright + GitHub Actions CI + multi-stage Dockerfiles + Traefik + Postgres compose; add missing enum states and Portfolio.Currency; add edit/search/filter/pagination to every list.

**Architecture:** Four-project split mirrors EdiPlatform (Core/Data/Api/Engine). RentalCommandDbContext inherits from IdentityDbContext<ApplicationUser, IdentityRole<int>, int>. PostgreSQL with Npgsql, real tenant scoping from JWT `portfolioId` claim + application-layer `.Where()` filtering (no RLS initially). JWT access tokens (15-min) + rotated refresh tokens (7-day, httpOnly, single-use). IMessagePublisher backed by DB-outbox (OutboxMessage + QueuedJob tables); Engine polls with advisory lock for single-instance safety. SignalR replaces SSE for realtime. Frontend uses hooks.server.ts (SSR auth), route groups, fetch layer with Zod, and TanStack Query integration with SignalR invalidation bridge.

**Tech Stack:** .NET 10 (multi-project), PostgreSQL + Npgsql EF Core, ASP.NET Identity, JWT (System.IdentityModel.Tokens.Jwt), SignalR, SvelteKit 5 + Svelte runes, TanStack Query, Zod, Playwright, GitHub Actions, Docker/Traefik, Postgres compose.

**Depends on:** None (foundational phase).

---

## File / project structure

### New projects to create
- **RentalCommand.Core** — Entities, all service interfaces (ILlmProvider, IScanService, IPaymentProvider, IScreeningProvider, IEsignProvider, INotificationChannel, IAuditTrailService, IMessagePublisher, IFileStorage, etc.), enums (with HasConversion config), config option classes, DTOs, value objects.
- **RentalCommand.Data** — RentalCommandDbContext : IdentityDbContext<ApplicationUser, IdentityRole<int>, int>, EF Core Migrations, DbContext configuration (soft-delete, enum converters, JSON columns, constraints), seeding, RLS interceptor (placeholder for later multi-tenant), AppSettings for connection string.
- **RentalCommand.Api** — ASP.NET Core web host, thin controllers, Scoped feature services, DTOs + responses, AuthController + JWT/cookie/API-key auth, SignalR hubs (NotificationHub, DataUpdateHub), middleware, Program.cs wiring.
- **RentalCommand.Engine** — Background service host, EngineWorkerBase + single-instance advisory lock, workers (EngineHealthWorker, OutboxDispatchWorker, skeleton for future rent-posting/scan/etc.), program wiring.
- **RentalCommand.TestCommon** — Tier-2 test infrastructure (Tier2WebAppFactory, Tier2TestBase, Tier2ControllerBase, Tier2InboundPipelineBase), DatabaseFixture (real Postgres via testcontainers or connection string), transaction rollback, fake implementations (ILlmProvider no-op, IPaymentProvider mock, etc.).
- **RentalCommand.Core.Tests, RentalCommand.Data.Tests, RentalCommand.Api.Tests, RentalCommand.Engine.Tests, RentalCommand.IntegrationTests** — xUnit test suites, .runsettings tier filtering, Playwright journeys in `web/tests/e2e/`.

### Existing projects to refactor
- **web/** — Upgrade from SvelteKit 4 to Svelte 5 + runes (if not already); rewrite hooks.server.ts for SSR auth; convert routes to `(auth)` + `(protected)` route groups; replace fetch calls with single-flight-refresh wrapper; add Zod schema validation; wire SignalR invalidation; add toasts, data-testid everywhere, edit/filter/sort/pagination UI to lists.
- **mcp/** — Retarget to new REST contract + authed client library (post-API stabilization; Phase 0 scope is minimal changes).

### Entities to keep
Portfolio, Property, Unit, Lease, Tenant, Payment, Expense, Vendor, WorkOrder, Appointment, Inspection, ActivityLog, UserAccount, PortalMessage, AuthSession (→ RefreshToken).

### Entities to add
- **OwnerEntity** (int Id, enum OwnerEntityType {Person, LLC, Trust}, string Name, string? TaxId, string? Address, string? Phone, int PortfolioId, DateTime CreatedAt, DateTime UpdatedAt) — references from Property, Tenant, Payment (who collected), Expense (billable-to).
- **ApplicationUser : IdentityUser<int>** (int? PortfolioId, int? OwnerEntityId, int? TenantId, string DisplayName, DateTime CreatedAt, DateTime? LastLoginAt) — Identity user with **int** PK (matches all domain entities) and portfolio/owner scoping.
- **RefreshToken** (int Id, int UserId, string Token, string TokenHash, DateTime ExpiresAt, DateTime IssuedAt, bool IsRevoked, bool IsUsed, string? IpAddress, string? UserAgent) — rotated, single-use refresh tokens.
- **AuditLog** (int Id, int PortfolioId, int? UserId, string? ActorLabel, string EntityType, int EntityId, string Operation, string? OldValues, string? NewValues, string? ChangeReason, DateTime Timestamp, string? IpAddress) — append-only audit trail with JSON payload hashing (post-baseline; Phase 0 structure only).
- **StoredFile** (int Id, int PortfolioId, string FileName, string FilePath, string ContentType, long FileSize, string? EntityType, int? EntityId, DateTime UploadedAt, DateTime? DeletedAt) — polymorphic attachment (Phase 1 scope; Phase 0 just entity shape).
- **ScanDraft** (int Id, int PortfolioId, string FilePath, string TargetEntityType, string Status, JSON ExtractedFields {value, confidence, sourceBox per field}, string? ModelId, int? TokensUsed, decimal? CostUsd, DateTime CreatedAt, DateTime? ReviewedAt, string? ReviewedBy, DateTime? ConfirmedAt) — Phase 2 scope; Phase 0 just entity shape.
- **OutboxMessage** (long Id, int PortfolioId, string MessageType, JSON Payload, int RetryCount, DateTime CreatedAt, DateTime? SentAt, DateTime? FailedAt, string? Error) — reliable SMS/email send with retry.
- **QueuedJob** (long Id, int PortfolioId, string JobType, string Status {Pending, Running, Completed, Failed}, JSON Payload, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt, string? Error) — background work tracking.

### Entities to DELETE
ProjectTask, Milestone, Phase, LifecycleTask, TestPlan, TestStep, TestStepResult, TestExecution, Test, AgentSession, AgentEscalation, Comment, Label, TaskLabel, TeamMember, TaskAssignment. + all migrations in LegacyMigrations/.

### Endpoint files to rename/consolidate
- TaskEndpoints.cs → **delete**; functions move to domain-specific files (LeaseEndpoints, PaymentEndpoints, ExpenseEndpoints, WorkOrderEndpoints).
- ProjectEndpoints.cs, MilestoneEndpoints.cs, PhaseEndpoints.cs, TeamEndpoints.cs, LabelEndpoints.cs, CommentEndpoints.cs, TestPlanEndpoints.cs → **delete**.
- AiEndpoints.cs → **keep as-is** (stubs); will be filled in Phase 3 (move to AiController).
- Keep: AuthEndpoints, PortfolioEndpoints, PropertyEndpoints, UnitEndpoints, TenantEndpoints, VendorEndpoints, LeaseEndpoints, PaymentEndpoints, AccountingEndpoints, WorkOrderEndpoints, AppointmentEndpoints, InspectionEndpoints, ActivityEndpoints, PortalEndpoints.
- Convert Minimal-API MapXxxEndpoints() → thin Controllers (e.g., PortfolioController, PropertyController, etc.) + Scoped services (e.g., PortfolioService, PropertyService, etc.).

---

## Tasks

### Task 1: Create solution structure (RentalCommand.sln) and .NET projects

**Files:** Create RentalCommand.sln, RentalCommand.Core/RentalCommand.Core.csproj, RentalCommand.Data/RentalCommand.Data.csproj, RentalCommand.Api/RentalCommand.Api.csproj, RentalCommand.Engine/RentalCommand.Engine.csproj, RentalCommand.TestCommon/RentalCommand.TestCommon.csproj, RentalCommand.*.Tests/*.csproj. Modify Directory.Build.props (enable reference assemblies, skip analyzers on local builds as per EdiPlatform).

**Steps:**
- [ ] Delete Lifecycle.csproj; create RentalCommand.sln in repo root.
- [ ] Create RentalCommand.Core project (ClassLibrary, net10.0, nullable=enable); reference no other projects.
- [ ] Create RentalCommand.Data project (ClassLibrary, net10.0, nullable=enable); reference Core.
- [ ] Create RentalCommand.Api project (ASP.NET Core web, net10.0, nullable=enable); reference Core + Data.
- [ ] Create RentalCommand.Engine project (ClassLibrary, net10.0, nullable=enable, will host BackgroundService); reference Core + Data.
- [ ] Create RentalCommand.TestCommon project (ClassLibrary, net10.0, nullable=enable); reference Core + Data + Api.
- [ ] Create RentalCommand.Core.Tests, RentalCommand.Data.Tests, RentalCommand.Api.Tests, RentalCommand.Engine.Tests, RentalCommand.IntegrationTests (all xUnit, reference TestCommon + respective projects).
- [ ] Add Directory.Build.props to repo root with RunAnalyzersDuringBuild=false on local, ProduceReferenceAssembly=true (EdiPlatform pattern).
- [ ] Add .runsettings file to repo root: `<Tier!=3-Slow&Legacy!=PendingAudit&Legacy!=Archived>` filter.
- [ ] **Commit: "chore: create RentalCommand.sln and project structure (Core/Data/Api/Engine/TestCommon)"**

### Task 2: Migrate entities and enums; delete dead code

**Files:** Move api/Data/Entities/* → RentalCommand.Core/Entities/ (except dead ones). Move api/Data/Enums/* → RentalCommand.Core/Enums/. Delete api/LegacyMigrations/. Delete api/Api/(ProjectEndpoints, MilestoneEndpoints, PhaseEndpoints, TeamEndpoints, LabelEndpoints, CommentEndpoints, TestPlanEndpoints, AiEndpoints skeleton). Rename Lifecycle namespace → RentalCommand everywhere.

**Steps:**
- [ ] Delete the 18 dead entities (ProjectTask, Milestone, Phase, LifecycleTask, TestPlan*, TeamMember, TaskAssignment, AgentSession, AgentEscalation, Comment, Label, TaskLabel) from api/Data/Entities/.
- [ ] Delete api/LegacyMigrations/ entirely.
- [ ] Delete endpoint files: ProjectEndpoints.cs, MilestoneEndpoints.cs, PhaseEndpoints.cs, TeamEndpoints.cs, LabelEndpoints.cs, CommentEndpoints.cs, TestPlanEndpoints.cs (keep TaskEndpoints.cs temporarily for refactor).
- [ ] Global namespace rename: grep -r "namespace Lifecycle" api/ → "namespace RentalCommand"; grep -r "using Lifecycle" api/ → "using RentalCommand". (Or via an IDE refactor, careful not to break class names.)
- [ ] Move api/Data/Entities/ → RentalCommand.Core/Entities/.
- [ ] Move api/Data/Enums/ → RentalCommand.Core/Enums/.
- [ ] Move api/Data/LifecycleDbContext.cs → RentalCommand.Data/RentalCommandDbContext.cs; rename class LifecycleDbContext → RentalCommandDbContext : IdentityDbContext<ApplicationUser, IdentityRole<int>, int> (to be fleshed out in Task 3).
- [ ] **Commit: "chore: delete dead template entities + LegacyMigrations; rename Lifecycle→RentalCommand namespace"**

### Task 3: Add ApplicationUser + auth entities; refactor DbContext

**Files:** Create RentalCommand.Core/Entities/ApplicationUser.cs, RentalCommand.Core/Entities/RefreshToken.cs, RentalCommand.Core/Entities/AuditLog.cs, RentalCommand.Core/Entities/OwnerEntity.cs. Create RentalCommand.Core/Enums/OwnerEntityType.cs, RentalCommand.Core/Enums/AuditLogOperation.cs. Refactor RentalCommandDbContext to inherit IdentityDbContext<ApplicationUser, IdentityRole<int>, int> + add DbSets for auth entities + soft-delete + constraints.

**Steps:**
- [ ] Create ApplicationUser.cs in RentalCommand.Core/Entities:
  ```csharp
  public class ApplicationUser : IdentityUser<int>   // int PK to match all domain entities
  {
      public int? PortfolioId { get; set; }
      public int? OwnerEntityId { get; set; }
      public int? TenantId { get; set; }
      public string DisplayName { get; set; } = string.Empty;
      public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
      public DateTime? LastLoginAt { get; set; }
      public Portfolio? Portfolio { get; set; }
      public OwnerEntity? OwnerEntity { get; set; }
      public Tenant? Tenant { get; set; }
      public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
      public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
  }
  ```
- [ ] Create RefreshToken.cs:
  ```csharp
  public class RefreshToken
  {
      public int Id { get; set; }
      public int UserId { get; set; }   // FK → ApplicationUser.Id (int)
      public string Token { get; set; } = string.Empty;
      public string TokenHash { get; set; } = string.Empty;
      public DateTime ExpiresAt { get; set; }
      public DateTime IssuedAt { get; set; }
      public bool IsRevoked { get; set; }
      public bool IsUsed { get; set; }
      public string? IpAddress { get; set; }
      public string? UserAgent { get; set; }
      public ApplicationUser? User { get; set; }
      public int? PortfolioId { get; set; }
  }
  ```
- [ ] Create OwnerEntity.cs with enum OwnerEntityType {Person, LLC, Trust}; add soft-delete flag (IsDeleted: DateTime? DeletedAt).
- [ ] Create AuditLog.cs (append-only: Id, PortfolioId, **UserId int? + ActorLabel string?** — nullable because system/AI actors like "ai-scan" or "engine:RentChargeWorker" have no ApplicationUser, EntityType, EntityId, Operation enum, OldValues JSON, NewValues JSON, Timestamp, IpAddress).
- [ ] Create RentalCommand.Core/Enums/OwnerEntityType.cs (Person=0, LLC=1, Trust=2).
- [ ] Create RentalCommand.Core/Enums/AuditLogOperation.cs (Created=0, Updated=1, Deleted=2, Approved=3, Rejected=4).
- [ ] Refactor RentalCommandDbContext in RentalCommand.Data/ to inherit IdentityDbContext<ApplicationUser, IdentityRole<int>, int>:
  ```csharp
  public class RentalCommandDbContext : IdentityDbContext<ApplicationUser, IdentityRole<int>, int>
  {
      public DbSet<Portfolio> Portfolios => Set<Portfolio>();
      public DbSet<OwnerEntity> OwnerEntities => Set<OwnerEntity>();
      // ... existing entities ...
      public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
      public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
      public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
      public DbSet<QueuedJob> QueuedJobs => Set<QueuedJob>();
      public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
      public DbSet<ScanDraft> ScanDrafts => Set<ScanDraft>();
  }
  ```
- [ ] Add OnModelCreating config for ApplicationUser (username=email, constraints on PortfolioId/TenantId/OwnerEntityId foreign keys).
- [ ] **Int-key ripple (do consistently):** register `AddIdentity<ApplicationUser, IdentityRole<int>>()` in Program.cs (Task 6); `JwtTokenService` writes `ClaimTypes.NameIdentifier`/`sub` as the int `user.Id.ToString()`; `AuthenticatedPortfolioControllerBase.GetUserId()` parses it back with `int.Parse`; `RefreshToken.UserId`, `AuditLog.UserId`, and any other user FK are `int`. (This is the one intentional divergence from EdiPlatform, which uses the default string/GUID key — adjust generic signatures when porting its auth code.)
- [ ] Add constraints to existing entities: `PortfolioId` non-null on Portfolio-scoped entities, soft-delete (IsDeleted: DateTime?) on Portfolio, Property, Lease, Unit, Tenant, Vendor, Expense, Expense.Category → enum ScheduleECategory (Advertising, AutoTravel, CleaningMaintenance, Commissions, Insurance, LegalProfessional, ManagementFees, MortgageInterest, Repairs, Supplies, Taxes, Utilities, Depreciation, Other).
- [ ] Add Portfolio.Currency (string, default "USD").
- [ ] **Commit: "feat: add ApplicationUser + RefreshToken + OwnerEntity + AuditLog entities; refactor DbContext to IdentityDbContext"**

### Task 4: Create initial EF Core migration (SQLite baseline) + migrate to PostgreSQL

**Files:** Create RentalCommand.Data/Migrations/000_InitialCreate.cs. Update appsettings to PostgreSQL connection string.

**Steps:**
- [ ] In RentalCommand.Data project, configure DbContext startup (appsettings.json with DefaultConnection=PostgreSQL, appsettings.Development.json with local Postgres).
- [ ] Create initial migration: `dotnet ef migrations add InitialCreate --project RentalCommand.Data --startup-project RentalCommand.Api` (will scaffold all entities + soft-delete shadow properties + identity schema).
- [ ] Review and hand-edit migration to ensure soft-delete columns (IsDeleted: DateTime?) are included, OwnerEntity enum stored as int, JSON columns on OutboxMessage/ScanDraft/AuditLog, constraints (Expense.Category enum, RentDueDay 1–31, unique (PropertyId,UnitNumber), StartDate<EndDate on Lease, etc.).
- [ ] Create schema.sql or migration SQL that adds Postgres-specific features if needed (advisory lock on engine startup, RLS placeholders).
- [ ] Delete any old SQLite migrations from api/Migrations/ and rebuild the migration history in RentalCommand.Data/Migrations/.
- [ ] Update appsettings.json: `"DefaultConnection": "Host=localhost;Database=rentalcommand;Username=postgres;Password=postgres"`.
- [ ] **Commit: "chore: create initial EF Core migration (PostgreSQL baseline); delete old SQLite migrations"**

### Task 5: Add service interfaces + config classes to RentalCommand.Core

**Files:** Create RentalCommand.Core/Interfaces/ILlmProvider.cs, IFileStorage.cs, IScanService.cs, IPaymentProvider.cs, IScreeningProvider.cs, IEsignProvider.cs, INotificationChannel.cs, IAuditTrailService.cs, IMessagePublisher.cs, IDataUpdateService.cs. Create RentalCommand.Core/Configuration/AssistantConfig.cs, JwtSettings.cs, UploadSettings.cs, AppSettings.cs.

**Steps:**
- [ ] Create ILlmProvider interface (mirror EdiPlatform.Core.Interfaces.ILlmProvider):
  ```csharp
  public interface ILlmProvider
  {
      Task<string> ChatAsync(string prompt, CancellationToken ct = default);
      Task<ExtractedFields> ExtractAsync(byte[] imageOrPdfBytes, string prompt, CancellationToken ct = default);
  }
  public class ExtractedFields
  {
      public Dictionary<string, FieldExtraction> Fields { get; set; }
      public string ModelId { get; set; }
      public int TokensUsed { get; set; }
  }
  public class FieldExtraction
  {
      public string Value { get; set; }
      public decimal Confidence { get; set; }
      public Box? SourceBox { get; set; }
  }
  ```
- [ ] Create other interfaces (stubs): IFileStorage (Upload/Download/Delete), IScanService, IPaymentProvider, IScreeningProvider, IEsignProvider (all with minimal signatures; to be expanded per phase).
- [ ] Create INotificationChannel (Send SMS, Send Email).
- [ ] Create IMessagePublisher (Publish for OutboxMessage).
- [ ] Create IAuditTrailService (LogAsync for AuditLog).
- [ ] Create IDataUpdateService (for SignalR broadcasting).
- [ ] Create AssistantConfig (string ModelId, string? ApiKey, int ContextLength).
- [ ] Create JwtSettings (string SecretKey, string Issuer, string Audience, int AccessTokenExpirationMinutes = 15, int RefreshTokenExpirationDays = 7).
- [ ] Create UploadSettings (string BasePath, int MaxFileSizeBytes, string[] AllowedMimeTypes).
- [ ] **Commit: "feat: add service interfaces + config classes to RentalCommand.Core"**

### Task 6: Implement ASP.NET Identity auth (JWT + refresh token + email verify + password reset + lockout)

**Files:** Create RentalCommand.Api/Services/Auth/JwtTokenService.cs, RentalCommand.Api/Services/Auth/AuthService.cs, RentalCommand.Api/Auth/ApiKeyAuthenticationHandler.cs, RentalCommand.Api/Controllers/AuthController.cs. Create DTOs (LoginRequest, LoginResponse, RegisterRequest, TokenResult). Update Program.cs to wire Identity + JWT.

**Steps:**
- [ ] Create JwtSettings class in RentalCommand.Core/Configuration (as in Task 5).
- [ ] Create JwtTokenService (mirror EdiPlatform.Api.Services.Auth.JwtTokenService.cs):
  - GenerateTokensAsync(ApplicationUser, roles) → AccessToken (15-min) + RefreshToken (7-day, rotated, single-use).
  - RefreshTokenAsync(refreshToken) → new AccessToken + rotated RefreshToken (old token marked IsUsed=true).
  - ValidateAccessToken(token) → ClaimsPrincipal.
  - Includes portfolioId + roles + customerId claims in JWT.
- [ ] Create AuthService (wrapper around UserManager):
  - LoginAsync(email, password) → user lookup + password verify + roles → JwtTokenService.GenerateTokensAsync.
  - RegisterAsync(email, password, displayName) → UserManager.CreateAsync + email-verification token (set IsEmailConfirmed=false until email verified).
  - ConfirmEmailAsync(email, token) → verify token + set IsEmailConfirmed=true.
  - ResetPasswordAsync(email) → send reset-password email (phase-dependent; stub for now).
  - LockoutAsync(user, duration) → UserManager lockout.
- [ ] Create ApiKeyAuthenticationHandler (mirror EdiPlatform.Api.Auth.ApiKeyAuthenticationHandler.cs):
  - Extract X-API-Key header.
  - Verify prefix (first 8 chars) + hash (SHA256) against ApiKey table.
  - Return ClaimsPrincipal with customer/portfolio claims.
- [ ] Create LoginRequest/LoginResponse/RegisterRequest/TokenResult DTOs.
- [ ] Create AuthController (mirror EdiPlatform.Api.Controllers.AuthController.cs):
  - POST /api/v1/auth/login (email, password) → LoginResponse (accessToken, refreshTokenExpiration in cookie).
  - POST /api/v1/auth/register (email, password, displayName) → "Check email to verify".
  - POST /api/v1/auth/refresh (uses cookie refresh token) → new AccessToken + rotated RefreshToken (in cookie).
  - POST /api/v1/auth/confirm-email (token) → set IsEmailConfirmed.
  - GET /api/v1/auth/me (requires JWT) → user info (id, email, displayName, portfolioId, roles).
  - POST /api/v1/auth/logout → revoke refresh token.
  - All endpoints use httpOnly refresh-token cookie (secure, sameSite=Strict).
- [ ] Update Program.cs:
  - AddIdentity<ApplicationUser, IdentityRole> with password policy (8+ chars, upper, lower, digit, no special required).
  - AddEntityFrameworkStores<RentalCommandDbContext>.
  - AddAuthentication(defaultScheme=JwtBearer).
  - AddJwtBearer(validate issuer/audience/signature/lifetime).
  - AddApiKeyBearer (custom authentication scheme for webhooks).
  - Configure JWT + refresh token settings from appsettings.
  - AddCors (restrict to web app origin in prod).
- [ ] **Commit: "feat: implement ASP.NET Identity + JWT + rotated refresh tokens + ApiKeyAuthenticationHandler"**

### Task 7: Migrate existing users/roles; add password rehashing on first login

**Files:** Create RentalCommand.Data/Migrations/MigrateExistingUsers.cs (data migration). Create RentalCommand.Api/Services/Auth/UserMigrationService.cs.

**Steps:**
- [ ] Query old UserAccount entities from Lifecycle.db (if any exist; likely zero for new project).
- [ ] For each user: create ApplicationUser in ASP.NET Identity with email=UserAccount.Email, DisplayName=UserAccount.Name, PortfolioId=UserAccount.PortfolioId.
- [ ] Set PasswordHash to null (force rehash on first login).
- [ ] Assign roles: Admin, Manager, Owner, Tenant, Viewer based on old role (if any).
- [ ] Write a middleware that intercepts login failures due to null PasswordHash, prompts user to reset password, sends reset-password email.
- [ ] Data migration SQL to migrate old users (or seed with 0 if brand new).
- [ ] **Commit: "chore: create user migration + add password-rehash-on-first-login flow"**

### Task 8: Create EngineWorkerBase + OutboxMessage dispatcher

**Files:** Create RentalCommand.Engine/Workers/EngineWorkerBase.cs, RentalCommand.Engine/Workers/OutboxDispatchWorker.cs, RentalCommand.Engine/EngineHostedService.cs, RentalCommand.Engine/Program.cs.

**Steps:**
- [ ] Create EngineWorkerBase (abstract BackgroundService):
  - Single-instance safety: PostgreSQL advisory lock on startup (key 59483 or RentalCommand-specific).
  - Execute(CancellationToken) abstract method.
  - Watchdog loop with configurable interval.
  - Graceful shutdown: release lock on service stop.
  - Error logging + retry logic.
  - (Mirror EdiPlatform.Engine.Workers.EngineWorkerBase logic.)
- [ ] Create OutboxDispatchWorker : EngineWorkerBase:
  - Poll OutboxMessage table (CreatedAt descending, where SentAt=null, RetryCount<5).
  - For each message: parse Payload, route to INotificationChannel (SMS/Email).
  - On success: set SentAt=UtcNow.
  - On failure: increment RetryCount, set FailedAt if retries exhausted.
  - Stub implementation for now (concrete SMS/Email providers in Phase 4).
- [ ] Create EngineHostedService : BackgroundService:
  - Instantiate and start all workers.
  - Graceful shutdown: stop all workers, release locks.
- [ ] Create RentalCommand.Engine/Program.cs:
  - AddDbContext<RentalCommandDbContext> (same connection string as API).
  - AddScoped<IMessagePublisher> (DB-backed implementation).
  - AddScoped<INotificationChannel> (stubs).
  - AddHostedService<EngineHostedService>.
  - Run as console app (scaffold in Dockerfile later).
- [ ] **Commit: "feat: create Engine + EngineWorkerBase + OutboxDispatchWorker"**

### Task 9: Create SignalR hubs + IDataUpdateService

**Files:** Create RentalCommand.Api/Hubs/NotificationHub.cs, RentalCommand.Api/Hubs/DataUpdateHub.cs, RentalCommand.Api/Services/DataUpdateService.cs.

**Steps:**
- [ ] Create NotificationHub (mirror EdiPlatform.Api.Hubs.NotificationHub):
  - OnConnectedAsync: set user context (UserId, PortfolioId from claims).
  - Broadcast methods: SendNotification(message), SendAlert(severity, message).
  - Private groups: user-{userId}, portfolio-{portfolioId}.
- [ ] Create DataUpdateHub:
  - OnConnectedAsync: join user + portfolio groups.
  - Broadcast methods: EntityUpdated(type, id, data), EntityDeleted(type, id).
  - Clients.Group(portfolio-{id}) for portfolio-scoped broadcasts.
- [ ] Create IDataUpdateService (mirror EdiPlatform.Api.Services.RealTime.IDataUpdateService):
  - public async Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data)
  - public async Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId)
  - Injected IHubContext<DataUpdateHub>.
  - Sends SignalR messages to portfolio-{portfolioId} group.
- [ ] Update Program.cs:
  - AddSignalR().AddJsonProtocol().
  - MapHub<NotificationHub>("/api/v1/hubs/notifications").
  - MapHub<DataUpdateHub>("/api/v1/hubs/updates").
- [ ] **Commit: "feat: add SignalR hubs + IDataUpdateService for realtime updates"**

### Task 10: Create thin controllers for each domain (Portfolio, Property, Unit, Tenant, etc.)

**Files:** Create RentalCommand.Api/Controllers/PortfolioController.cs, RentalCommand.Api/Controllers/PropertyController.cs, RentalCommand.Api/Controllers/UnitController.cs, RentalCommand.Api/Controllers/TenantController.cs, RentalCommand.Api/Controllers/LeaseController.cs, RentalCommand.Api/Controllers/PaymentController.cs, RentalCommand.Api/Controllers/ExpenseController.cs, RentalCommand.Api/Controllers/VendorController.cs, RentalCommand.Api/Controllers/WorkOrderController.cs, RentalCommand.Api/Controllers/AppointmentController.cs, RentalCommand.Api/Controllers/InspectionController.cs, RentalCommand.Api/Controllers/AccountingController.cs, RentalCommand.Api/Controllers/ActivityController.cs, RentalCommand.Api/Controllers/PortalController.cs. Create corresponding Service classes in RentalCommand.Api/Services/. Delete endpoint files (ProjectEndpoints.cs, etc., and convert remaining Minimal API MapXxxEndpoints calls).

**Steps:**
- [ ] Create base controller: AuthenticatedPortfolioControllerBase (mirror EdiPlatform):
  ```csharp
  [Authorize]
  public abstract class AuthenticatedPortfolioControllerBase : ControllerBase
  {
      protected int GetPortfolioId() => int.Parse(User.FindFirst("portfolioId")?.Value ?? "0");
      protected string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
      protected IEnumerable<string> GetRoles() => User.FindAll(ClaimTypes.Role).Select(c => c.Value);
  }
  ```
- [ ] Create PortfolioController : AuthenticatedPortfolioControllerBase:
  - GET /api/v1/portfolios (list all for user).
  - GET /api/v1/portfolios/{id} (get one, verify ownership).
  - POST /api/v1/portfolios (create new; set PortfolioId on current user claim).
  - PATCH /api/v1/portfolios/{id} (update; verify ownership).
  - DELETE /api/v1/portfolios/{id} (soft-delete).
  - All endpoints: inject IPortfolioService (scoped), call .Where(p => p.Id == GetPortfolioId()) for tenant scoping.
- [ ] Create PortfolioService (scoped):
  - GetPortfoliosAsync(userId), GetPortfolioAsync(portfolioId), CreateAsync(dto), UpdateAsync(id, dto), DeleteAsync(id).
  - All queries filter by PortfolioId (claim-based; never from request param).
  - Return DTOs (not entities).
- [ ] Repeat for Property, Unit, Tenant, Lease, Payment, Expense, Vendor, WorkOrder, Appointment, Inspection, Accounting (summary endpoint), Activity (read-only list), Portal (user settings).
  - Each has a thin Controller + Service + DTOs (Create/Update/Response).
  - Each PATCH endpoint already exists (per spec); add GET with filter/search/pagination.
  - All read endpoints support: ?skip=0&take=50&filter=name:John&sort=-createdAt.
- [ ] **IMPORTANT:** Add data-testid to all API response DTOs (include a `TestId` field or JSON property for frontend test selectors).
- [ ] Update Program.cs: delete MapXxxEndpoints() calls; replace with builder.Services.AddScoped<IPortfolioService>(), etc.; replace with app.MapControllers().
- [ ] Delete old endpoint files from api/Api/.
- [ ] **Commit: "chore: convert Minimal APIs to thin controllers + scoped services + DTOs"**

### Task 11: Frontend auth + hooks.server.ts + route guards + fetch layer

**Files:** Create/update web/src/hooks.server.ts, web/src/lib/server/token-refresh.ts, web/src/lib/api/client.ts, web/src/lib/stores/auth.ts, web/src/routes/(auth)/(public)/login/+page.svelte, web/src/routes/(protected)/+layout.server.ts, web/src/routes/(protected)/(admin)/+layout.server.ts, web/src/routes/(protected)/(portal)/+layout.server.ts. Create Zod schemas for all forms.

**Steps:**
- [ ] Create hooks.server.ts (mirror EdiPlatform.ediplatform-web/src/hooks.server.ts):
  - Read access_token from cookies on each request.
  - Call /api/v1/auth/me to validate and populate event.locals.user.
  - Handle 401 → call serverRefreshToken (from token-refresh.ts).
  - On refresh success: set new cookies + retry /auth/me.
  - On refresh failure: clear cookies, redirect to /login.
  - Middleware sequence: handle auth → setRequestId → resolve.
- [ ] Create lib/server/token-refresh.ts (mirror EdiPlatform pattern):
  - Single-flight deduplication for concurrent refresh requests.
  - POST /api/v1/auth/refresh with old refresh token (from cookie).
  - On success: extract new accessToken + refreshToken from response; set cookies (httpOnly, sameSite=Strict, secure in prod).
  - On failure: return null (caller clears session).
  - Cache result for 5 seconds to prevent race conditions on rapid invalidateAll().
- [ ] Create lib/api/client.ts (fetch wrapper):
  - Wrap fetch to add Authorization: Bearer {token} header.
  - On 401: call serverRefreshToken → retry with new token.
  - Support single-flight deduplication (same request in-flight → share response).
  - On 403: redirect to /access-denied.
  - All responses: throw on non-2xx (no silent failures).
  - Support generic typing: async function apiCall<T>(...): Promise<T>.
- [ ] Create lib/stores/auth.ts:
  - Writable store: { user, accessToken, portfolioId, roles }.
  - Load from event.locals.user in +layout.server.ts.
  - Subscribe to SignalR AuthChangedHub to detect session revocation.
- [ ] Create route groups:
  - (auth) → public routes (login, register, forgot-password, email-verify).
  - (protected) → requires logged-in user (middleware in +layout.server.ts redirects to /login if !event.locals.user).
  - (protected)/(admin) → requires Admin role.
  - (protected)/(portal) → requires Owner/Manager/Tenant role (application-level filter).
- [ ] Create web/src/routes/(auth)/(public)/login/+page.svelte:
  - Email + Password form (Zod schema).
  - POST /api/v1/auth/login.
  - On success: set cookies (no JS access — server-only).
  - Redirect to /dashboard or /+page.
  - Toast errors on failure.
- [ ] Create web/src/routes/(protected)/+layout.server.ts:
  - Check event.locals.user; if null, redirect to /login.
  - Pass user + portfolios to +page.
- [ ] Create web/src/routes/(protected)/(admin)/+layout.server.ts:
  - Check event.locals.user.roles includes "Admin"; if not, redirect to /403.
- [ ] Create Zod schemas for all forms (LoginSchema, RegisterSchema, UpdatePropertySchema, etc.) in lib/zod/ dir.
- [ ] **Commit: "feat: add frontend SSR auth + hooks.server.ts + route guards + single-flight fetch layer"**

### Task 12: Replace SSE with SignalR integration + TanStack Query bridge

**Files:** Update web/src/lib/realtime/signalr.ts (create if missing), web/src/routes/(protected)/+layout.svelte (add SignalR listener), web/src/lib/hooks/useInvalidateOnSignalR.ts (custom hook).

**Steps:**
- [ ] Create lib/realtime/signalr.ts:
  - Establish HubConnection to /api/v1/hubs/updates.
  - Auto-reconnect with exponential backoff.
  - Emit events: connected, disconnected, error.
  - Export subscribe(eventName, callback) for components.
- [ ] Create custom hook useInvalidateOnSignalR(queryKey: string[]):
  - On mount: subscribe to SignalR DataUpdateHub.
  - On EntityUpdated(type, id): call queryClient.invalidateQueries(queryKey).
  - On EntityDeleted(type, id): call queryClient.removeQueries(queryKey).
  - Cleanup: unsubscribe on unmount.
- [ ] In routes/(protected)/+layout.svelte:
  - Establish HubConnection once per session.
  - Pass hub context to child routes (store or context API).
- [ ] Replace all SSE listeners with SignalR subscribe calls in components.
- [ ] Update data-fetching patterns: useQuery(...) + useInvalidateOnSignalR (auto-refresh on signal).
- [ ] Delete SSE endpoints + SseService from API.
- [ ] **Commit: "feat: replace SSE with SignalR + TanStack Query invalidation bridge"**

### Task 13: Add toasts + data-testid + edit/filter/search/pagination UI to all lists

**Files:** Create web/src/lib/components/Toast.svelte, web/src/lib/stores/toast.ts. Update all list routes (+page.svelte files) to add filter, search, sort, pagination. Add data-testid to all interactive elements.

**Steps:**
- [ ] Create Toast component + store (mirror Shadcn-Svelte Toast).
  - toast.add({ message, severity: "info" | "error" | "success", duration: 5000 }).
  - Store: writable array of toast messages.
  - Component displays them in fixed position (top-right).
- [ ] For each list (portfolios, properties, tenants, leases, expenses, etc.):
  - Add filter input (e.g., search by name, email, status).
  - Add sort dropdown (e.g., sort by -createdAt, name, status).
  - Add pagination (skip/take, with prev/next buttons or page selector).
  - Add "Edit" button per row (PATCH endpoint).
  - Add "Delete" button (soft-delete, confirm dialog).
  - Wire filter/sort/pagination to API query params (?filter=name:John&sort=-createdAt&skip=0&take=50).
  - Use SvelteKit form actions (POST via <form>) or apiCall for mutations.
- [ ] Add data-testid="portfolio-list-item-{id}", data-testid="edit-portfolio-button", etc. to all interactive elements.
- [ ] Add form validation (Zod) to all create/edit forms; display inline errors.
- [ ] Wire form submissions to toast notifications (success/error).
- [ ] **Commit: "feat: add edit/filter/search/pagination to all lists + toast notifications + data-testid"**

### Task 14: Create TestCommon + Tier-2 fixtures + Playwright E2E tests

**Files:** Create RentalCommand.TestCommon/Bases/Tier2WebAppFactory.cs, RentalCommand.TestCommon/Bases/Tier2TestBase.cs, RentalCommand.TestCommon/Bases/Tier2ControllerBase.cs, RentalCommand.TestCommon/DatabaseFixture.cs, RentalCommand.TestCommon/Fakes/FakeLlmProvider.cs, RentalCommand.TestCommon/Fakes/FakeFileStorage.cs. Create web/tests/e2e/auth.spec.ts, web/tests/e2e/portfolio.spec.ts, web/tests/e2e/property.spec.ts (Playwright journeys).

**Steps:**
- [ ] Create Tier2WebAppFactory (mirror EdiPlatform.TestCommon.Bases.Tier2WebAppFactory):
  - Wraps WebApplicationFactory<Program>.
  - Injects test database (via testcontainers or environment variable).
  - Registers fake implementations (ILlmProvider → FakeLlmProvider, IFileStorage → FakeFileStorage, IPaymentProvider → mock, IScreeningProvider → mock, IEsignProvider → mock).
  - Provides CreateClient() with default auth token.
- [ ] Create Tier2TestBase (mirror EdiPlatform.TestCommon.Bases.Tier2TestBase):
  - Wraps IAsyncLifetime (setup/teardown).
  - Factory + DbContext + User.
  - RefreshDb() to rollback transaction between tests.
  - AuthenticateAsync(user) to generate test JWT token.
- [ ] Create Tier2ControllerBase (for controller unit tests):
  - Pre-wired HttpContext + User + ClaimsPrincipal with portfolio claims.
  - Provides controller instance + mocked dependencies.
- [ ] Create DatabaseFixture:
  - Auto-creates Postgres schema (testcontainers if Docker available; falls back to environment connection string).
  - Seeding: portfolios, properties, tenants, users for test data.
  - Rollback between tests (transaction wrapper).
- [ ] Create FakeLlmProvider (no-op, deterministic extraction):
  - Always returns null confidence (safe default).
  - Logs calls for verification.
- [ ] Create FakeLlmProvider, FakeFileStorage, FakePaymentProvider, FakeScreeningProvider, FakeEsignProvider (all stubs).
- [ ] Create web/tests/e2e/:
  - auth.spec.ts: login flow, refresh token rotation, logout.
  - portfolio.spec.ts: create, list, filter, edit, delete.
  - property.spec.ts: create property for portfolio, edit, soft-delete.
  - auth includes 403 redirect test + access-denied page test.
- [ ] All Playwright tests: use data-testid selectors (not class/id names).
- [ ] Add .runsettings to repo root: Tier!=3-Slow&Legacy!=PendingAudit&Legacy!=Archived (exclude slow/legacy tests by default).
- [ ] **Commit: "test: add TestCommon infrastructure + Tier-2 fixtures + Playwright E2E suite"**

### Task 15: GitHub Actions CI + multi-stage Dockerfile + Traefik + Postgres compose

**Files:** Create .github/workflows/ci.yml (build, test, lint), Dockerfile.api, Dockerfile.engine, Dockerfile.web, docker-compose.yml, .env.example.

**Steps:**
- [ ] Create .github/workflows/ci.yml:
  - Triggers: on push to main/develop, on PR.
  - Jobs: lint (dotnet format --verify-no-changes), test (dotnet test + coverage), build (dotnet build).
  - Runs on ubuntu-latest.
  - Cache dependencies (.nuget, npm).
  - Report test results + coverage.
- [ ] Create Dockerfile.api (multi-stage):
  - Stage 1: build (SDK image, dotnet publish -c Release -o /app/publish).
  - Stage 2: runtime (runtime image, COPY published files, ENTRYPOINT ["dotnet", "RentalCommand.Api.dll"], PORT 5001).
  - Include healthcheck.
- [ ] Create Dockerfile.engine (similar, ENTRYPOINT ["dotnet", "RentalCommand.Engine.dll"]).
- [ ] Create Dockerfile.web (node multi-stage):
  - Stage 1: build (node:20, npm ci, npm run build).
  - Stage 2: runtime (node:20-alpine, COPY .svelte-kit, npm run preview or use adapter).
  - PORT 3000.
- [ ] Create docker-compose.yml:
  - postgres service (image: postgres:16, POSTGRES_DB=rentalcommand, POSTGRES_PASSWORD=postgres, volumes: db:/var/lib/postgresql/data).
  - api service (build: ./api, depends_on: postgres, environment: DefaultConnection, ports: 5001:5001).
  - engine service (build: ./engine, depends_on: postgres, environment: DefaultConnection).
  - web service (build: ./web, ports: 3000:3000, depends_on: api).
  - Traefik service (image: traefik, ports: 80:80, 443:443, volumes: /var/run/docker.sock, traefik.yml).
- [ ] Create traefik.yml or docker-compose labels for routing:
  - *.localhost/api/* → api:5001.
  - *.localhost → web:3000.
- [ ] Create .env.example (template for local dev).
- [ ] Create scripts/start-dev.sh (run docker-compose up for local dev).
- [ ] **Commit: "chore: add GitHub Actions CI + Dockerfiles + Traefik + Postgres compose"**

### Task 16: Add missing enum states + Portfolio.Currency + database constraints

**Files:** Update RentalCommand.Core/Enums/*.cs, RentalCommand.Data/Migrations/*, RentalCommand.Data/RentalCommandDbContext.cs.

**Steps:**
- [ ] Create/update enums:
  - PaymentStatus: Pending, Received, Failed, Refunded.
  - ExpenseStatus: Draft, Pending, Approved, Rejected, Paid.
  - LeaseStatus: Draft, Active, Expired, Terminated, PendingSignature.
  - WorkOrderStatus: Open, InProgress, OnHold, Completed, Cancelled.
  - InspectionStatus: Scheduled, InProgress, Completed, Reviewed, Archived.
  - ScheduleECategory: Advertising, AutoTravel, CleaningMaintenance, Commissions, Insurance, LegalProfessional, ManagementFees, MortgageInterest, Repairs, Supplies, Taxes, Utilities, Depreciation, Other.
- [ ] Add Portfolio.Currency (string, default "USD").
- [ ] Add database constraints in OnModelCreating:
  - Lease.StartDate < Lease.EndDate.
  - unique index on (Property.PortfolioId, Unit.UnitNumber).
  - Payment.RentDueDay 1–31.
  - PortfolioId NOT NULL on all tenant-scoped entities.
  - Foreign key constraints with ON DELETE Cascade/SetNull (as appropriate).
- [ ] Run migration to apply (or hand-edit existing migration).
- [ ] **Commit: "feat: add missing enum states + Portfolio.Currency + database constraints"**

### Task 17: Build passes, migration applies, basic smoke tests

**Files:** Verify builds, DB migration, and happy-path endpoints.

**Steps:**
- [ ] Run `dotnet build RentalCommand.sln` (all projects compile).
- [ ] Run `dotnet ef migrations add TestMigration --project RentalCommand.Data` (sanity check).
- [ ] Run `dotnet ef database update --project RentalCommand.Data --startup-project RentalCommand.Api` (Postgres schema created).
- [ ] Run `dotnet test` (Tier 1+2 tests pass; Tier 3 skip if no Docker).
- [ ] Start API + Web locally (docker-compose up or direct dotnet run).
- [ ] Smoke test: POST /api/v1/auth/register → 200, confirm email link works, POST /api/v1/auth/login → 200 with cookie.
- [ ] Smoke test: GET /api/v1/portfolios → 401 (no token) → 200 (with token).
- [ ] Smoke test: web login page loads, form submission works, redirects to dashboard.
- [ ] Web page data-testid selectors are present (browser console check).
- [ ] **Commit: "test: verify build + migration + smoke tests pass"**

### Task 18: Clean up web app + remove legacy code

**Files:** Delete web/src/routes/legacy-*, update web/src/routes layout hierarchy, remove old imports, remove AiEndpoints stub calls.

**Steps:**
- [ ] Delete web/src/routes/legacy-* directories (if any).
- [ ] Delete old API client imports (old service classes, old fetch patterns).
- [ ] Update web/package.json: ensure @sveltejs/kit, svelte 5+, tailwindcss 4+, zod, tanstack/svelte-query, signalr.
- [ ] Delete old SseService.cs + SseEndpoints.cs from API.
- [ ] **Commit: "chore: remove legacy web code + old API endpoints"**

---

## Acceptance criteria

1. **Project structure:** RentalCommand.sln with Core/Data/Api/Engine/TestCommon + five test projects exists and compiles.
2. **Namespace migration:** All code uses RentalCommand namespace (not Lifecycle).
3. **Dead code deleted:** ~18 template entities + LegacyMigrations gone; misnamed endpoint files converted to controllers or deleted.
4. **Database:** PostgreSQL schema created successfully; migrations apply cleanly.
5. **Entities:** ApplicationUser, RefreshToken, OwnerEntity, AuditLog, OutboxMessage, QueuedJob, StoredFile, ScanDraft exist and are seeded for tests.
6. **Auth:** Login/register/refresh/logout work end-to-end; refresh tokens rotate; JWT includes portfolioId claim; API key auth works for webhooks.
7. **Controllers:** All domain entities have thin controller + scoped service + DTOs; PATCH endpoints functional; query params (filter/sort/pagination) supported.
8. **Frontend:** hooks.server.ts validates token and populates event.locals.user; route guards redirect unauthorized requests; fetch layer auto-refreshes; forms have Zod validation + toast errors; all interactive elements have data-testid.
9. **SignalR:** Hubs registered; DataUpdateService broadcasts entity updates to portfolio-{id} groups; frontend invalidates TanStack Query on signal.
10. **Engine:** EngineWorkerBase + OutboxDispatchWorker scaffold complete; advisory lock mechanism in place.
11. **Tests:** TestCommon infrastructure ready; ~15 Playwright journeys (auth, CRUD, filter, pagination) passing; Tier-2 DB fixtures working.
12. **CI/CD:** GitHub Actions workflow builds, tests, lints on PR; Dockerfiles multi-stage; docker-compose runs locally.
13. **Constraints + enums:** All missing enum states added (ExpenseStatus, LeaseStatus, etc.); database constraints (date ordering, unique unit numbers, PortfolioId scoping) enforced; Portfolio.Currency present.
14. **Smoke tests:** `dotnet build`, `dotnet test`, `dotnet run`, web login/dashboard load; no red warnings or errors.

---

## Plan self-review notes

- **Spec §6 + §6.1 coverage:** Foundation (projects, namespace, Identity auth, JWT, refresh tokens), data model (ApplicationUser, OwnerEntity, soft-delete, ScheduleECategory enum, Portfolio.Currency), Entity scoping (PortfolioId from JWT claim), Append-only AuditTrail (entity added; implementation post-baseline). Service interfaces (ILlmProvider stub, IFileStorage stub, IScanService stub, etc.). SignalR hubs (realtime, replacing SSE). Engine workers (scaffold for future rent posting + scan extraction). Edit/filter/pagination (pulled forward as usability prerequisite per spec §7). Upload pipeline (entity shape added; phase 1 is implementation).
- **EdiPlatform parity achieved:** JwtTokenService + refresh rotation + httpOnly cookies (mirror EdiPlatform.Api.Services.Auth.JwtTokenService + CookieRefreshMiddleware). ApiKeyAuthenticationHandler (mirror EdiPlatform.Api.Auth). Controllers + DTOs + scoped services (mirror EdiPlatform.Api.Controllers + EdiPlatform.Api.Services). hooks.server.ts + single-flight refresh (mirror EdiPlatform.ediplatform-web/src/hooks.server.ts + token-refresh.ts). Tier-2 test infrastructure (mirror EdiPlatform.TestCommon). EngineWorkerBase + advisory lock (mirror EdiPlatform.Engine). SignalR hubs (mirror EdiPlatform.Api.Hubs).
- **Out of scope:** Any new user-facing feature (scanning, daily briefing, Q&A, automations, money capture, screening, accounting exports, ownership/entity UI, field maintenance, onboarding) — all Phase 1+. LLM provider implementation (Phase 2); notification channels (Phase 4); payment/screening/esign integrations (Phase 5–6); RLS (deferred; application-layer scoping sufficient Phase 0). Bank reconciliation (Phase 5+). Predictive maintenance (Phase 10+).
- **Risky unknowns to validate early:** Does the frontend Postgres setup work locally (testcontainers / environment connection string)? Are migrations deterministic across team machines? Is the SignalR connection stable under network interruption (reconnect logic)? Do the Playwright tests run reliably in CI?
- **Future-proofing:** Service interfaces designed to be swappable (real Anthropic LLM in Phase 2, real Stripe in Phase 5, real TransUnion in Phase 6). DbContext factored for RLS interceptor (Phase 7+, if multi-tenant SaaS). OutboxMessage pattern supports future RabbitMQ swap. Advisory lock ready for multi-Engine failover. Scoped services ready for per-feature middleware/filtering logic.
- **Sequence discipline:** Delete dead code first (avoid merge conflicts). Split projects before wiring (easier to add wiring than refactor split projects). Migrate auth before building features (everything downstream assumes Identity + JWT). SignalR before building list UI (edit/delete endpoints need realtime confirmation). Controllers before frontend (API contract fixed before UI built). Tests before merging (catch integration bugs early).
