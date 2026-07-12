# Lease foundation: physical schema and destructive cutover specification

**Status:** implementation contract for Checkpoint 3 of the approved foundation blueprint

**Scope:** PostgreSQL schema, domain commands, API/client replacement boundary, and verification for `LeaseManagement`, `LeaseAgreement`, `LeaseAddendum`, possession, parties, `TenantAccount`, and tenant ledger

**Baseline:** foundation branch `deb73962`

**Replacement posture:** destructive; no data migration, compatibility DTO, alias, bridge, dual read, or dual write

## 1. Purpose and settled vocabulary

The current `Lease` row is simultaneously a contract, occupancy flag, household membership, e-sign envelope, billing schedule, and mutable lifecycle status. The current `Payment` row is simultaneously a charge and a receipt. Those shapes cannot represent a future signed renewal beside a current agreement, correct an issued contract without overwriting it, retain one balance across renewals, or prevent conflicting occupancy under concurrency.

This specification replaces those shapes with the following boundaries:

- **Unit** remains the physical Command Center through vacancy, applications, occupancy, renewal, move-out, turnover, and later households.
- **LeaseManagement** is one continuous household, possession, and tenant-account episode at one Unit. The product UI calls it **Tenant & lease**.
- **LeaseAgreement** is one legal base agreement version. Draft terms are editable; issued terms and artifacts are immutable.
- **LeaseAddendum** is a separately issued legal amendment attached to the exact base agreement it amends.
- **TenantAccount** owns all resident charges, receipts, credits, adjustments, refunds, and account history for the LeaseManagement episode.
- **Tenant** remains the person/contact record. Its role in a household is effective-dated on `LeaseManagementParty`; it is not copied onto an agreement as mutable membership.
- A legal agreement's state and Unit occupancy are database read models calculated from durable facts. Neither is a writable `Status` column.
- `SingleRental` and `MultiRental` never appear in this schema. They remain presentation/onboarding choices over the same Property, Unit, and lease tables.

All identifiers below use the existing integer domain-key convention unless explicitly marked `bigint`. All instants are `timestamp with time zone` and UTC in .NET. Legal/calendar dates are PostgreSQL `date`. Money is `numeric(18,2)`. Currency is uppercase ISO-4217 `varchar(3)`. Enums are stored as readable strings with database `CHECK` constraints and map through EF string conversions.

## 2. Global database rules

### 2.1 Scope columns and cross-scope foreign keys

Every new business table carries `PortfolioId int NOT NULL`, even where the portfolio could be reached through a parent. This is intentional for row-level security and index-leading authorization predicates. Scope duplication is constrained, never trusted:

1. Add `UNIQUE ("Id", "PortfolioId")` to `Properties`, `Units`, `Tenants`, `DocumentTemplates`, and every new scoped parent used by a composite FK.
2. Add `PortfolioId int NOT NULL` to `Units` in the clean baseline and enforce:
   - `FK_Units_Properties_Scope (PropertyId, PortfolioId) -> Properties(Id, PortfolioId) ON DELETE RESTRICT`.
3. Every child uses a composite `(ParentId, PortfolioId)` FK to the parent's `(Id, PortfolioId)` alternate key.
4. Where both Property and Unit are stored for a hot authorization path, enforce `(UnitId, PropertyId, PortfolioId)` against a unique Unit key. `LeaseManagement.PropertyId` is a constrained scope accelerator; Unit remains its canonical physical parent.

This avoids cross-portfolio references even if an API guard is bypassed. It also allows every list, worker candidate query, and RLS predicate to remain one DB-side statement.

### 2.2 Delete behavior

- All legal, possession, party, account, ledger, deposit, signature, notice, and operational references use `RESTRICT`/`NO ACTION`.
- There is no soft-delete column on the new lease/account domain. A durable cancel, void, supersession, return, reversal, or closure fact replaces deletion.
- Issued legal artifacts and posted financial entries cannot be deleted, including through a portfolio delete. Development/test reset drops and recreates the database.
- Unissued drafts are canceled rather than deleted so attempted move-ins and corrections remain explainable.

### 2.3 RLS and view security

- Apply the existing `tenant_isolation` RLS policy to every new table with `PortfolioId`.
- Views are created with `security_invoker = true` and retain `PortfolioId` as a required projected/filterable column.
- Management API queries set the portfolio session context and additionally filter `PortfolioId` plus capability/property scope in SQL.
- Cross-portfolio Engine candidates run only under the explicit Engine/system database role; they do not disable RLS in application code.

### 2.4 Database time

- Defaults and claim/terminal facts use `clock_timestamp()`, not caller clocks.
- Read-model status uses one PostgreSQL `statement_timestamp()` per statement and the
  portfolio's IANA `TimeZone`. This keeps composed views on the same effective instant while
  defaults, claims, and terminal mutation facts continue to use `clock_timestamp()`.
- Simulation uses a database-visible effective-clock function shared by all status/worker projections. The final implementation must expose `rc_effective_now_utc(portfolio_id)` and `rc_business_date(portfolio_id)` so production and simulation do not implement different status logic.
- Date ranges are half-open internally. A user-facing inclusive `TermEndOn` becomes `TermEndOn + 1` for PostgreSQL range operations.

### 2.5 PostgreSQL extension

Enable `btree_gist` in the clean baseline. It is required for equality-plus-range exclusion constraints on Unit possession, governing agreements, and effective household memberships.

## 3. Core relationship and possession tables

### 3.1 `LeaseManagements`

One row is one continuous household/account episode at one Unit.

| Column | PostgreSQL type | Null | Rule |
|---|---|---:|---|
| `Id` | `int generated by default as identity` | no | PK |
| `PublicId` | `uuid` | no | default `gen_random_uuid()`, globally unique; used in durable links |
| `PortfolioId` | `int` | no | scoped FK |
| `PropertyId` | `int` | no | constrained scope accelerator |
| `UnitId` | `int` | no | canonical physical parent |
| `RelationshipNumber` | `varchar(100)` | no | human-readable, unique per portfolio |
| `PlannedPossessionAtUtc` | `timestamptz` | yes | planned handover; not occupancy |
| `PossessionGivenAtUtc` | `timestamptz` | yes | opens physical possession |
| `PossessionAgreementExceptionReason` | `varchar(1000)` | yes | required only when a gated command records possession without a governing executed Agreement |
| `PossessionAgreementExceptionAuthorizedByUserId` | `int` | yes | capability-gated override actor, `RESTRICT` |
| `NoticeGivenAtUtc` | `timestamptz` | yes | operational notice fact, not agreement status |
| `PlannedMoveOutAtUtc` | `timestamptz` | yes | expected return |
| `PossessionReturnedAtUtc` | `timestamptz` | yes | closes physical possession |
| `AccountClosedAtUtc` | `timestamptz` | yes | relationship closes only after account close |
| `CanceledAtUtc` | `timestamptz` | yes | planned relationship did not proceed |
| `CancellationReasonCode` | `varchar(40)` | yes | required when canceled |
| `CancellationNote` | `varchar(2000)` | yes | optional explanation |
| `EndingDisposition` | `varchar(40)` | no | `Undecided`, `OfferRenewal`, `OfferMonthToMonth`, `NonRenewalMoveOut`; default `Undecided` |
| `EndingDispositionDecidedAtUtc` | `timestamptz` | yes | required unless undecided |
| `EndingDispositionDecidedByUserId` | `int` | yes | Identity FK, `RESTRICT` |
| `CreatedAtUtc` | `timestamptz` | no | DB clock |
| `CreatedByUserId` | `int` | no | Identity FK, `RESTRICT` |
| `UpdatedAtUtc` | `timestamptz` | no | only mutable operational facts update |
| `RowVersion` | `uuid` | no | optimistic token regenerated by command |

Keys and indexes:

- PK `Id`; alternate key `UNIQUE (Id, PortfolioId)`.
- `UNIQUE (PublicId)`.
- `UNIQUE (PortfolioId, RelationshipNumber)`.
- `UNIQUE (Id, UnitId, PropertyId, PortfolioId)` for constrained downstream context.
- index `(PortfolioId, UnitId, CreatedAtUtc DESC, Id DESC)`.
- index `(PortfolioId, PlannedPossessionAtUtc, Id) WHERE PossessionGivenAtUtc IS NULL AND CanceledAtUtc IS NULL`.
- index `(PortfolioId, PossessionReturnedAtUtc, AccountClosedAtUtc, Id)` for ending/close reconciliation.

Checks:

- property/unit/portfolio match through `FK_LeaseManagements_Units_Scope (UnitId, PropertyId, PortfolioId)`.
- `PossessionGivenAtUtc IS NULL OR CanceledAtUtc IS NULL`.
- `PossessionReturnedAtUtc IS NULL OR PossessionGivenAtUtc IS NOT NULL`.
- `PossessionReturnedAtUtc IS NULL OR PossessionReturnedAtUtc >= PossessionGivenAtUtc`.
- possession-exception reason and actor are both null or both present, and cannot be present without `PossessionGivenAtUtc`.
- `AccountClosedAtUtc IS NULL OR PossessionReturnedAtUtc IS NOT NULL`.
- `AccountClosedAtUtc IS NULL OR AccountClosedAtUtc >= PossessionReturnedAtUtc`.
- cancellation code/timestamp are both null or both non-null.
- ending decision fields are null for `Undecided` and present for all other values.

Possession exclusion constraint:

```sql
EXCLUDE USING gist
(
  "UnitId" WITH =,
  tstzrange("PossessionGivenAtUtc", "PossessionReturnedAtUtc", '[)') WITH &&
)
WHERE ("PossessionGivenAtUtc" IS NOT NULL AND "CanceledAtUtc" IS NULL)
DEFERRABLE INITIALLY IMMEDIATE;
```

The constraint is deferrable so an atomic Unit transfer can close the old possession and open the destination possession in one transaction. Planned possession never blocks occupancy. API pre-checks provide friendly errors; this constraint settles concurrency.

### 3.2 `LeaseManagementParties`

This replaces `LeaseTenants` and the primary `Lease.TenantId` shortcut.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PortfolioId` | `int` | no | RLS/scope |
| `LeaseManagementId` | `int` | no | composite scoped FK, `RESTRICT` |
| `TenantId` | `int` | no | person/contact, composite scoped FK, `RESTRICT` |
| `Role` | `varchar(30)` | no | `PrimaryTenant`, `CoTenant`, `Guarantor`, `Occupant` |
| `EffectiveFrom` | `date` | no | inclusive |
| `EffectiveThrough` | `date` | yes | inclusive; null is open-ended |
| `GuarantorLegalNoticeEligible` | `boolean` | no | default false; only meaningful for guarantor |
| `ChangeReason` | `varchar(500)` | no | why membership began/ended/changed |
| `CreatedAtUtc` | `timestamptz` | no | DB clock |
| `CreatedByUserId` | `int` | no | `RESTRICT` |

Constraints and indexes:

- `UNIQUE (Id, PortfolioId)`.
- `CHECK (EffectiveThrough IS NULL OR EffectiveThrough >= EffectiveFrom)`.
- `CHECK (Role = 'Guarantor' OR GuarantorLegalNoticeEligible = false)`.
- exclusion on `(LeaseManagementId =, TenantId =, daterange(EffectiveFrom, EffectiveThrough + 1, '[)') &&)` prevents overlapping memberships for one person.
- partial exclusion on `(LeaseManagementId =, daterange(...) &&) WHERE Role = 'PrimaryTenant'` permits at most one primary at any date.
- index `(PortfolioId, TenantId, EffectiveFrom DESC, Id DESC)` powers Tenant history.
- index `(PortfolioId, LeaseManagementId, EffectiveFrom, EffectiveThrough, Role)` powers current household and recipient resolution.

Role implications are deterministic:

| Role | Occupies Unit | Financially responsible | Default financial/legal recipient |
|---|---:|---:|---:|
| PrimaryTenant | yes | yes | yes |
| CoTenant | yes | yes | yes |
| Guarantor | no | yes | only when `GuarantorLegalNoticeEligible` and notice type permits |
| Occupant | yes | no | no |

Signing is not inferred forever from this table. Each issued artifact freezes its own signer snapshot.

### 3.3 `TenantUserAccesses`

Relationship access remains separate from workspace jobs.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PublicId` | `uuid` | no | globally unique legal-artifact identity |
| `PortfolioId` | `int` | no | RLS |
| `ApplicationUserId` | `int` | no | Identity FK, `RESTRICT` |
| `LeaseManagementPartyId` | `int` | no | scoped FK, `RESTRICT` |
| `GrantedAtUtc` | `timestamptz` | no | explicit grant/invite acceptance |
| `RevokedAtUtc` | `timestamptz` | yes | access stops immediately |
| `GrantedByUserId` | `int` | no | Identity FK |
| `RevokedByUserId` | `int` | yes | required with revocation |
| `Reason` | `varchar(500)` | no | durable explanation |

- Partial unique `(ApplicationUserId, LeaseManagementPartyId) WHERE RevokedAtUtc IS NULL`.
- Tenant portal queries join this row, its effective party, and the requested LeaseManagement/Account in one scoped SQL statement.
- Party membership ending does not silently delete historical portal access; explicit policy/command decides revocation or retained read-only history.

### 3.4 `UnitOperationalPeriods`

`Unit.Status` is deleted. Non-occupancy operational facts remain independent.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PortfolioId` | `int` | no | scoped |
| `PropertyId` | `int` | no | constrained context |
| `UnitId` | `int` | no | Unit FK |
| `Type` | `varchar(30)` | no | `Turnover`, `OutOfService`, `ManagementHold` |
| `StartedAtUtc` | `timestamptz` | no | inclusive |
| `EndedAtUtc` | `timestamptz` | yes | exclusive |
| `SourceLeaseManagementId` | `int` | yes | required for turnover, scoped FK |
| `Reason` | `varchar(1000)` | no | explanation |
| `CreatedAtUtc` | `timestamptz` | no | DB clock |
| `CreatedByUserId` | `int` | no | Identity FK |

- Check end after start.
- One open period per Unit/type via partial unique `(UnitId, Type) WHERE EndedAtUtc IS NULL`.
- An atomic possession-return command starts `Turnover` when appropriate; a maintenance completion command ends it.
- Occupancy and operational flags may both be true during emergencies; reports never equate OutOfService with vacancy.

## 4. Immutable legal artifacts

### 4.1 `LegalDocumentArtifacts`

This is the immutable database identity for an issued or executed PDF. It wraps a stored blob without depending on mutable `StoredFiles.EntityType/EntityId` strings.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PublicId` | `uuid` | no | unique, default `gen_random_uuid()` |
| `PortfolioId` | `int` | no | RLS |
| `StoredFileId` | `int` | no | unique scoped FK, `RESTRICT` |
| `ArtifactKind` | `varchar(30)` | no | `IssuedAgreement`, `ExecutedAgreement`, `IssuedAddendum`, `ExecutedAddendum`, `CompletionCertificate` |
| `StorageKey` | `varchar(500)` | no | content-addressed immutable key |
| `FileName` | `varchar(255)` | no | frozen display name |
| `ContentType` | `varchar(100)` | no | PDF for lease artifacts |
| `ByteLength` | `bigint` | no | positive |
| `ContentSha256` | `char(64)` | no | lowercase hex SHA-256 |
| `CreatedAtUtc` | `timestamptz` | no | DB clock |
| `CreatedByUserId` | `int` | no | actor/system user |

- Unique `PublicId`, `(PortfolioId, StoredFileId)`, and `(PortfolioId, StorageKey)`; nonunique `(PortfolioId, ContentSha256)` supports duplicate/integrity review without forcing two legally distinct documents to share one artifact row.
- Trigger rejects every `UPDATE` and `DELETE`.
- Blob storage uses create-if-absent semantics at an artifact-UUID-plus-SHA key. Reads re-hash when producing evidence or on integrity audits. Object versioning/retention is enabled in deployed storage.
- A pending upload is written before the DB transaction. The issue/finalize transaction validates ownership, purpose, length, and SHA, promotes it to `StoredFile` plus `LegalDocumentArtifact`, and binds it to the agreement/signature state atomically. Failed transactions delete only the unpromoted pending object.

The database guarantees immutable identity and hash; storage retention guarantees bytes at that identity cannot be overwritten.

### 4.2 `LeaseAgreements`

One row is one base legal version within a LeaseManagement.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PublicId` | `uuid` | no | globally unique |
| `PortfolioId` | `int` | no | RLS |
| `LeaseManagementId` | `int` | no | scoped FK, `RESTRICT` |
| `VersionNumber` | `int` | no | starts at 1, strictly increases inside LeaseManagement |
| `AgreementNumber` | `varchar(100)` | no | display/legal identifier |
| `ChangeType` | `varchar(30)` | no | `Initial`, `Correction`, `Renewal`, `MonthToMonth`, `Restatement` |
| `ReplacesAgreementId` | `int` | yes | correction/restatement predecessor |
| `RenewsAgreementId` | `int` | yes | renewal/month-to-month predecessor |
| `TermType` | `varchar(20)` | no | `FixedTerm`, `MonthToMonth` |
| `TermStartOn` | `date` | no | contract term date |
| `TermEndOn` | `date` | yes | inclusive; required for fixed, null for month-to-month |
| `GoverningFromOn` | `date` | no | date this version begins governing; may be later than original term start for a correction |
| `SupersededEffectiveOn` | `date` | yes | first date this version no longer governs due to replacement |
| `SupersededByAgreementId` | `int` | yes | exact successor; paired with effective date |
| `SupersessionRecordedAtUtc` | `timestamptz` | yes | when future/immediate supersession was recorded |
| `BaseRentAmount` | `numeric(18,2)` | no | nonnegative |
| `RentDueDay` | `smallint` | no | 1..31; command applies last-day rule for short months |
| `SecurityDepositObligation` | `numeric(18,2)` | no | nonnegative contractual obligation |
| `LateFeeAmount` | `numeric(18,2)` | no | nonnegative; richer future policy remains typed columns/table, not hidden JSON |
| `GracePeriodDays` | `smallint` | no | 0..31 |
| `Currency` | `varchar(3)` | no | matches TenantAccount/portfolio |
| `TermsSchemaVersion` | `int` | no | renderer/parser contract version |
| `TermsPayload` | `jsonb` | no | complete frozen render payload; queried business terms remain relational columns |
| `DocumentTemplateId` | `int` | no | scoped FK, `RESTRICT` |
| `DocumentTemplateVersion` | `int` | no | frozen template version |
| `IssuedArtifactId` | `int` | yes | immutable original PDF |
| `IssuedAtUtc` | `timestamptz` | yes | freezes terms/signers; local durable issue admission |
| `ExecutedArtifactId` | `int` | yes | immutable completed PDF |
| `FullyExecutedAtUtc` | `timestamptz` | yes | authoritative execution fact |
| `VoidedAtUtc` | `timestamptz` | yes | issued artifact legally voided |
| `VoidReasonCode` | `varchar(40)` | yes | required with void |
| `VoidNote` | `varchar(2000)` | yes | explanation |
| `DraftCanceledAtUtc` | `timestamptz` | yes | unissued draft canceled |
| `DraftCancellationReason` | `varchar(1000)` | yes | required with draft cancellation |
| `CreatedAtUtc` | `timestamptz` | no | DB clock |
| `CreatedByUserId` | `int` | no | actor |
| `UpdatedAtUtc` | `timestamptz` | no | changes only while unissued |
| `DraftRevision` | `int` | no | optimistic draft revision, starts 1 |

Why both term and governing dates exist: a correction/restatement may preserve the legal document's original term dates while beginning to govern prospectively. Renewals normally have `GoverningFromOn = TermStartOn`. This prevents the common error of rewriting the prior agreement's dates merely to make range constraints work.

Checks and lineage:

- `UNIQUE (Id, PortfolioId)` and `UNIQUE (Id, LeaseManagementId, PortfolioId)`.
- `UNIQUE (PublicId)`, `(LeaseManagementId, VersionNumber)`, and `(PortfolioId, AgreementNumber)`.
- fixed term requires `TermEndOn >= TermStartOn`; month-to-month requires null end.
- `GoverningFromOn >= TermStartOn` and, when fixed, `GoverningFromOn <= TermEndOn`.
- supersession date and successor are both null or both present; supersession is after `GoverningFromOn`.
- issuance requires issued artifact; execution requires issuance and executed artifact.
- void requires issuance; draft cancellation requires no issuance; void and draft cancellation are mutually exclusive.
- `Initial`: both predecessor FKs null and version 1.
- `Correction`/`Restatement`: `ReplacesAgreementId` required, `RenewsAgreementId` null.
- `Renewal`/`MonthToMonth`: `RenewsAgreementId` required, `ReplacesAgreementId` null.
- composite self-FKs guarantee predecessor/successor belongs to the same LeaseManagement and portfolio.
- trigger locks the parent LeaseManagement and rejects a new `VersionNumber` unless it equals current max + 1. Rollback cannot consume a visible version.

Governing period expression:

```sql
daterange(
  "GoverningFromOn",
  LEAST(
    COALESCE("TermEndOn" + 1, 'infinity'::date),
    COALESCE("SupersededEffectiveOn", 'infinity'::date)
  ),
  '[)'
)
```

Exclusion constraint:

```sql
EXCLUDE USING gist
(
  "LeaseManagementId" WITH =,
  /* governing period expression above */ WITH &&
)
WHERE ("FullyExecutedAtUtc" IS NOT NULL AND "VoidedAtUtc" IS NULL)
DEFERRABLE INITIALLY IMMEDIATE;
```

This permits an executed future renewal after the current fixed term, but rejects overlapping executed agreements. A future correction records the old agreement's `SupersededEffectiveOn` and the new agreement's matching `GoverningFromOn` in one transaction; no midnight worker is needed to supersede it.

Indexes:

- `(PortfolioId, LeaseManagementId, VersionNumber DESC)`.
- `(PortfolioId, GoverningFromOn, TermEndOn, Id) WHERE FullyExecutedAtUtc IS NOT NULL AND VoidedAtUtc IS NULL` for current/upcoming candidates.
- `(PortfolioId, TermEndOn, Id) WHERE FullyExecutedAtUtc IS NOT NULL AND VoidedAtUtc IS NULL AND TermEndOn IS NOT NULL` for expiration notices.
- `(PortfolioId, IssuedAtUtc, FullyExecutedAtUtc, Id) WHERE VoidedAtUtc IS NULL` for signature queues.

### 4.3 Agreement signer snapshots

`LeaseAgreementSigners` freezes who must sign and the address used at issuance.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PortfolioId` | `int` | no | scope |
| `LeaseAgreementId` | `int` | no | scoped FK |
| `LeaseManagementPartyId` | `int` | yes | provenance; nullable for an external/non-member signer |
| `TenantId` | `int` | yes | person provenance |
| `SignerRole` | `varchar(30)` | no | `PrimaryTenant`, `CoTenant`, `Guarantor`, `Manager`, `Owner`, `Other` |
| `NameSnapshot` | `varchar(200)` | no | frozen |
| `EmailSnapshot` | `varchar(320)` | no | frozen |
| `SigningOrder` | `smallint` | no | positive |
| `IsRequired` | `boolean` | no | normally true |

- Unique `(LeaseAgreementId, SigningOrder)` and `(LeaseAgreementId, lower(EmailSnapshot))`.
- At least one responsible tenant signer is enforced by the issue command and a deferred constraint trigger.
- Child trigger rejects signer insert/update/delete after parent `IssuedAtUtc` is set.
- Issue transaction inserts/validates signers before setting the parent issue fact.

### 4.4 `LeaseAddenda`

Each logical addendum has a stable `SeriesPublicId`; corrections create later versions in that series.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PublicId` | `uuid` | no | unique row identity |
| `SeriesPublicId` | `uuid` | no | stable logical addendum identity |
| `PortfolioId` | `int` | no | RLS |
| `LeaseManagementId` | `int` | no | scoped FK |
| `BaseAgreementId` | `int` | no | exact agreement amended; same LeaseManagement/portfolio |
| `VersionNumber` | `int` | no | starts at 1 per series |
| `AddendumNumber` | `varchar(100)` | no | human/legal number |
| `Purpose` | `varchar(40)` | no | `Financial`, `Pet`, `Occupancy`, `Rules`, `Other` |
| `ReplacesAddendumId` | `int` | yes | prior version in same series |
| `EffectiveFromOn` | `date` | no | inclusive |
| `EffectiveThroughOn` | `date` | yes | inclusive; null open until explicitly ended/replaced/renewal boundary |
| `SupersededEffectiveOn` | `date` | yes | first date prior version no longer applies |
| `SupersededByAddendumId` | `int` | yes | paired successor |
| `SupersessionRecordedAtUtc` | `timestamptz` | yes | durable record time |
| `TermsSchemaVersion` | `int` | no | payload schema |
| `TermsPayload` | `jsonb` | no | complete frozen legal payload |
| `DocumentTemplateId` | `int` | no | scoped FK |
| `DocumentTemplateVersion` | `int` | no | frozen |
| `IssuedArtifactId` | `int` | yes | immutable PDF |
| `IssuedAtUtc` | `timestamptz` | yes | freeze boundary |
| `ExecutedArtifactId` | `int` | yes | immutable executed PDF |
| `FullyExecutedAtUtc` | `timestamptz` | yes | execution fact |
| `VoidedAtUtc` | `timestamptz` | yes | issued artifact legally voided |
| `VoidReasonCode` | `varchar(40)` | yes | required with void |
| `VoidNote` | `varchar(2000)` | yes | optional explanation |
| `DraftCanceledAtUtc` | `timestamptz` | yes | unissued draft cancellation |
| `DraftCancellationReason` | `varchar(1000)` | yes | required with draft cancellation |
| `CreatedAtUtc` | `timestamptz` | no | DB clock |
| `CreatedByUserId` | `int` | no | Identity FK, `RESTRICT` |
| `UpdatedAtUtc` | `timestamptz` | no | draft-only mutation time |
| `DraftRevision` | `int` | no | optimistic draft revision, starts 1 |

Constraints/indexes:

- unique `PublicId`; unique `(LeaseManagementId, SeriesPublicId, VersionNumber)`; unique `(PortfolioId, AddendumNumber, VersionNumber)`.
- composite FK `(BaseAgreementId, LeaseManagementId, PortfolioId)` prevents cross-relationship addenda.
- version trigger locks LeaseManagement and requires max-in-series + 1.
- correction must reference the preceding version in the same series.
- dates, issuance, execution, void, cancellation, and paired supersession follow agreement checks.
- index `(PortfolioId, LeaseManagementId, EffectiveFromOn, EffectiveThroughOn)` for billing/record home.
- index `(PortfolioId, BaseAgreementId, SeriesPublicId, VersionNumber DESC)` for agreement history.

Addenda are not included in the base-agreement exclusion constraint. Multiple executed addenda may be simultaneously effective.

`LeaseAddendumSigners` has this exact shape:

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PortfolioId` | `int` | no | RLS/scope |
| `LeaseAddendumId` | `int` | no | composite scoped FK, `RESTRICT` |
| `LeaseManagementPartyId` | `int` | yes | signer provenance, `RESTRICT` |
| `TenantId` | `int` | yes | person provenance, `RESTRICT` |
| `SignerRole` | `varchar(30)` | no | same allowed values as Agreement signer |
| `NameSnapshot` | `varchar(200)` | no | frozen display/legal name |
| `EmailSnapshot` | `varchar(320)` | no | frozen delivery address |
| `SigningOrder` | `smallint` | no | positive |
| `IsRequired` | `boolean` | no | default true |

It has unique `(LeaseAddendumId, SigningOrder)` and `(LeaseAddendumId, lower(EmailSnapshot))` indexes. A deferred constraint trigger requires at least one required signer at issue; the freeze trigger rejects child mutation after parent issuance.

### 4.5 `LeaseAddendumFinancialEffects`

Financial effects are relational, queryable, and frozen with their parent. A non-financial addendum has zero effect rows.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PortfolioId` | `int` | no | scope |
| `LeaseAddendumId` | `int` | no | scoped FK |
| `EffectType` | `varchar(40)` | no | `RecurringRentDelta`, `OneTimeCharge`, `DepositObligationDelta` |
| `Amount` | `numeric(18,2)` | no | signed for deltas; positive for one-time charge |
| `Currency` | `varchar(3)` | no | account currency |
| `ChargeCode` | `varchar(50)` | no | typed reporting/billing code |
| `EffectiveFromOn` | `date` | yes | recurring/deposit effective date |
| `EffectiveThroughOn` | `date` | yes | recurring inclusive end |
| `DueOn` | `date` | yes | one-time charge due date |
| `Description` | `varchar(500)` | no | statement description |

Checks enforce column shape by type:

- recurring delta: nonzero amount, `EffectiveFromOn` required, `DueOn` null;
- one-time charge: amount positive, `DueOn` required, effective range null;
- deposit delta: nonzero amount, `EffectiveFromOn` required, `DueOn` null;
- through date is not before from date.

It has `UNIQUE (Id, PortfolioId)`, index `(PortfolioId, LeaseAddendumId, EffectType, EffectiveFromOn, EffectiveThroughOn)`, and index `(PortfolioId, DueOn, Id) WHERE EffectType = 'OneTimeCharge'`.

Posting a one-time effect uses ledger `BusinessKey = 'addendum-effect:' || effect.Id`. The
billable projection treats the effect as eligible on or after `DueOn` only while that key has
never been posted, so retries and later status queries cannot recreate the charge.

Trigger rejects every mutation after parent issuance. Billing candidates join executed/nonvoid effective addenda and effects in one SQL statement. Renewal never silently carries an effect: the renewal command records each series disposition as `End`, `IncorporateIntoBase`, or `ReissueAsAddendum` in `LeaseRenewalAddendumDecisions`.

### 4.6 `LeaseRenewalAddendumDecisions`

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PortfolioId` | `int` | no | scope |
| `LeaseManagementId` | `int` | no | constrained relationship scope |
| `RenewalAgreementId` | `int` | no | renewal/MTM agreement |
| `SourceAddendumSeriesPublicId` | `uuid` | no | exact source series |
| `Decision` | `varchar(30)` | no | `End`, `IncorporateIntoBase`, `ReissueAsAddendum` |
| `ReplacementAddendumId` | `int` | yes | required only for reissue |
| `CreatedAtUtc` | `timestamptz` | no | DB clock |
| `CreatedByUserId` | `int` | no | Identity FK, `RESTRICT` |

Unique `(RenewalAgreementId, SourceAddendumSeriesPublicId)` proves every then-effective addendum was considered. Index `(PortfolioId, LeaseManagementId, RenewalAgreementId)` serves the execution validator. Composite scoped FKs bind the renewal and optional replacement Addendum to the same LeaseManagement. A check requires `ReplacementAddendumId` only for `ReissueAsAddendum`. Executing the renewal atomically sets the source Addendum's `SupersededEffectiveOn` to the renewal governing date for all three decisions; reissue additionally activates the linked replacement Addendum. Billing therefore cannot carry an old effect beyond a renewal by omission.

### 4.7 Signature packet replacement

Retain the existing concepts, but replace the lease FK and mutable duplicate status ownership.

#### `SignatureRequests`

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PublicId` | `uuid` | no | globally unique public envelope key |
| `PortfolioId` | `int` | no | RLS/scope |
| `LeaseAgreementId` | `int` | yes | exact legal parent |
| `LeaseAddendumId` | `int` | yes | exact legal parent |
| `Provider` | `varchar(50)` | no | native/remote provider namespace |
| `ProviderEnvelopeId` | `varchar(200)` | yes | provider identity |
| `IdempotencyKey` | `varchar(200)` | no | stable across retries |
| `State` | `varchar(30)` | no | `Prepared`, `Dispatching`, `AwaitingSignatures`, `Completed`, `Declined`, `Voided`, `DeliveryFailed` |
| `Subject` | `varchar(300)` | no | frozen signer-facing subject |
| `IssuedArtifactId` | `int` | no | immutable source artifact |
| `ExecutedArtifactId` | `int` | yes | immutable executed artifact; completion only |
| `PreparedAtUtc` | `timestamptz` | no | local issue transaction |
| `ProviderAcceptedAtUtc` | `timestamptz` | yes | remote/native acceptance |
| `CompletedAtUtc` | `timestamptz` | yes | all required signatures + artifact finalized |
| `DeclinedAtUtc` | `timestamptz` | yes | terminal decline |
| `VoidedAtUtc` | `timestamptz` | yes | packet void |
| `FailureCode` | `varchar(100)` | yes | bounded machine reason |
| `LastError` | `varchar(2000)` | yes | bounded operator detail |
| `ClaimOwner` | `varchar(200)` | yes | worker owner |
| `ClaimToken` | `uuid` | yes | fencing token |
| `ClaimExpiresAtUtc` | `timestamptz` | yes | DB-clock lease |
| `AttemptCount` | `int` | no | default 0 |
| `NextAttemptAtUtc` | `timestamptz` | yes | retry eligibility |
| `CreatedByUserId` | `int` | no | Identity FK, `RESTRICT` |

- exactly one of Agreement/Addendum FK is set;
- composite FKs bind it to the same portfolio;
- unique `(Id, PortfolioId)`, `PublicId`, `(Provider, IdempotencyKey)`, and partial `(Provider, ProviderEnvelopeId)` when non-null;
- executed artifact/completion are paired and allowed only in `Completed`;
- claim owner/token/expiry are all null or all present; terminal rows cannot retain a claim;
- candidate and terminal updates use DB-clock token-fenced SQL.

#### `SignatureSigners`

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PortfolioId` | `int` | no | RLS/scope |
| `SignatureRequestId` | `int` | no | scoped FK, `RESTRICT` |
| `AgreementSignerId` | `int` | yes | source Agreement signer snapshot |
| `AddendumSignerId` | `int` | yes | source Addendum signer snapshot |
| `NameSnapshot` | `varchar(200)` | no | frozen |
| `EmailSnapshot` | `varchar(320)` | no | frozen |
| `SigningOrder` | `smallint` | no | positive |
| `IsRequired` | `boolean` | no | frozen |
| `TokenHash` | `char(64)` | no | SHA-256; raw token never stored |
| `TokenExpiresAtUtc` | `timestamptz` | no | expiry |
| `State` | `varchar(20)` | no | `Pending`, `Viewed`, `Signed`, `Declined`, `Expired` |
| `ConsentGivenAtUtc` | `timestamptz` | yes | ESIGN/UETA consent |
| `ViewedAtUtc` | `timestamptz` | yes | first view |
| `SignedAtUtc` | `timestamptz` | yes | signature fact |
| `DeclinedAtUtc` | `timestamptz` | yes | decline fact |
| `SignatureType` | `varchar(20)` | yes | `Typed`, `Drawn` |
| `TypedName` | `varchar(200)` | yes | typed signature evidence |
| `DrawnSignatureStoredFileId` | `int` | yes | private immutable image evidence |
| `IpAddress` | `inet` | yes | evidence |
| `UserAgent` | `varchar(1000)` | yes | evidence |
| `CreatedAtUtc` | `timestamptz` | no | DB clock |
| `UpdatedAtUtc` | `timestamptz` | no | last atomic state transition |

- exactly one source signer FK is set and matches the packet parent kind;
- unique `(Id, SignatureRequestId, PortfolioId)`, `(SignatureRequestId, SigningOrder)`, `(SignatureRequestId, lower(EmailSnapshot))`, and `TokenHash`;
- signed requires consent, signature type/evidence, and signed timestamp;
- token resolution/transition is one atomic `UPDATE ... WHERE TokenHash = ... AND State IN (...) AND TokenExpiresAtUtc > clock_timestamp()`.

#### `SignatureAuditEvents`

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `bigint identity` | no | PK |
| `PortfolioId` | `int` | no | RLS/scope |
| `SignatureRequestId` | `int` | no | scoped FK, `RESTRICT` |
| `SignatureSignerId` | `int` | yes | signer event source |
| `Type` | `varchar(30)` | no | sent/viewed/signed/declined/completed/voided/delivery events |
| `OccurredAtUtc` | `timestamptz` | no | DB clock |
| `IpAddress` | `inet` | yes | evidence |
| `UserAgent` | `varchar(1000)` | yes | evidence |
| `Detail` | `varchar(2000)` | yes | bounded detail |

This table is append-only by trigger and indexed `(PortfolioId, SignatureRequestId, OccurredAtUtc, Id)`.

Partial unique indexes permit only one nonterminal packet per Agreement and per Addendum:

```sql
UNIQUE ("LeaseAgreementId")
WHERE "LeaseAgreementId" IS NOT NULL
  AND "State" IN ('Prepared','Dispatching','AwaitingSignatures');
```

and the equivalent Addendum index.

The Agreement/Addendum owns legal issue/execution facts. SignatureRequest owns delivery and signer workflow evidence. Completion writes both sides in one transaction; projections never choose between competing status columns.

### 4.8 Immutability triggers

Install database triggers, not only EF guards:

1. `protect_issued_lease_agreement()` rejects `DELETE` always and rejects changes to every contractual, lineage, template, payload, issued-artifact, signer-source, and term column when `OLD.IssuedAtUtc IS NOT NULL`. It permits only the explicit evidence allowlist: executed artifact/time, void fact/reason, supersession fact/link, and audit timestamp.
2. `protect_issued_lease_addendum()` applies the same rule.
3. signer/effect triggers reject child insert/update/delete after the parent issue fact.
4. legal artifact trigger rejects all updates/deletes.
5. transition checks reject clearing or moving an evidence timestamp backward and reject changing successor/void facts after first set.
6. issued rows may never be soft deleted because no such column exists.

Corrections therefore insert a new Agreement/Addendum version. They never modify the old legal terms or PDF.

## 5. Tenant account and append-only money

### 5.1 `TenantAccounts`

Exactly one account belongs to each LeaseManagement.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PublicId` | `uuid` | no | unique |
| `PortfolioId` | `int` | no | RLS |
| `LeaseManagementId` | `int` | no | unique scoped FK, `RESTRICT` |
| `AccountNumber` | `varchar(100)` | no | unique per portfolio |
| `Currency` | `varchar(3)` | no | fixed for account |
| `OpenedAtUtc` | `timestamptz` | no | created with Prepare move-in |
| `ClosedAtUtc` | `timestamptz` | yes | deliberate close only |
| `CloseReasonCode` | `varchar(40)` | yes | paired with close |
| `CloseNote` | `varchar(1000)` | yes | optional |
| `CreatedAtUtc` | `timestamptz` | no | DB clock |
| `CreatedByUserId` | `int` | no | Identity FK, `RESTRICT` |

- unique `(Id, PortfolioId)`, `PublicId`, `LeaseManagementId`, and `(PortfolioId, AccountNumber)`.
- currency must equal Portfolio currency at creation and never changes.
- close fields paired; closing command requires possession returned and a zero derived receivable/deposit balance after same-transaction adjustments/refunds.
- a deferred constraint trigger requires `TenantAccounts.ClosedAtUtc` and `LeaseManagements.AccountClosedAtUtc` to be equal or both null; neither fact can drift independently.
- no `Balance` or mutable `Status` column.

### 5.2 `TenantAccountConditionPeriods`

Payment plans and collections are durable effective periods; `Current` and `PastDue` remain derived from ledger facts.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PortfolioId` | `int` | no | scope |
| `TenantAccountId` | `int` | no | scoped FK |
| `Condition` | `varchar(30)` | no | `PaymentPlan`, `Collections` |
| `StartedAtUtc` | `timestamptz` | no | start |
| `EndedAtUtc` | `timestamptz` | yes | end |
| `Reason` | `varchar(1000)` | no | explanation |
| `CreatedByUserId` | `int` | no | actor |

- one open period per account/condition via partial unique index;
- no overlapping same-condition ranges via exclusion constraint;
- only the null `EndedAtUtc` may be set once; every other update and every delete is rejected.

### 5.3 `TenantLedgerEntries`

This replaces lease-tied `Payments` and `OpeningBalances`. It is the tenant receivable source of truth.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `bigint identity` | no | PK |
| `PublicId` | `uuid` | no | unique |
| `PortfolioId` | `int` | no | RLS |
| `TenantAccountId` | `int` | no | scoped FK, `RESTRICT` |
| `EntryType` | `varchar(40)` | no | `OpeningBalance`, `RentCharge`, `AddendumCharge`, `LateFeeCharge`, `DepositCharge`, `ManualCharge`, `PaymentReceipt`, `Credit`, `Adjustment`, `Refund`, `Reversal` |
| `Direction` | `varchar(10)` | no | `Debit` increases amount owed; `Credit` decreases it |
| `Amount` | `numeric(18,2)` | no | positive magnitude |
| `Currency` | `varchar(3)` | no | account currency |
| `EffectiveOn` | `date` | no | statement/accounting date |
| `DueOn` | `date` | yes | required for charge types |
| `PostedAtUtc` | `timestamptz` | no | append time |
| `Description` | `varchar(500)` | no | tenant-readable |
| `BusinessKey` | `varchar(200)` | no | command/schedule idempotency key |
| `LeaseAgreementId` | `int` | yes | legal provenance |
| `LeaseAddendumId` | `int` | yes | legal provenance |
| `ReversesEntryId` | `bigint` | yes | exact original for full reversal |
| `ProviderPaymentAttemptId` | `bigint` | yes | receipt/refund provenance |
| `SourceStoredFileId` | `int` | yes | scan/check/receipt evidence |
| `CreatedByUserId` | `int` | no | actor/system |

Constraints:

- `UNIQUE (Id, PortfolioId)`, `PublicId`, and `(TenantAccountId, BusinessKey)`.
- amount positive; currency matches account.
- debit types: opening balance may be either direction; charges are debit; payment/credit/refund are credit; reversal must be opposite its original.
- charge types require `DueOn`; receipt/credit/refund/reversal do not.
- addendum charge requires Addendum provenance; scheduled rent requires Agreement provenance.
- both provenance FKs must resolve to the same LeaseManagement as the account (deferred constraint trigger).
- `ReversesEntryId` unique so one full reversal cannot be posted twice. Trigger verifies same account/currency/amount and opposite direction.
- posted rows reject every update/delete. Corrections append adjustment or reversal entries.

Indexes:

- `(PortfolioId, TenantAccountId, EffectiveOn DESC, Id DESC)` for paged statements.
- `(PortfolioId, TenantAccountId, DueOn, Id) WHERE Direction = 'Debit'` for past due.
- `(PortfolioId, EntryType, EffectiveOn, Id)` for Engine candidates/reporting.
- `(PortfolioId, LeaseAgreementId, EffectiveOn)` and `(PortfolioId, LeaseAddendumId, EffectiveOn)` partial on non-null provenance.
- unique scheduled keys use deterministic forms such as `rent:{agreement-public-id}:{yyyy-MM}` and `addendum:{addendum-public-id}:{effect-id}:{period}`.

### 5.4 `TenantLedgerAllocations`

Allocation explains which credits settle which debits without changing account balance.

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `bigint identity` | no | PK |
| `PortfolioId` | `int` | no | scope |
| `TenantAccountId` | `int` | no | scoped FK |
| `DebitEntryId` | `bigint` | no | charge/opening debit |
| `CreditEntryId` | `bigint` | no | receipt/credit/refund credit |
| `Amount` | `numeric(18,2)` | no | positive normal allocation; negative compensating allocation |
| `ReversesAllocationId` | `bigint` | yes | exact prior allocation when amount is negative |
| `AllocatedAtUtc` | `timestamptz` | no | DB clock |
| `BusinessKey` | `varchar(200)` | no | idempotency |
| `CreatedByUserId` | `int` | no | actor/system |

- both entries must belong to the same account and have correct directions, enforced by deferred trigger.
- normal allocation is positive and does not over-allocate either entry after net compensating rows.
- reversal allocation is the exact negative of its referenced allocation and is unique per original.
- append-only trigger rejects update/delete.
- unique `(TenantAccountId, BusinessKey)` and index `(TenantAccountId, DebitEntryId, Id)`.

The open amount per charge is calculated DB-side as debit amount minus net allocations. Partial payment is therefore data, not a mutable `PaymentStatus.Partial` flag.

### 5.5 Provider payment attempts

Replace `PaymentTransactions` and lease-based `AutopayEnrollments`:

#### `TenantPaymentAttempts`

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `bigint identity` | no | PK |
| `PublicId` | `uuid` | no | globally unique |
| `PortfolioId` | `int` | no | RLS/scope |
| `TenantAccountId` | `int` | no | scoped FK, `RESTRICT` |
| `Provider` | `varchar(50)` | no | provider namespace |
| `ProviderObjectId` | `varchar(200)` | yes | provider payment/refund identity |
| `IdempotencyKey` | `varchar(200)` | no | stable command/provider key |
| `AttemptType` | `varchar(20)` | no | `Charge`, `Refund`, `Verification` |
| `State` | `varchar(30)` | no | `Prepared`, `Submitted`, `Succeeded`, `Failed`, `Canceled`, `Unknown` |
| `Amount` | `numeric(18,2)` | no | positive |
| `Currency` | `varchar(3)` | no | account currency |
| `PaymentMethodSummary` | `varchar(200)` | yes | non-secret display summary |
| `PayerName` | `varchar(200)` | yes | check/manual evidence |
| `CheckNumber` | `varchar(100)` | yes | check evidence |
| `BankName` | `varchar(200)` | yes | check evidence |
| `FailureCode` | `varchar(100)` | yes | bounded machine reason |
| `FailureReason` | `varchar(2000)` | yes | operator detail |
| `PreparedAtUtc` | `timestamptz` | no | DB clock |
| `SubmittedAtUtc` | `timestamptz` | yes | provider admission |
| `SettledAtUtc` | `timestamptz` | yes | terminal success time |
| `UpdatedAtUtc` | `timestamptz` | no | last fenced state change |
| `ClaimOwner` | `varchar(200)` | yes | worker owner |
| `ClaimToken` | `uuid` | yes | fencing token |
| `ClaimExpiresAtUtc` | `timestamptz` | yes | DB-clock expiry |
| `AttemptCount` | `int` | no | default 0 |
| `NextAttemptAtUtc` | `timestamptz` | yes | retry eligibility |
| `CreatedByUserId` | `int` | no | actor/system |

- unique `(Id, TenantAccountId, PortfolioId)`, `PublicId`, `(Provider, IdempotencyKey)`, and partial `(Provider, ProviderObjectId)` when non-null;
- amount positive, currency equals account, and claim fields are all null or all present;
- succeeded Charge/Refund must have exactly one `TenantLedgerEntry.ProviderPaymentAttemptId`; that ledger FK has a unique partial index, avoiding a circular attempt-to-ledger FK;
- provider state may advance only through token-fenced reconciliation. The resulting ledger receipt/refund, audit, outbox, and terminal attempt transition commit atomically;
- no API can edit the resulting posted ledger entry.

#### `TenantAutopayEnrollments`

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PortfolioId` | `int` | no | RLS/scope |
| `TenantAccountId` | `int` | no | scoped FK, `RESTRICT` |
| `AuthorizingPartyId` | `int` | no | responsible LeaseManagementParty, `RESTRICT` |
| `Provider` | `varchar(50)` | no | provider namespace |
| `ProviderCustomerId` | `varchar(200)` | no | encrypted/tokenized as provider requires |
| `ProviderPaymentMethodId` | `varchar(200)` | no | encrypted/tokenized as provider requires |
| `AuthorizationArtifactId` | `int` | yes | StoredFile/legal authorization evidence, `RESTRICT` |
| `EnrolledAtUtc` | `timestamptz` | no | start |
| `CanceledAtUtc` | `timestamptz` | yes | end |
| `CancelReason` | `varchar(500)` | yes | required with cancellation |
| `CreatedByUserId` | `int` | no | actor |

- one open enrollment per TenantAccount via partial unique `(TenantAccountId) WHERE CanceledAtUtc IS NULL`;
- deferred trigger proves the authorizing party is financially responsible, effective on enrollment, and belongs to the account's LeaseManagement;
- enrollment never moves to a renewal Agreement because it already belongs to the continuous account; changed legal authorization is a canceled enrollment plus a new row/evidence.

### 5.6 Security deposit subledger

Deposit funds are not mixed into receivable balance.

#### `SecurityDepositAccounts`

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `int identity` | no | PK |
| `PortfolioId` | `int` | no | scope |
| `TenantAccountId` | `int` | no | unique scoped FK |
| `OriginatingAgreementId` | `int` | no | provenance |
| `Currency` | `varchar(3)` | no | account currency |
| `CreatedAtUtc` | `timestamptz` | no | DB clock |
| `CreatedByUserId` | `int` | no | actor/system |

- unique `(Id, PortfolioId)` and `TenantAccountId`;
- composite FKs prove account and Agreement belong to the same LeaseManagement/portfolio;
- currency equals TenantAccount currency.

#### `SecurityDepositEntries`

| Column | Type | Null | Rule |
|---|---|---:|---|
| `Id` | `bigint identity` | no | PK |
| `PublicId` | `uuid` | no | unique |
| `PortfolioId` | `int` | no | RLS/scope |
| `SecurityDepositAccountId` | `int` | no | scoped FK, `RESTRICT` |
| `EntryType` | `varchar(30)` | no | `Receipt`, `Deduction`, `Refund`, `TransferIn`, `TransferOut`, `Adjustment`, `Reversal` |
| `Direction` | `varchar(10)` | no | `Increase` or `Decrease` |
| `Amount` | `numeric(18,2)` | no | positive magnitude |
| `Currency` | `varchar(3)` | no | account currency |
| `EffectiveOn` | `date` | no | holding/accounting date |
| `PostedAtUtc` | `timestamptz` | no | DB clock |
| `BusinessKey` | `varchar(200)` | no | idempotency |
| `Description` | `varchar(500)` | no | tenant/operator explanation |
| `LeaseAgreementId` | `int` | yes | legal provenance |
| `LeaseAddendumId` | `int` | yes | legal provenance |
| `ReversesEntryId` | `bigint` | yes | exact full reversal |
| `TenantLedgerEntryId` | `bigint` | yes | associated receivable/receipt/credit entry |
| `SourceStoredFileId` | `int` | yes | receipt/statement evidence |
| `CreatedByUserId` | `int` | no | actor/system |

- unique `PublicId` and `(SecurityDepositAccountId, BusinessKey)`;
- amount positive; currency/account and all provenance must share portfolio/LeaseManagement;
- `Receipt`/`TransferIn` increase, `Deduction`/`Refund`/`TransferOut` decrease, `Adjustment` may be either, and `Reversal` is exact opposite of its referenced entry;
- `ReversesEntryId` is unique; a trigger validates same account, currency, amount, and opposite direction;
- append-only trigger rejects update/delete; typed rows replace `DeductionsJson` and mutable returned totals/status;
- indexes `(PortfolioId, SecurityDepositAccountId, EffectiveOn, Id)` and partial Agreement/Addendum provenance indexes support statements/history;
- holding balance is sum(increase) - sum(decrease). Derived status is `NotFunded`, `Held`, `PartiallyReturned`, `Returned`, or `Withheld` based on entries plus relationship closure—not a mutable column.

Collecting a deposit atomically posts/allocates the tenant receipt and appends the deposit receipt. A deduction/refund command appends deposit entries, any tenant charge/credit, audit, and outbox in one transaction.

### 5.7 Exact foreign-key matrix

Every FK below is `ON DELETE RESTRICT` and includes `PortfolioId` in the child/parent key unless the parent is global Identity. Self/successor FKs are deferrable where the atomic transition inserts the successor and updates its predecessor in one transaction.

| Child columns | Parent columns |
|---|---|
| `Units(PropertyId, PortfolioId)` | `Properties(Id, PortfolioId)` |
| `LeaseManagements(UnitId, PropertyId, PortfolioId)` | `Units(Id, PropertyId, PortfolioId)` |
| `LeaseManagementParties(LeaseManagementId, PortfolioId)` | `LeaseManagements(Id, PortfolioId)` |
| `LeaseManagementParties(TenantId, PortfolioId)` | `Tenants(Id, PortfolioId)` |
| `TenantUserAccesses(LeaseManagementPartyId, PortfolioId)` | `LeaseManagementParties(Id, PortfolioId)` |
| `TenantUserAccesses(ApplicationUserId)` | `AspNetUsers(Id)` |
| `UnitOperationalPeriods(UnitId, PropertyId, PortfolioId)` | `Units(Id, PropertyId, PortfolioId)` |
| `UnitOperationalPeriods(SourceLeaseManagementId, UnitId, PortfolioId)` | `LeaseManagements(Id, UnitId, PortfolioId)` when source is non-null |
| `LegalDocumentArtifacts(StoredFileId, PortfolioId)` | `StoredFiles(Id, PortfolioId)` |
| `LeaseAgreements(LeaseManagementId, PortfolioId)` | `LeaseManagements(Id, PortfolioId)` |
| `LeaseAgreements(ReplacesAgreementId, LeaseManagementId, PortfolioId)` | `LeaseAgreements(Id, LeaseManagementId, PortfolioId)` |
| `LeaseAgreements(RenewsAgreementId, LeaseManagementId, PortfolioId)` | same Agreement alternate key |
| `LeaseAgreements(SupersededByAgreementId, LeaseManagementId, PortfolioId)` | same Agreement alternate key |
| Agreement template/artifact FKs | scoped `DocumentTemplates` / `LegalDocumentArtifacts` alternate keys |
| `LeaseAgreementSigners(LeaseAgreementId, PortfolioId)` | `LeaseAgreements(Id, PortfolioId)` |
| Agreement signer optional party/person FKs | scoped party/Tenant alternate keys |
| `LeaseAddenda(LeaseManagementId, PortfolioId)` | `LeaseManagements(Id, PortfolioId)` |
| `LeaseAddenda(BaseAgreementId, LeaseManagementId, PortfolioId)` | `LeaseAgreements(Id, LeaseManagementId, PortfolioId)` |
| Addendum predecessor/successor FKs | same Addendum series/management scoped alternate keys |
| Addendum template/artifact FKs | scoped template/artifact alternate keys |
| `LeaseAddendumSigners(LeaseAddendumId, PortfolioId)` | `LeaseAddenda(Id, PortfolioId)` |
| `LeaseAddendumFinancialEffects(LeaseAddendumId, PortfolioId)` | `LeaseAddenda(Id, PortfolioId)` |
| `LeaseRenewalAddendumDecisions(RenewalAgreementId, LeaseManagementId, PortfolioId)` | scoped Agreement key |
| renewal-decision source/replacement Addendum FKs | scoped Addendum keys under the same LeaseManagement |
| `SignatureRequests(LeaseAgreementId, PortfolioId)` | scoped Agreement key when non-null |
| `SignatureRequests(LeaseAddendumId, PortfolioId)` | scoped Addendum key when non-null |
| SignatureRequest artifact FKs | scoped LegalDocumentArtifact keys |
| `SignatureSigners(SignatureRequestId, PortfolioId)` | scoped SignatureRequest key |
| SignatureSigner source-signer FKs | matching Agreement/Addendum signer key |
| `SignatureAuditEvents(SignatureRequestId, PortfolioId)` | scoped SignatureRequest key |
| `SignatureAuditEvents(SignatureSignerId, SignatureRequestId, PortfolioId)` | signer key under same packet when non-null |
| `TenantAccounts(LeaseManagementId, PortfolioId)` | scoped LeaseManagement key |
| `TenantAccountConditionPeriods(TenantAccountId, PortfolioId)` | scoped TenantAccount key |
| `TenantLedgerEntries(TenantAccountId, PortfolioId)` | scoped TenantAccount key |
| Ledger Agreement/Addendum provenance FKs | scoped legal parent under Account's LeaseManagement (deferred trigger also proves relationship) |
| `TenantLedgerEntries(ReversesEntryId, TenantAccountId, PortfolioId)` | Ledger entry under same account |
| `TenantLedgerEntries(ProviderPaymentAttemptId, TenantAccountId, PortfolioId)` | payment attempt under same account |
| allocation account/debit/credit/reversal FKs | entries/allocations under the same TenantAccount |
| `TenantPaymentAttempts(TenantAccountId, PortfolioId)` | scoped TenantAccount key |
| `TenantAutopayEnrollments(TenantAccountId, PortfolioId)` | scoped TenantAccount key |
| `TenantAutopayEnrollments(AuthorizingPartyId, PortfolioId)` | scoped LeaseManagementParty key; deferred trigger proves same management |
| `SecurityDepositAccounts(TenantAccountId, PortfolioId)` | scoped TenantAccount key |
| `SecurityDepositAccounts(OriginatingAgreementId, PortfolioId)` | scoped Agreement key; deferred trigger proves same management |
| `SecurityDepositEntries(SecurityDepositAccountId, PortfolioId)` | scoped deposit-account key |
| Deposit provenance/reversal/ledger FKs | matching scoped legal/deposit/ledger keys |

Every new table also has Identity actor FKs for its `...ByUserId` columns with `RESTRICT`. The clean baseline must fail if EF attempts to replace any of these relationships with a cascade.

## 6. Read models and authoritative SQL

### 6.1 `vw_lease_agreement_status`

One row per Agreement, including portfolio business date, governing range, and derived `AgreementStatus`:

1. `VoidedAtUtc IS NOT NULL` -> `Void`.
2. `DraftCanceledAtUtc IS NOT NULL` -> `Canceled`.
3. `IssuedAtUtc IS NULL` -> `Draft`.
4. `FullyExecutedAtUtc IS NULL` -> `AwaitingSignatures` (packet delivery state is shown separately).
5. business date is on/after `SupersededEffectiveOn` -> `Superseded`.
6. business date is before `GoverningFromOn` -> `Upcoming`.
7. business date is within the half-open governing range -> `Active`.
8. otherwise -> `Expired`.

`IsGoverning` is true only for an executed, nonvoid agreement whose governing range contains business date. The exclusion constraint guarantees at most one. Time alone changes Upcoming -> Active -> Expired/Superseded; no worker writes status.

The blueprint's shorthand `SupersededAt` is refined into `SupersededEffectiveOn` plus `SupersessionRecordedAtUtc`. A future correction otherwise would require a midnight job—the stale-status failure the design is intended to remove.

### 6.2 `vw_lease_addendum_status`

One row per Addendum, using the same portfolio business date. Precedence is `Void`, `Canceled`, `Draft`, `AwaitingSignatures`, `Superseded`, `Upcoming`, `Active`, `Expired`. Addendum Active requires execution, nonvoid state, business date on/after `EffectiveFromOn`, before `EffectiveThroughOn + 1` when present, before `SupersededEffectiveOn` when present, and an executed base Agreement in the same relationship. It exposes financial effect count and whether any effect is currently billable. No worker writes Addendum status.

### 6.3 `vw_unit_occupancy`

Exactly one row per live Unit with:

- `PortfolioId`, `PropertyId`, `UnitId`;
- `IsOccupied` and `CurrentLeaseManagementId` from a possession range containing effective database time;
- `HasScheduledMoveIn`, `NextPlannedPossessionAtUtc`, and `PlannedLeaseManagementId` from the nearest uncanceled plan without possession;
- `IsInTurnover`, `IsOutOfService`, and `IsOnManagementHold` from open `UnitOperationalPeriods`;
- `HasGoverningAgreementWithoutPossession`;
- `HasPossessionWithoutGoverningAgreement`;
- `OccupancyExceptionCode` when either mismatch exists.

These are independent booleans, not one overloaded status. Occupancy percentages use only `IsOccupied`. Marketing, maintenance, and Summary may present the other facts together without changing occupancy truth.

Indexes listed above make the view's lateral nearest-plan/current-range lookups bounded. Property/portfolio occupancy uses `COUNT(*) FILTER (WHERE IsOccupied)` directly over this view in one SQL statement.

### 6.4 `vw_lease_management_lifecycle`

Derived lifecycle:

- canceled -> `Canceled`;
- account closed -> `Closed`;
- possession returned, account open -> `AccountingCloseout`;
- notice/planned move-out while possession open -> `Ending`;
- possession open -> `Occupied`;
- planned possession or executed upcoming agreement -> `Upcoming`;
- otherwise -> `Preparing`.

It projects current/upcoming Agreement IDs by joining `vw_lease_agreement_status`, current household counts/primary contact, TenantAccount ID, and reconciliation flags. It does not aggregate balance; record-home composes the separately indexed account-balance view in the same SQL query.

### 6.5 `vw_tenant_account_balances`

One row per account:

- total debits, total credits, receivable balance;
- unapplied credit;
- past-due amount/count from debit entries minus net allocations where `DueOn < business date`;
- next due date/amount;
- condition `Collections`, `PaymentPlan`, `PastDue`, `Credit`, or `Current` in that precedence;
- last receipt date/amount.

All sums/grouping run in PostgreSQL. No entity collection is materialized before aggregation.

### 6.6 `vw_security_deposit_balances`

One row per deposit account with total received, deductions, refunds/transfers, held balance, and derived status. It replaces JSON deductions and stored `DeductionsTotal`/`ReturnedAmount`.

### 6.7 `vw_tenant_charge_balances`

One row per debit entry with original amount, net allocations, open amount, and past-due flag. Tenant statements and late-fee/rent-reminder candidates page/filter this view server-side.

### 6.8 `vw_lease_reconciliation_exceptions`

Union of actionable contradictions that constraints cannot infer away:

- governing Agreement without possession after planned grace;
- possession without governing executed Agreement;
- returned possession with open turnover absent where required;
- closed account with nonzero receivable/deposit balance (defense-in-depth; command should reject);
- executed future successor missing an explicit addendum disposition;
- completed signature packet missing executed legal artifact;
- provider settlement without ledger receipt or ledger receipt without provider settlement where provider provenance exists.

Engine/UI candidate selection filters and pages this view DB-side.

## 7. Atomic command boundaries and order

Every command uses the atomic command kernel: one receipt/idempotency key, one canonical SHA-256 business-request fingerprint, one explicit transaction, DB-clock facts, audit rows, and outbox rows. The receipt is written in the same transaction as business state. Reusing the same command/key with a different business payload is rejected as a conflict after current-session replay authorization and before a stored result is returned. External I/O uses prepare/admit/finalize; it is never held inside a database transaction.

### 7.1 Prepare move-in

In one serializable or parent-row-locked transaction:

1. claim/validate operation receipt and actor capability/property scope;
2. lock Unit and reject conflicting possession/planned policy as applicable;
3. validate approved Application and selected Tenant records in scope;
4. insert LeaseManagement with plan facts;
5. insert effective party memberships, exactly one primary;
6. insert TenantAccount and optional SecurityDepositAccount;
7. insert initial Agreement draft, version 1, signer drafts, and terms;
8. mark Application handoff fact (not a legacy Lease FK);
9. append audit and required outbox/realtime work;
10. complete receipt and commit once.

No portal access begins implicitly. No charge is posted until its explicit command/policy says so.

### 7.2 Edit draft

Lock Agreement/Addendum row, require unissued/uncanceled, require `DraftRevision`, update draft terms/signers/effects, increment revision, audit, and commit. Issued rows fail in both command and trigger.

### 7.3 Issue agreement/addendum

Before the transaction, render/upload a pending PDF using the exact draft revision and operation key. Then one transaction:

1. lock draft and validate unchanged revision, terms, signers, template version, scope, and no open packet;
2. validate pending file actor/purpose/hash/length;
3. insert immutable LegalDocumentArtifact;
4. insert SignatureRequest in `Prepared` with provider idempotency key and signer delivery rows;
5. set issued artifact/time on parent (the freeze boundary);
6. consume pending upload, append audit/outbox, complete receipt, commit.

An outbox worker performs any remote provider call with the same idempotency key. Finalizing provider acceptance atomically records provider envelope/delivery state, audit, and delivery outbox. Failure leaves one frozen, retryable packet—not a mutable draft or duplicate packet.

### 7.4 Sign and fully execute

Each signer action atomically fences token use, records consent/signature/audit, and enqueues completion when all required signers are done. Executed PDF generation uses pending storage outside the transaction. Finalize transaction locks packet and parent, validates claim token plus pending artifact, inserts immutable artifact, sets packet completed and parent `FullyExecutedAtUtc`/artifact, applies any due legal transition/supersession facts, appends audit/outbox, and commits.

### 7.5 Correct/restated agreement

1. lock LeaseManagement and current Agreement;
2. insert next draft with copied terms and `ReplacesAgreementId`;
3. user changes only the new draft;
4. issue/sign normally;
5. execution transaction sets new `GoverningFromOn` and atomically sets old `SupersededEffectiveOn`, successor, and record time;
6. exclusion constraint proves periods do not overlap;
7. any retroactive financial difference posts explicit adjustments/reversals under separate typed ledger command—never rewrites old entries.

### 7.6 Renewal/month-to-month

Lock LeaseManagement/current governing Agreement, insert next version with `RenewsAgreementId`, record a decision for every effective Addendum, and issue/sign. The new Agreement may be executed while Upcoming. Its range must begin after the old natural/superseded governing range. TenantAccount, parties, payments, deposit, notices, and history remain under the same LeaseManagement.

A month-to-month Agreement has `TermType = MonthToMonth`, null term end, and still has a concrete governing start date. Ending it requires a later superseding agreement or LeaseManagement ending/possession-return path; no fake far-future date is stored.

### 7.7 Add/remove/change party

Lock LeaseManagement, end the old membership date, insert the new effective membership/role, validate primary/responsibility constraints and legal-document requirement, atomically change portal access when policy requires, audit/outbox, commit. An issued Agreement signer snapshot never changes.

### 7.8 Give/return possession and transfer

- Give possession locks Unit and LeaseManagement, validates executed governing Agreement or records an explicitly authorized reconciliation exception path, sets `PossessionGivenAtUtc`, audit/outbox, commit. Exclusion constraint closes the race.
- Return possession sets return fact, ends current resident memberships according to command input, starts Unit turnover, changes portal access policy, audit/outbox, commit. It does not close the account or delete legal/financial history.
- Transfer closes possession at old LeaseManagement and creates the destination LeaseManagement/account/agreement under the destination Unit in one deferred-constraint transaction. Money transfer is explicit paired ledger/deposit entries, never reassigned history.

### 7.9 Post charge/receipt/reversal/deposit

Lock TenantAccount and operation receipt; validate source Agreement/Addendum/account/scope; insert append-only ledger/deposit entries and allocations; finalize provider inbox/attempt when applicable; append audit/outbox; complete receipt; commit. Scheduled rent/fee candidates and deterministic business keys are selected/claimed DB-side.

### 7.10 Cancel planned relationship and close account

- Cancel requires no possession; sets LeaseManagement cancellation, cancels drafts, voids issued artifacts through explicit rules, revokes/retains access by policy, and resolves posted money with append-only refund/reversal commands in the same orchestration boundary.
- Close requires returned possession, no unfinished required workflows, and zero derived receivable/deposit balance after same-transaction final adjustments/refunds. It sets TenantAccount and LeaseManagement close facts together.

## 8. Exact destructive cutover

There is no legacy data to preserve. The implementation must not create copy SQL, nullable compatibility FKs, old/new discriminators, fallback reads, or temporary dual endpoints.

### 8.1 Database reset strategy

1. Finish the coherent Checkpoint 3 model in the isolated integration branch.
2. Delete the existing EF migration chain and model snapshot only at the planned foundation baseline-squash checkpoint, after all already-approved foundation tables are represented in the model.
3. Generate one clean PostgreSQL baseline containing extensions, new schema, RLS, triggers, views, and indexes.
4. Drop/recreate every development and test database. Seed only the new model.
5. Never ship an `INSERT ... SELECT` from an old lease/payment table. Never retain an old table renamed as `Legacy*`.

If implementation needs an interim local migration while the branch is under construction, it may drop old objects and create new ones for testing, but it is not a preservation migration and is squashed before merge.

### 8.2 Tables/entities deleted or replaced

Delete outright:

- `Leases` / `Lease`;
- `LeaseTenants` / `LeaseTenant`;
- `OpeningBalances` / `OpeningBalance`;
- current lease-tied `Payments` / `Payment` charge-receipt hybrid (retain application-fee income only by moving it to the appropriate application/operating-income model, not by keeping nullable Lease compatibility);
- current `SecurityDepositHoldings` JSON/mutable-total shape;
- current lease-keyed `PaymentTransactions` and `AutopayEnrollments` shapes;
- mutable `LeaseStatus`, `PaymentStatus`, `RentTrackingStartMode`, and `SecurityDepositStatus` source enums;
- `Lease` e-sign columns and `SignatureRequest.LeaseId`;
- `Unit.Status` as occupancy truth;
- every FK/index/query filter/view column that references old `LeaseId` or old `Payments`.

Replace old dependent FKs as follows:

| Current dependency | Replacement |
|---|---|
| `WorkOrder.LeaseId` | optional `LeaseManagementId`; Unit remains canonical operational context |
| `Appointment.LeaseId` | optional `LeaseManagementId` or Application context; never Agreement for ordinary appointment |
| `Inspection.LeaseId` | optional `LeaseManagementId`; optional Agreement only when inspection is contract-specific |
| `EvictionCase.LeaseId` | required `LeaseManagementId`, optional `LeaseAgreementId`, and explicit respondent party links |
| `NoticeDraft.LeaseId/TenantId/PaymentId` | `LeaseManagementId`, recipient party IDs, optional Agreement/Addendum/LedgerEntry provenance |
| `StoredFile EntityType='Lease'` | typed Agreement/Addendum/LeaseManagement document links; legal PDFs use LegalDocumentArtifact |
| `RentalApplication` move-in result | `PreparedLeaseManagementId` |
| conversations/portal | LeaseManagement plus effective party/access, not one primary Lease tenant |
| accounting transaction view | tenant ledger receipt/charge projections plus operational income/expense sources |
| reports/dashboard/unit dashboard | authoritative views in section 6 |

Historical migrations are not edited one-by-one. They disappear in the clean baseline.

The current nullable-lease `ApplicationFee` use of `Payments` is a cutover dependency, not a reason to retain `Payment`. The blueprint does not decide whether application fees belong to an Application subledger or general operating-income ledger. The TSK-621 application-fee slice must choose and land that canonical parent before Slice E drops `Payments`; this lease specification deliberately does not invent a second financial account model. The completion audit must prove application fee create/refund/report/provider paths use that new parent and that no nullable TenantAccount/Lease compatibility column was introduced.

### 8.3 Services/workers removed and replaced

Delete the mutable CRUD/status implementation rather than wrapping it:

- `LeaseService` create/update/delete/status-transition and `SyncUnitOccupancyAsync`;
- `LeaseController` broad POST/PATCH/DELETE contract mutations;
- generated-document/e-sign paths that mutate a Lease row;
- `PaymentService` charge/receipt reassignment and mark-status operations;
- `OpeningBalanceService` and current SecurityDeposit service;
- `RentChargeService`, `LateFeeService`, `AutopayChargeService`, and Lease expiry worker queries over old Lease/Payment state;
- current portal/autopay authorization through `Lease.TenantId`;
- scan confirmation writers that create old Lease/Payment rows;
- report/accounting projections over old Lease/Payment status.

Replace them with explicit commands and server-side projections. No generic `UpdateLeaseRequest` survives.

### 8.4 API replacement surface

Canonical management routes:

- `POST /api/v1/lease-managements/prepare-move-in`
- `GET /api/v1/lease-managements/page` and `GET /api/v1/lease-managements/{id}`
- `POST /api/v1/lease-managements/{id}/cancel`
- `POST /api/v1/lease-managements/{id}/give-possession`
- `POST /api/v1/lease-managements/{id}/return-possession`
- `POST /api/v1/lease-managements/{id}/close-account`
- `POST /api/v1/lease-managements/{id}/parties/change`
- `POST /api/v1/lease-managements/{id}/agreements`
- `PATCH /api/v1/lease-agreements/{id}/draft` (draft-only, revision required)
- `POST /api/v1/lease-agreements/{id}/issue`, `/void`, and `/execute` only through signature workflow
- `POST /api/v1/lease-agreements/{id}/renew`, `/correct`, `/restatement`, `/month-to-month`
- corresponding draft/issue/void routes for `/lease-addenda`
- `GET /api/v1/tenant-accounts/{id}`, `/entries/page`, `/charges/page`, `/deposit`
- explicit account commands `/charges`, `/receipts`, `/credits`, `/adjustments`, `/reversals`, `/refunds`, and deposit commands.

All list endpoints accept server-side search/filter/sort/page. Every response names `LeaseManagementId`, `LeaseAgreementId`, `LeaseAddendumId`, and `TenantAccountId` precisely; no ambiguous `LeaseId` remains.

The Agreement response contains derived `AgreementStatus` from the view and separate packet delivery state. The Unit response consumes `vw_unit_occupancy`; clients never calculate Active/Occupied from dates or status strings.

### 8.5 Web/mobile replacement implications

- Replace the flat mutable `Lease` client model with `LeaseManagementSummary`, `LeaseManagementDetail`, `LeaseAgreementSummary`, `LeaseAddendumSummary`, `TenantAccountSummary`, and paged ledger models.
- Global **Leases** search lists Agreement/relationship results with clear columns: household, Unit, relationship lifecycle, governing Agreement status, next agreement, and balance. Selecting one opens the Unit's route-backed **Tenant & lease** section while retaining a stable LeaseManagement detail route for deep links.
- Unit **Tenant & lease** shows parties, current/upcoming Agreement, version history, signatures, Addenda, ending decision, and prior LeaseManagement episodes. **Money** shows the continuous TenantAccount and deposit.
- Renewal/correction/month-to-month are named workflows that create a draft version; they are not edit forms over the current contract.
- Web routes use IDs in paths, not hidden tab-only state. Mobile keeps Unit contextual tabs/sections, bottom-sheet draft forms, steppers for Prepare move-in/renewal, FAB/context capture, global lists, search, and filters.
- Scan review targets explicit commands: Prepare move-in, create Agreement draft, attach/issue Agreement, create Addendum draft, post receipt/check, or attach evidence. Context carries Unit, LeaseManagement, Agreement, and TenantAccount separately.
- Tenant portal reads its authorized LeaseManagement, governing/history Agreements, TenantAccount statement, deposit, notices, and documents through `TenantUserAccess`; it does not infer ownership from one primary Tenant FK.
- Realtime entity names and navigation intents change in the same slice. Old `Lease`/`Payment` event names and old routes are deleted.

## 9. Verification matrix

All integration tests use PostgreSQL. SQLite/in-memory providers cannot prove exclusion constraints, RLS, triggers, views, DB clock, or translated queries.

### 9.1 Schema and immutability

- issued Agreement contractual column update fails at DB trigger;
- issued Addendum effect/signer update/insert/delete fails;
- issued/executed LegalDocumentArtifact update/delete fails;
- draft edit with stale revision loses cleanly;
- draft can be canceled, not deleted;
- issued mistake requires new version and preserves old artifact/hash;
- version insert skips/duplicates/concurrent next number: only the correct next version commits;
- cross-portfolio Unit/Tenant/Agreement/Addendum/Account FK attempts fail in PostgreSQL.

### 9.2 Governing agreements and time

- current fixed Agreement and nonoverlapping executed future renewal both commit;
- overlapping renewal attempts race: at most one commits;
- future correction truncates old governing range at matching boundary without a worker;
- immediate correction supersedes old and activates new atomically;
- time-only view transition Upcoming -> Active -> Expired uses portfolio timezone and simulated clock;
- month-to-month open range blocks overlapping executed successor unless supersession boundary is recorded;
- void/cancel precedence is correct;
- one LeaseManagement can retain all versions and Addenda.

### 9.3 Possession and lifecycle

- planned move-in does not count occupied;
- concurrent give-possession attempts for the same Unit: one commits;
- exact return/give boundary is allowed; true overlap fails;
- governing Agreement without possession and possession without Agreement produce exceptions, not contradictory occupancy percentages;
- possession return starts turnover but does not close TenantAccount;
- a later household after return creates a new LeaseManagement;
- Unit transfer atomically closes/opens possession under deferred constraints;
- Unit/Property/portfolio occupancy all consume the same view.

### 9.4 Parties and access

- concurrent/effective overlapping PrimaryTenant rows fail;
- the same person cannot have overlapping memberships in one relationship;
- role history answers who occupied/was responsible on a date;
- Agreement signer snapshots remain unchanged after party/contact edits;
- Occupant cannot receive financial/legal notice merely by residing;
- guarantor receives only permitted explicitly designated notices;
- tenant access cannot cross party/relationship/portfolio scope; revocation invalidates projections immediately.

### 9.5 Account, ledger, and deposit

- renewal/correction leaves Account ID, balance, deposit, and statement history unchanged;
- duplicate scheduled rent/addendum/fee business keys race: one entry commits;
- posted entry mutation/delete fails;
- full reversal must be exact/opposite/same account and cannot repeat;
- allocation cannot cross accounts, exceed debit/credit, or repeat reversal;
- partial receipt produces correct open charge and past-due amount without status mutation;
- provider success commits attempt, receipt, allocation, audit, outbox atomically; injected failure rolls all back;
- replay returns same receipt/result and never charges/posts twice;
- deposit collection/deduction/refund/reversal aggregate and derived status are correct;
- account close fails with open receivable/deposit balance and succeeds atomically after explicit resolution.

### 9.6 Signature/external recovery

- one open packet per artifact under concurrency;
- issue file admission failure leaves draft unissued and pending object recoverable/cleanable;
- provider timeout/retry uses the same idempotency key and packet;
- stale claim owner/token cannot finalize;
- claim takeover after DB-clock expiry succeeds;
- signer token replay fails;
- final signer action plus executed-artifact failure leaves no half-executed Agreement;
- completion retry recovers and commits parent/packet/artifact/audit/outbox once.

### 9.7 SQL/query proof

For each principal path inspect generated SQL and query count:

- Unit record home: one view/projection query, one bounded activity query, optional genuinely separate third query;
- LeaseManagement detail and Agreement history: server-filtered/sorted/paged;
- Tenant list/history, global Leases list, TenantAccount entries, charge balances, notices, renewal candidates, rent/late-fee candidates, occupancy reports;
- authorization scope is part of SQL; no allowed-ID materialization or per-row follow-up;
- aggregation/grouping/filtering/joins/sorting/paging are one translated statement or DB view;
- `EXPLAIN (ANALYZE, BUFFERS)` uses the named indexes at realistic cardinality;
- cross-scope decoy data never appears.

### 9.8 API, web, mobile, and scan proof

- every old `/leases` mutable endpoint and ambiguous `LeaseId` client field is absent;
- Prepare move-in, draft edit, issue/sign, correct, renew, month-to-month, Addendum, possession, payment/deposit, move-out, and close work on web and physical phone;
- global list and Unit entry point open the same record/command with context retained;
- search/filter/paging remain discoverable on web/mobile;
- scan from global and Unit context routes to the right typed command, shows extracted values, permits correction, and confirms atomically;
- sole landlord sees simple Unit-centered language; scoped roles see only authorized properties/actions;
- Back/deep links restore the correct Unit section without nested-tab ambiguity.

## 10. Phased implementation slices

Each slice is buildable within the integration branch, but old and new behavior never merge together. Heavy tests are serialized and may run on the Azure verification runner.

### Slice A — schema kernel and projections

- introduce enums/entities/configuration, `btree_gist`, composite scope keys, new tables, triggers, RLS, views, and PostgreSQL constraint tests;
- map all views as keyless/read-only EF projections;
- add SQL translation/query-count harnesses;
- do not expose old/new compatibility models.

### Slice B — Prepare move-in, parties, possession, and access

- implement Prepare/cancel/give/return/transfer commands with atomic receipt/audit/outbox;
- replace Unit occupancy/status consumption and Tenant lease-history queries;
- implement TenantUserAccess projection;
- prove concurrency and cross-scope boundaries.

### Slice C — Agreement/Addendum drafting, issue, and signature

- implement version builders, revisioned drafts, immutable artifact admission, packet dispatch/recovery, signer actions, completion, void, correction, renewal, month-to-month, and Addendum carry decisions;
- replace render/e-sign APIs and scan Agreement/Addendum targets;
- prove immutable triggers, time transitions, packet claims, and recovery.

### Slice D — TenantAccount, ledger, provider money, and deposit

- implement append-only entries, allocations, balance/charge/deposit views, provider attempts, autopay account ownership, scheduled billing, reversals/refunds, reconciliation, and close rules;
- replace Payment/OpeningBalance/SecurityDeposit APIs, reports, accounting projection, portal statement, and scan payment/check targets;
- prove replay/rollback/duplicates/claim takeover and DB-side query plans.

### Slice E — dependent domains and API/client cutover

- replace WorkOrder/Appointment/Inspection/Eviction/Notice/document/application dependencies;
- replace global and Unit route models on web/mobile while preserving search, filters, tabs, FABs, bottom sheets, steppers, and scan context;
- delete old entities, enums, services, workers, routes, DTOs, client models, seeds, and tests;
- recreate databases from the clean baseline.

### Slice F — adversarial completion audit

- repository-wide searches prove no old Lease/Payment ownership or Unit occupancy status survives;
- run full PostgreSQL matrix, generated-SQL/query plans, role/scope decoys, browser proof, and physical-phone proof;
- confirm old migrations/routes/realtime names/screenshots/docs are gone or rewritten;
- merge only after the clean replacement is coherent.

## 11. Resolved ambiguities and remaining approval questions

### Resolved in this contract

1. **A draft can coexist with a current Agreement.** It has no governing range until fully executed, so future renewal/correction preparation does not affect current terms.
2. **Agreement status is not the latest event.** The view uses issue, execution, governing dates, void, and supersession facts in portfolio business time.
3. **Future supersession is a date plus record timestamp.** This removes the need for a midnight status writer.
4. **Term dates and governing dates are distinct.** Corrections can preserve original contract term dates while governing prospectively.
5. **TenantAccount is continuous only inside one LeaseManagement.** Unit transfer/new household opens a new account; explicit ledger transfers preserve provenance.
6. **The four party roles are sufficient.** Signer type/order is an artifact snapshot, not another household role taxonomy.
7. **Deposit is a subledger, not JSON or a mutable total.** Receivable and held-funds balances cannot contaminate each other.
8. **Legal PDFs use immutable artifact identities.** Generic string attachments are not authoritative legal storage.
9. **Canceled drafts need a derived `Canceled` state.** Without a cancellation fact the blueprint's “drafts may be canceled” requirement would display them forever as Draft.
10. **No broad delete/cascade path exists.** Development resets recreate the database; business history ends through facts and reversals.

### Approval questions that change product policy, not schema integrity

These do not block implementing the structural schema, but the owning workflow must settle them before its UI/API slice:

1. **Possession without execution:** should Workspace Administrators be allowed to record possession with a required exception reason, or must the command always require a governing executed Agreement? This contract supports a visible exception but recommends a tightly capability-gated override for real-world paper/import situations.
2. **Post-move-out tenant portal retention:** how long may a former tenant retain read-only statements/documents after membership ends? `TenantUserAccess` supports explicit revocation/retention; policy duration remains to be chosen.
3. **Responsible-party replacement threshold:** the blueprint says a wholly replaced household opens a new LeaseManagement. The exact workflow test—e.g., replacement of PrimaryTenant plus all CoTenants versus an explicitly confirmed “same household/account” amendment—requires product wording. The command must require an explicit choice and never infer solely from count.
4. **Multiple deposit buckets:** this contract defines one security-deposit subaccount per TenantAccount. Jurisdictions or products that require independently held pet/key deposits may require typed deposit buckets. Adding a `DepositType` and unique `(TenantAccountId, DepositType)` now is low-cost if that is a near-term requirement.
5. **Agreement numbering policy:** versions are strictly database-enforced, but the human `AgreementNumber` format (shared base number with `-V2` versus a new legal number per renewal) is workspace policy and should not be encoded in constraints.
6. **Application-fee financial parent:** the approved lease blueprint covers tenant-account money but not pre-tenancy application fees. TSK-621 must settle Application subledger versus operating-income ownership before the old `Payments` table is deleted. It must be a clean typed replacement, never a nullable Lease/TenantAccount bridge.

None of these questions justify retaining the old Lease row, mutable status, lease-keyed balance, or compatibility behavior.

## Appendix A — current-code replacement inventory

This is the minimum known inventory from the `deb73962` tree. Implementation must also use repository-wide searches because generated migrations, tests, reports, and knowledge-base pages contain additional old vocabulary.

### Domain and data

- delete/replace `RentalCommand.Core/Entities/Lease.cs`, `LeaseTenant.cs`, `Payment.cs`, `OpeningBalance.cs`, `SecurityDepositHolding.cs`, `AutopayEnrollment.cs`, and the lease-keyed shape of `PaymentTransaction.cs`;
- delete/replace `LeaseStatus.cs`, `PaymentStatus.cs`, `RentTrackingStartMode.cs`, and `SecurityDepositStatus.cs` as writable source state;
- replace Lease/Payment DbSets, configurations, filters, indexes, and navigations in `RentalCommand.Data/RentalCommandDbContext.cs` and the final model snapshot;
- replace `SignatureRequest.LeaseId` and native e-sign command/handler assumptions in `RentalCommand.Core/Esign` and `RentalCommand.Data/Esign`;
- replace string `StoredFile.EntityType = "Lease"` legal-document authority with typed links/artifacts;
- regenerate RLS policies and `vw_accounting_transactions` from the new scoped schema.

### API and Engine

- replace `LeaseController`, `LeaseDtos`, `ILeaseService`/`LeaseService`, `LeaseEsignService`, Agreement render/PDF services, `PaymentController`/`PaymentService`, `OpeningBalanceController`/service, and `SecurityDepositsController`/service;
- replace Lease assumptions in Tenant, Unit dashboard, Portal, WorkOrder, Appointment, Inspection, Eviction, Notice, Reports, Accounting, Schedule E, owner statement, daily briefing, QA, import, scanning, and demo seed services;
- replace old Lease/Payment target writers in `RentalCommand.Data/Scanning/ProductionScanConfirmationTargetWriter.cs`;
- delete/rewrite `RentChargeService`, `LateFeeService`, `AutopayChargeService`, `LeaseExpiryReminderService`, and their workers against DB-side Agreement/Account views and atomic claims;
- update provider inbox/payment handlers so provider terminal state and Account ledger effects are one fenced atomic finalization.

### Web

- replace `web/src/lib/api/endpoints/leases.ts` and payments endpoint models;
- replace mutable Lease detail/form/status helpers and `web/src/lib/components/records/LeaseDetail.svelte`;
- replace Unit `LeaseTab.svelte`, `RentTab.svelte`, and `LedgerTab.svelte` with the binding Tenant & lease / Money projections without removing the approved contextual Unit navigation;
- replace `/leases`, `/leases/[id]`, portal Lease/payment pages, scan Lease review/prefill, document routes, and realtime invalidation names;
- rewrite tests that assert Lease status mutation, Lease-keyed payment labels, or old paths instead of preserving them.

### Mobile

- replace `mobile/lib/core/models/lease.dart` and `payment.dart`;
- replace `features/leases/leases_repository.dart`, lease detail/list/forms/ledger, payment repository/detail/record sheet, Tenant lease screens, portal account history, and scan Lease overrides;
- preserve mobile global lists, search/filter, Unit contextual navigation, FAB, bottom sheets, steppers, and scrollable tabs while changing the domain payloads and commands;
- remove update-status/edit-current-contract actions; add Prepare, draft version, issue, renew, correct, month-to-month, Addendum, possession, account, and deposit actions.

### Completion searches

At Slice F, searches over non-generated source must produce no old ownership/state implementation:

```text
class Lease
class LeaseTenant
DbSet<Lease>
LeaseStatus
PaymentStatus
RentTrackingStartMode
\.LeaseId\b
EntityType == "Lease"
EntityType = "Lease"
SyncUnitOccupancy
UnitStatus.Occupied
```

Any surviving `LeaseId` must be an intentional new `LeaseAgreementId`, `LeaseAddendumId`, or `LeaseManagementId` spelled in full. Test fixtures and documentation are rewritten to the same vocabulary; they are not exempted as “legacy.”
