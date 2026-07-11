# Rental Command Foundation Rewrite — Checkpoint 1 Implementation Plan

**Status:** In progress
**Branch:** `tsk-668-670-672-674-foundation-rewrite`
**Tasks:** TSK-672 atomic writes, TSK-670 role/access kernel, supporting infrastructure for TSK-668 and TSK-674
**Source of truth:** `Docs/superpowers/specs/2026-07-10-rental-command-foundation-blueprint.md`

## Outcome

Checkpoint 1 establishes the infrastructure every later destructive vertical slice must use. It does not preserve the old single-role, split-commit, or unclaimed-worker designs. It adds the new primitives, migrates surviving cross-cutting paths, and records obsolete paths for deletion in the checkpoint that replaces their domain.

The checkpoint is complete only when:

- a business command cannot commit without its required audit, ledger, inbox, command receipt, and outbox intent;
- remote work never occurs while a database transaction is open;
- Engine eligibility, ordering, paging, and claiming happen in PostgreSQL;
- an expired/stale worker cannot finalize another worker's claim;
- workspace membership, independently scoped job assignments, relationship experiences, and access revision can be represented without the old one-user/one-role/one-portfolio assumptions;
- authorization queries apply capability and scope together before projection and paging;
- focused PostgreSQL failure, concurrency, and cross-scope tests prove these claims.

## Repository rules applied by this plan

- Prefer one tracked object graph and one save.
- When generated keys or structurally separate flushes are required, one logical command owns one explicit transaction around all saves.
- Use the EF execution strategy around the whole logical command, not around individual saves.
- Do not attempt an automatic retry on a partially accepted `DbContext` change tracker. Retriable commands recreate their scoped context and are protected by a unique command receipt/idempotency key.
- Audit, ledger, inbox, command-receipt, and outbox failures roll back the business mutation.
- AI, storage, payment, e-sign, email, SMS, push, and other remote calls occur outside the database transaction.
- All aggregation, grouping, filtering, joins, authorization scope, sorting, paging, candidate eligibility, and claim selection execute DB-side in translated SQL or a database view/function.
- No migrations, aliases, data copies, role aliases, dual reads, or compatibility bridges are added for old data.

## Baseline inventory

The first repository sweep found 252 `SaveChanges`/`SaveChangesAsync` calls across 78 API, Data, and Engine files, but transaction markers in only 14 files. Counts identify review hotspots, not proof that every call is defective.

Highest save-count hotspots include:

- `InspectionService` — 14
- `DemoDataSeeder` — 12
- `AccountingImportService` — 11
- `LeaseService` — 9
- `BankingService` — 9
- `ApplicationService` — 9
- `ConversationService` — 8
- `DocumentTemplateService` — 7
- `NativeSigningService` — 6
- `AccountingConnectionService` — 6
- `PaymentService`, `NoticeDraftService`, and `CapitalAssetService` — 5 each

Confirmed cross-cutting defects:

1. `AuditSaveChangesInterceptor` saves the business entity first, then recursively saves `AuditLog` rows. Without an existing outer transaction these are two commits.
2. `AuditTrailService` intentionally flushes semantic audit separately, so a failed business command may retain an audit event for work that rolled back, or a committed business row may lack the rich event.
3. API and Engine `OutboxMessagePublisher` implementations call `SaveChanges` themselves. This prevents the business command from owning a single predictable commit boundary.
4. `OutboxDispatchWorker` loads candidates before retry eligibility is applied, which violates the DB-side rule and permits head-of-line starvation.
5. Outbox rows have no atomic claim/lease/fencing token. Overlapping Engine processes can send the same row and a stale worker can overwrite a later outcome.
6. `DedupKey` is optional and non-unique, and it is absent from `IMessagePublisher`; it is not an idempotency guarantee.
7. Suppressed/unconfigured delivery can be marked as sent even though no provider accepted it.
8. The current identity model stores one `PortfolioId`, `OwnerEntityId`, `TenantId`, and global Identity role set directly on `ApplicationUser`; it cannot safely represent multiple workspace assignments or independent Owner/Tenant relationships.
9. The controller base trusts one token `portfolioId` and exposes broad portfolio context before a capability-and-scope join.

### Baseline verification

Before implementation, serialized `dotnet test RentalCommand.sln -m:1 --nologo` produced:

- Core: 42 passed
- API: 868 passed
- Engine: 97 passed, 2 failed
- Integration: 26 passed
- Data filter: no matching tests

The two repeatable baseline failures are both in `LeaseExpiryReminderServiceTests` and expect the obsolete mutable `ExpiryReminderSentAt` marker to be populated. The approved notification/agreement rewrite deletes that path; Checkpoint 1 records it and does not repair it.

Package warnings recorded at baseline:

- MailKit 4.15.1 moderate advisory `GHSA-9j88-vvj5-vhgr`
- SQLitePCLRaw.lib.e_sqlite3 2.1.11 high advisory `GHSA-2m69-gcr7-jv3q`

## Task 1 — Atomic command and audit staging

### Files to replace or add

- `RentalCommand.Core/Persistence/IAtomicUnitOfWork.cs`
- `RentalCommand.Core/Interfaces/IAuditTrailService.cs`
- `RentalCommand.Core/Interfaces/IAuditScope.cs`
- `RentalCommand.Core/Entities/AtomicCommandReceipt.cs`
- `RentalCommand.Data/Auditing/AuditSaveChangesInterceptor.cs`
- `RentalCommand.Data/Auditing/AuditScope.cs`
- `RentalCommand.Data/RentalCommandDbContext.cs`
- `RentalCommand.Data/Persistence/AtomicUnitOfWork.cs`
- API and Engine dependency registrations
- focused API and PostgreSQL integration tests

### Required behavior

1. A command supplies a stable command type/idempotency key and executes through one logical unit of work.
2. The unit of work checks/inserts a unique `AtomicCommandReceipt`, invokes the database mutation, flushes generated keys, materializes generic audit rows, flushes staged semantic audit/outbox/ledger rows, and commits once.
3. The execution strategy surrounds the complete transaction. A retry receives a fresh scoped `DbContext`; it never reuses a tracker after a rolled-back flush.
4. Nested code joins an existing command transaction but cannot commit, roll it back, or start a retry loop.
5. `AuditSaveChangesInterceptor` captures/materializes rows but never recursively calls `SaveChanges`.
6. `AuditTrailService` stages a rich semantic event. It does not independently make a business command durable.
7. Generic and semantic audit use an entity reference plus command-local save/mutation ordinal so the rich event replaces/enriches the exact generic event without collapsing two legitimate updates to the same entity.
8. An explicit audit-only command remains possible, but it still runs through the executor with its own command key.
9. Post-commit broadcasts are returned as post-commit work or represented as outbox work; they never run before the database commit.

### Proof

- injected failure after the business flush rolls back business and generic/semantic audit;
- injected failure during audit/outbox flush rolls back the business mutation;
- duplicate command key produces one effect and one receipt;
- generated IDs appear on the audit row;
- nested command code cannot commit the caller's transaction;
- semantic audit and generic audit result in exactly one rich row;
- two legitimate updates to one entity in a command produce two distinct mutation audit rows;
- generated SQL/transaction logs prove one transaction owns all database writes.

## Task 2 — Durable outbox intent, atomic claim, and fenced completion

### Files to replace or add

- `RentalCommand.Core/Entities/OutboxMessage.cs`
- `RentalCommand.Core/Interfaces/IMessagePublisher.cs`
- notification delivery context/receipt contracts
- `RentalCommand.Api/Services/OutboxMessagePublisher.cs`
- `RentalCommand.Engine/Services/OutboxMessagePublisher.cs`
- `RentalCommand.Data/RentalCommandDbContext.cs`
- PostgreSQL baseline/migration SQL for claim function and indexes
- `RentalCommand.Engine/Services/OutboxClaimStore.cs`
- `RentalCommand.Engine/Workers/OutboxDispatchWorker.cs`
- PostgreSQL integration tests and focused channel tests

### Target row

One row represents one external destination and contains:

- required unique `IdempotencyKey`;
- `AttemptCount`, `LastAttemptAtUtc`, and required `NextAttemptAtUtc`;
- `ClaimOwner`, `ClaimToken`, and `ClaimExpiresAtUtc`;
- `AcceptedAtUtc`, optional `DeliveredAtUtc`, and explicit `DeadLetteredAtUtc`;
- provider, provider message ID, failure kind, and last error;
- no ambiguous `SentAt` or optional advisory dedup field.

### Required behavior

1. Publisher APIs only enqueue/stage. They require the stable key and never call `SaveChanges`.
2. Business facts, audit, and outbox intent commit in Task 1's transaction.
3. One PostgreSQL statement selects only eligible rows, orders/pages them, locks with `FOR UPDATE SKIP LOCKED`, assigns a claim token/expiry, increments the attempt, and returns the claims.
4. No transaction remains open during provider delivery.
5. Success, retryable failure, blocked configuration, and permanent failure use conditional updates by `Id + ClaimToken`. Zero affected rows means the lease was lost and the stale worker cannot finalize.
6. Provider acceptance and provider delivery are different facts. Missing configuration is blocked/dead-letter state, never accepted.
7. Retries reuse the idempotency key. Providers receive it where supported and return a receipt/provider ID.
8. Push fan-out creates destination-level durable work so one failed token does not resend already accepted tokens.
9. Provider webhooks later enter an idempotent inbox keyed by provider event ID and reconcile by provider message ID.

### Indexes

- unique idempotency key;
- ready partial index on `NextAttemptAtUtc, CreatedAtUtc, Id` for nonterminal rows;
- expired-lease partial index on `ClaimExpiresAtUtc, Id` for claimed nonterminal rows;
- partial provider receipt index on provider + provider message ID.

### Proof

- 50 deferred rows cannot hide a newer eligible row;
- two workers claiming simultaneously never receive the same active claim;
- claims cannot be stolen before expiry and can be reclaimed after expiry;
- stale completion after reclaim updates zero rows;
- a crash after provider acceptance retries with the same key;
- configuration absence never marks a row accepted;
- concurrent producers create one intent under the unique key;
- all filtering, sorting, paging, locking, and claiming occur in the one PostgreSQL statement.

## Task 3 — Workspace membership and scoped authorization kernel

### Target entities

- `WorkspaceAccessContext`
- `WorkspaceMembership`
- seeded `RoleProfile`
- `CapabilityDefinition`
- `RoleProfileCapability`
- `MembershipRoleAssignment`
- `MembershipRoleAssignmentProperty`
- `AuthSession`
- `WorkOrderResponsibility` lands with the maintenance slice
- relationship grants (`OwnerUserAccess`, `TenantUserAccess`) hang from the same access context but remain independent of Team membership; their physical domain FKs land with the Owner/LeaseManagement slices rather than temporary legacy bridges

### Required constraints

- one `WorkspaceAccessContext` per user/workspace, containing status, monotonic `AccessRevision`, and last authorized experience;
- at most one management-business `WorkspaceMembership` per access context; relationship-only users need no Team membership;
- unique immutable system key per seeded role profile;
- unique immutable key per capability definition;
- unique capability per role profile;
- each assignment belongs to exactly one membership and one profile;
- scope rows cannot reference a property outside the assignment's workspace;
- an all-property assignment cannot also carry selected-property scope rows;
- effective/revoked timestamps are durable facts; current status is derived in SQL;
- access revision is bumped atomically with every membership, assignment, scope, responsibility, relationship, or status change;
- refresh tokens belong to an `AuthSession`; access JWTs contain identity, session, context, and revision only (`sub`, `sid`, `ctx`, `ar`), never roles or allowed record IDs;
- no `Admin`, `Manager`, `Agent`, `Owner`, or `Tenant` compatibility role mapping.

### Seeded job profiles

- Workspace Administrator
- Property Manager
- Leasing Agent
- Maintenance Technician

Capabilities are stable action keys. An authorization decision succeeds only when one effective assignment supplies both the required capability and the matching resource scope. Capabilities are never unioned before scope is evaluated.

### API primitives

- an `ActiveAccessContext` loaded by one DB-side projection for the signed-in session, context, selected workspace, and revision;
- a required membership/access revision claim or request header checked against the database for mutation authorization;
- reusable query extensions/services that join effective assignment + profile capability + property/assignment scope before projecting or paging records;
- 404 for cross-scope existence-sensitive reads and explanatory 403 for denied actions on a visible record;
- purpose-built restricted projections; forbidden fields are never serialized and hidden in the client.

### Proof

- a sole landlord holds Administrator membership and independent primary Owner relationship without choosing one identity;
- one user can be Leasing Agent for Property A and Technician for assigned work at Property B without capability leakage;
- Property Manager can operate payments/expenses/reconciliation in scope but cannot manage bank connections, credentials, payouts, integrations, Team/security/billing, or self-expand scope;
- Owner/Tenant relationship alone grants no Team capability;
- access revision invalidates stale mutation requests;
- cross-scope decoy records never appear in list, count, aggregate, search, or detail responses;
- generated SQL applies authorization joins before ordering and paging.

The final access cutover must replace the current `RlsConnectionInterceptor` behavior that maps the
customer-facing Admin role—or a missing portfolio claim—to database administrator bypass. Workspace
Administrator remains workspace scoped, and only an explicit platform/background actor mode may
bypass workspace RLS.

Checkpoint 1 deliberately does **not** activate that replacement globally. Review found that doing so
before converting registration, public applications, signing, provider webhooks, accounting OAuth,
startup, and every background worker would either deny legitimate work or encourage a broad platform
bypass around the entire request pipeline. The new capability/scope evaluator is opt-in in this
checkpoint; the later destructive auth cutover replaces the old interceptor once every entry path has
an explicit actor context. There is no endpoint running both authorization models simultaneously.

Access-authority writes in this checkpoint use typed atomic commands. Caller-supplied mutation
delegates and load-all membership/assignment graphs are forbidden. Ownership foreign keys are
immutable after insertion, so moving authority between roots requires revoking the old durable fact
and creating a new one while advancing the affected revisions.

## Task 4 — Surviving-path conversions

Checkpoint 1 converts only infrastructure and domain paths that survive the blueprint. Each conversion removes the old boundary rather than running old and new writers together.

Initial surviving paths to convert and fault-test:

- vendor/work-order dispatch intent;
- native e-sign request + signer + destination intents;
- adverse-action stored artifact + notice + delivery intent;
- payment/provider inbox + ledger + receipt/outbox;
- scan confirm command + source link + audit + downstream intent;
- conversation message + recipient destination intents;
- refresh-token rotation/reuse revocation;
- outbox and provider inbox reconciliation.

Surviving Engine candidate paths that must move to bounded DB-side claim/eligibility primitives:

- scan processing and simulation command queues (currently two-statement read then claim);
- accounting pull and token refresh (currently unbounded materialization before per-row locking);
- debt service, recurring expenses, and recurring maintenance (currently schedule/configuration eligibility after materialization and no claim);
- worker watchdog unhealthy-candidate selection.

Obsolete lease-expiry markers, old notice delivery inheritance, legacy Team roles, and old Lease mutation paths are recorded for deletion in their replacement checkpoints instead of repaired. The same delete-not-repair rule applies to legacy rent-charge, late-fee, autopay-charge, lease-expiry, notice-autopilot, and daily-briefing-delivery worker candidate implementations; their replacements use the TenantAccount/agreement/notification models from later checkpoints.

## Task 5 — Serialized verification and reviews

For each coherent slice:

1. implement with focused tests and self-review;
2. run focused verification without another heavy build in parallel;
3. commit the coherent slice.

Independent review is intentionally batched so review effort stays proportional to risk:

- run one code-quality review after roughly two or three related commits, or sooner when a slice is unusually risky;
- run a spec-compliance review only at a major architectural boundary, destructive cutover, or when implementation may have diverged from the blueprint;
- resolve important findings before crossing the next architectural boundary;
- do not require duplicate spec and quality reviews for every small commit.

Checkpoint verification is serialized:

- focused Core/Data/API/Engine tests;
- PostgreSQL integration tests for transaction rollback, claim concurrency, fencing, query translation, and cross-scope decoys;
- repository `rg` sweeps for recursive audit save, publisher-owned save, client-side worker eligibility, and old role use;
- `dotnet test RentalCommand.sln -m:1` with `MSBUILDDISABLENODEREUSE=1`;
- `dotnet build-server shutdown` after the heavy batch;
- compare failures with the recorded two-test baseline rather than silently normalizing new failures.

## Deferred to later destructive checkpoints

- lease/tenancy/agreement/account domain replacement;
- final Property/Unit baseline and sole-landlord onboarding;
- complete role-aware web/mobile shells and access-revision cache purge;
- global Scan / Add shell and capture review UI;
- notification settings/templates UI and legal notice workflow;
- final single baseline migration and deletion of every old entity/route/provider/test.

These are deferred dependencies, not compatibility work. Checkpoint 1's contracts are designed for them, and later checkpoints delete the old domains as they land.
