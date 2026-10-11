# Structure audit — 25 August 2026

Four independent read-only audits of the codebase for KISS/YAGNI violations: "two ways of doing
things", one-implementation abstractions, speculative generality, dead code, and over-large units.

| Report | Side | Reviewer |
|---|---|---|
| [backend-opus.md](backend-opus.md) | .NET backend | Opus 5 |
| [backend-sol.md](backend-sol.md) | .NET backend | SOL medium |
| [frontend-opus.md](frontend-opus.md) | web + mobile | Opus 5 |
| [frontend-sol.md](frontend-sol.md) | web + mobile | SOL medium |

Both reviewers per side agreed the architectures are sound and internally consistent. The waste is
in leftovers from finished migrations, helpers retyped instead of imported, dead files, and a few
speculative provider/resolver layers.

## Consolidated change list

Items both reviewers found independently are marked ★.

### Backend — applying now

| Lane | Item | Source | Risk |
|---|---|---|---|
| be-a | Rename 35 `*Handler(s).cs` → `*Rules.cs`; delete 2 forwarding shells ★ | O4, S3, S5 | low |
| be-a | Delete `WriteEntryPointAttribute`/`Kind` and `WriteIdempotencyPolicy` (single-valued) | O6, O7 | low |
| be-a | Delete `DateTimeNormalization`, `ShowMojo`, `IPaymentProvider`, `EnableNoticeAutopilot` | S1, S2, O9, O10 | low |
| be-a | Delete 17 `throw RetiredPath()` methods | S4 | medium |
| be-a | `LeaseManagementReadContext` → `WorkspaceReadScope` ★ | O5, S7 | low |
| be-a | `LedgerAccountCommands`: drop unread `RequestedNormalBalance` + compat ctor | S8 | medium |
| be-a | 9 hand-rolled address joins → `AddressComposer` | S10 | low |
| be-b | Delete ~60 dead `int portfolioId` read overloads (they skip `WhereAuthorized`) | O1 | medium |
| be-c | Collapse `IRequestWriteExecutor`/`IJobStepWriteExecutor` into `IWriteExecutor`; merge `ExecuteExactAsync` | O2, O3 | medium |
| be-c | Make the write executor a required ctor dependency; delete 61 `RequireWrites()` | O8 | medium |
| be-d | Delete `AccountingProviderResolver` and `ListingChannelAdapterResolver` (one impl each) | S12, S13 | medium |
| be-d | Normalise 22 stray controller error shapes onto `{ error }` | O11 | low |
| be-d | Collapse 7 `Where*Authorized` names into `WhereAuthorized` overloads | O12 | medium |

### Web — applying now

| Lane | Item | Source | Risk |
|---|---|---|---|
| web-a | Delete ~30 files nothing imports (`lib/api/index.ts` barrel, `ui/command/`, …) | O1 | low |
| web-a | Delete duplicate `PropertyCashFlow` + unused `accounting.cashFlow` ★ | O12, S12 | low |
| web-a | Delete dead `tenantAccounts.depositsPage`/`reverseEntry` | S7 | low |
| web-a | Merge duplicate `sentenceCaseIdentifier` | S13 | low |
| web-a | 6 hand-rolled authenticated downloads → `downloadFile` | S2 | medium |
| web-a | 11 hand-built query strings → `buildListQuery` | O13 | medium |
| web-a | Merge `lib/scans`→`lib/scan`, `lib/units`→`lib/unit`; rehome orphans | O10 | low |
| web-b | One currency formatter (32 inline `Intl.NumberFormat`) ★ | O3, S9 | medium — visible |
| web-b | 7 local date formatters → `utils/date` ★ | O4, S8 | low — visible |
| web-c | Merge `endpoints/payments.ts` into `tenant-money.ts` | S1 | high — money |
| web-c | 12 raw server `fetch` → `serverFetch` | O7 | medium — auth |

### Mobile — applying now

| Lane | Item | Source | Risk |
|---|---|---|---|
| mob-a | Delete 5 files nothing imports (incl. 779-line `guided_rental_flow.dart`) | O1 | low |
| mob-a | Delete dead `core/models/{expense,vendor}.dart` and the `hide Vendor` workarounds ★ | O6, S4 | low |
| mob-a | Merge `core/navigation/` into `core/router/` | O11 | low |
| mob-a | Delete `WorkOrderFormShell` (forwards to `TabbedFormSheet`) | S5 | low |
| mob-a | Delete one-impl `AccountingDetailModeStore` interface | S6 | low |
| mob-a | Delete production-only `ReviewNoticeSheetTestHarness` | S14 | low |
| mob-b | Promote `money_format.dart` to `core/presentation/`; delete ~29 money copies ★ | O2, S10 | medium — visible |
| mob-b | Collapse 13 month-name tables; 15 date helpers → `dateFmt` ★ | O4, S11 | low — visible |
| mob-b | Delete 3 byte-identical label-helper pairs | O5 | low |
| ~~mob-c~~ | One receipt command/model pair — **retired**: the lane's field-by-field diff (receipts/mob-c.md) showed the two clients send different payloads to the same endpoint (trimming/omission of six server-read strings, explicit vs defaulted `allocateOldestCharges`) and parse the result differently, so this is a divergence bug for TSK-1019, not a safe merge | S3 | — |

### Deferred — needs an owner decision or a larger plan

| Item | Source | Why deferred |
|---|---|---|
| Keep one SMS provider, delete three | O13 | Which vendor is live in production? |
| Drop the always-true `EnableRentCharges` column | S11 | Schema migration on a money setting |
| Remove `VendorDispatchStatus.Acknowledged` | S6 | Persisted enum; needs a DB check for stored rows |
| Let the global handler own `DomainValidationException` | S9 | Conflicts with O11 — 683 sites use `{ error }`; keeping that shape wins |
| Split `scan_review_screen.dart` (5,637 lines) and `scan/[draftId]/+page.svelte` (2,947) | O8, O9 | Flagship write path; one document type per PR, each verified in-app |
| 66 `Navigator.push` → go_router | O14 | Changes back-stack/return-value behaviour; per feature |
| Assistant intent parsing duplicated in both clients | O15 | Crosses the API contract |
| Add prettier to web | O-rule-5 | Reformats every file — its own PR |

### Rules worth adding to AGENTS.md (from both sides)

1. When a vocabulary migration renames classes, rename the files and delete the compat entry
   points in the same PR.
2. No enum, attribute, or config property ships with one legal value or zero readers.
3. No `IFoo? foo = null` constructor parameters for services that are always registered.
4. One formatter per concern per app, in `core`/`lib/utils` — never in a screen file.
5. No source-text assertion tests (`readFileSync`/`readAsStringSync` over source). 120 of 226
   web test files and 40 of 109 mobile test files do this today; it is the biggest tax on cleanup.
