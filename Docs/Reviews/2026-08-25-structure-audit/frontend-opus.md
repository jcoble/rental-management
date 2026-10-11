# Frontend structure audit — Opus 5

## Shape of the code
- **Web** (`web/src`, 116,682 lines, 411 `.svelte` + 519 `.ts`): SvelteKit 5 runes only — `writable(`/`readable(` appear 0 times. One API client (`lib/api/client.ts`, 387 `api.*` calls) plus `lib/api/server-fetch.ts` for `+page.server.ts` loads. TanStack Query for server cache (`createQuery` in 122 files). 71 modules in `lib/api/endpoints/`. Feature logic in `lib/<feature>/*.ts` (pure functions) with UI in `lib/components/<feature>/`. 6 layout groups, 1 shell.
- **Mobile** (`mobile/lib`, 118,177 lines, 258 `.dart`): flat `features/<name>/*.dart` — 35 of 35 features, zero subdirectories. One `dioProvider`, one `ApiException` (141 importers), 42 `*Repository` classes. Riverpod `Notifier` + `FutureProvider`, hand-written, zero codegen. 275 hand-written `fromJson` factories. go_router with 35 routes.
- Both architectures are healthy and internally consistent. The waste is almost entirely in **presentation-layer helpers retyped instead of imported**, dead files nobody deleted, and two flagship screens that fan out per document type in one file.

## Top changes, ranked by simplification-per-effort

### 1. Delete the files that nothing imports — ~2,600 lines across both apps
- Where (mobile): `mobile/lib/features/scan/guided_rental_flow.dart` (779), `mobile/lib/features/home/more_tab.dart` (181), `mobile/lib/core/widgets/mobile_section_selector.dart` (92), `mobile/lib/features/scan/scan_tab.dart` (32), `mobile/lib/core/api/paginated.dart` (15).
- Where (web): `web/src/lib/api/index.ts`, `web/src/lib/utils/payment-labels.ts`, `web/src/lib/components/ui/command/` (12 files), `web/src/lib/components/m3/Chip.svelte`, `web/src/lib/components/m3/Card.svelte`, `web/src/lib/components/forms/TenantMultiSelect.svelte`, `web/src/lib/components/shared/ShortcutsHelp.svelte`, `web/src/lib/components/ui/{separator,slider,inline-field}/`, `web/src/lib/components/ui/StatusBadgeWithHelp.svelte`, `web/src/lib/components/ui/PageIntro.svelte`, `web/src/lib/stores/guidanceState.ts`.
- Evidence: Verified — `rg -c "from '\$lib/api'" web/src | wc -l` → 0 (while `$lib/api/endpoints/` is imported by 151 files); `rg -l "ui/command" web/src | grep -v components/ui/command | wc -l` → 0; `payment-labels` → only its own file; `guidanceState` → itself + `PageIntro.svelte`, itself dead. `MobileSectionSelector` → own file + `mobile/test/unit_navigation_test.dart`. `GuidedRentalFlow` → own file + two test files.
- Why: Three mobile files are referenced by tests **only as negative assertions** that they must not be used — `mobile/test/navigation_contract_test.dart:385` `isNot(contains('const MoreTab()'))`, `:671` `isNot(contains('GuidedRentalFlow.open'))`, `mobile/test/unit_navigation_test.dart:473`. The suite guarantees the code is unreachable and the files were left on disk anyway.
- Change: Delete all. Keep the negative test assertions.
- Size/risk: ~45 files, ~−2,600 lines, low. Two PRs (one per app).

### 2. Promote `money_format.dart` to `core/` and delete the other 29 mobile money formatters
- Where: `mobile/lib/features/money/money_format.dart:3` (`moneyFmt`, 20 importers); byte-identical copies at `owner_reports_screen.dart:18`, `insights_screen.dart:11`, `work_order_detail_screen.dart:45`; divergent copies at `deposits_screen.dart:17`, `tenant_detail_screen.dart:32`, `property_detail_screen.dart:34`, `units_list_screen.dart:522`, `unit_command_center_screen.dart:3761`, `applications_shared.dart:36`.
- Evidence: Verified — `rg -n "^\s*String _?(fmt|format)[A-Za-z]*\(" mobile/lib` → 47 local formatter definitions, ~11 money. `rg -l "NumberFormat" mobile/lib` → 0 — `intl` is not used at all. Exactly 4 files carry the same comma-grouping loop; 5 carry the round-to-whole variant.
- Why: Four mutually inconsistent outputs for the same dollar value — `$1,234.56`, `$1234.56`, `$1,235`, `$1234.56` bare. `property_detail_screen.dart` imports `money_format.dart` and still defines its own `_formatCurrency` at :34 that drops the cents.
- Change: Move `money_format.dart` to `mobile/lib/core/presentation/formatting.dart`, fold in `core/presentation/date_labels.dart` (26 lines, 1 importer), replace the other 29 with imports. Pick one money format.
- Size/risk: ~30 files, ~−450/+40 lines, medium — changes visible output; needs a look in the running app. One PR: yes, two commits.

### 3. Add one `web/src/lib/utils/money.ts` and delete the 32 inline currency formatters
- Where: `web/src/lib/accounting/accounting-display.ts:66` (`formatAccountingCurrency`, 114 refs), `web/src/lib/components/unit/money.ts:5` (`money`, 178 refs), `expense-receipt-display.ts:25` (`formatExpenseMoney`, 10), `assistant/actions.ts:9` (`formatAssistantMoney`, 7), `utils/status-labels.ts:12` (`currencyFormatter`), `data-grid/DataGrid.svelte:189`, plus 26 route/component files.
- Evidence: Verified — `rg -c "style: 'currency'" web/src` → 32 files each constructing their own `Intl.NumberFormat`. They disagree on rounding — `maximumFractionDigits: 0` at `routes/(protected)/+page.svelte:39`, `accounting/past-due/+page.svelte:52`, `leasing/LeasingListPage.svelte:64`, vs 2-decimal elsewhere.
- Why: Five named helpers and none won. The dashboard shows `$1,234` where the property page shows `$1,234.00`.
- Change: One exported `formatMoney(value, { whole?: boolean })`; replace all 32 and collapse the five helpers. Preserve each call site's fraction-digit intent.
- Size/risk: ~35 files, ~−250 lines net, medium. One PR: yes (currency only).

### 4. Collapse the 16 copies of the month-name table (mobile) and 7 local date formatters (web)
- Where (mobile): `_monthNames`/`_months` at `messages/message_detail_screen.dart:13`, `messages_list_screen.dart:24`, `appointments/appointments_shared.dart:24`, `tenants/tenant_detail_screen.dart:14`, `applications/applications_shared.dart:7`, `maintenance/work_order_detail_screen.dart:27`, `maintenance/work_order_timeline.dart:5`, `leases/lease_ledger_view.dart:7`, `money/money_format.dart:18`, `inspections/inspections_list_screen.dart:16`, `recurring_maintenance_list_screen.dart:12`, `properties/property_detail_screen.dart:48`, `core/presentation/date_labels.dart:1`.
- Where (web): `web/src/lib/utils/date.ts:15,87,93` is canonical (`formatDateOnly` 83 refs, `formatDate` 109, `formatRelative` 19); re-implementations at `shared/DocumentsPanel.svelte:120`, `admin/users/+page.svelte:79`, `owners-report/+page.svelte:142`, `maintenance/inspections/[id]/+page.svelte:372`, `leases/AgreementSignatureProgress.svelte:82`, `records/WorkOrderDetail.svelte:443`, `lease-templates/+page.svelte:183`.
- Evidence: Verified — 13 array definitions in mobile; `rg -l "DateFormat" mobile/lib` → 0. `rg -l "toLocaleDateString" web/src | wc -l` → 23. Mobile dates disagree: `Mar 3, 2026` vs `3/3/2026`.
- Change: Mobile — one table in the new `core/presentation/formatting.dart`. Web — point the 7 local functions at `lib/utils/date.ts`.
- Size/risk: ~22 files, ~−200 lines, low-medium. One PR per app.

### 5. Route mobile's 66 label helpers through `plainEnglishLabel`, starting with the exact duplicates
- Where: `mobile/lib/core/presentation/plain_english_labels.dart:42` (intended winner). Byte-identical duplicate pairs: `_effectTypeLabel` at `leases/addendum_action_sheets.dart:1016` and `leases/successor_agreement_sheet.dart:592`; `_loanPaymentStatusLabel` at `scan/scan_review_screen.dart:4157` and `properties/property_detail_screen.dart:2245`; `PaidOff → 'Paid off'` at `property_loan_form_sheet.dart:402` and `property_detail_screen.dart:2240`.
- Evidence: Verified — read all six; character-for-character same. `rg -n "String _?[a-zA-Z]*(Label|label)\(String" mobile/lib` → 33 definitions (63 incl. non-String arg); `rg -l "plainEnglishLabel" mobile/lib` → 5 files.
- Change: Delete the three duplicate pairs; convert pure `PascalCase → Sentence case` switches to `plainEnglishLabel` calls.
- Size/risk: ~10 files for duplicates (low, one PR); full sweep ~25 files, medium, separate PR.

### 6. Delete `core/models/expense.dart` and `core/models/vendor.dart` and the three `hide`/`show` workarounds they force
- Where: `mobile/lib/core/models/expense.dart`, `vendor.dart`, barrel `models.dart:3,10`; workarounds at `maintenance/create_work_order_sheet.dart:9`, `maintenance/work_order_detail_screen.dart:10` (`hide Vendor`), `recurring_maintenance_repository.dart:11`.
- Evidence: Verified — `rg -l "core/models/expense.dart" mobile/lib` → 0; same for `vendor.dart`, `appointment.dart`, `inspection.dart`, `portfolio.dart`. Live classes are `features/money/expense_models.dart:88` and `features/vendors/vendors_models.dart:6`.
- Why: Two classes named `Expense` and two named `Vendor`. The core ones are dead but still exported by the barrel, so live imports must `hide` them.
- Change: Delete both files, drop from barrel, remove `hide`/`show`. Also delete zero-reference `LeaseAgreementEffectiveAddendumSeriesItem` and `PropertyOwnership` in `core/models/`.
- Size/risk: ~5 files, ~−250 lines, low — compiler catches everything. One PR: yes.

### 7. Replace the 12 raw server-side `fetch(\`${SERVER_API_BASE_URL}…\`)` sites with `serverGet`/`serverPost`
- Where: `web/src/lib/api/server-fetch.ts:42,100,104` is the helper. Raw callers: `routes/login/+page.server.ts:71`, `register/+page.server.ts:74`, `forgot-password/+page.server.ts:33`, `reset-password/+page.server.ts:53`, `verify-email/+page.server.ts:25`, `logout/+page.server.ts:27`, `activate-team/+page.server.ts:28`, `(public)/docs/+page.server.ts:16`, `(public)/docs/+layout.server.ts:13`, `(public)/docs/[slug]/+page.server.ts:118-119`.
- Evidence: Verified — `SERVER_API_BASE_URL` in 27 files; `serverGet|serverPost|serverFetch` in 8 non-test files. ~12 raw vs 22 helper calls.
- Change: Extend `serverFetch` to accept "no token", convert the 12 sites. Leave the file-proxy `+server.ts` streams alone.
- Size/risk: ~10 files, ~−120 lines, medium (auth write paths). One PR: yes, with login/logout checked in the app.

### 8. Split `scan_review_screen.dart` (5,637 lines, 54 classes) by document type
- Where: `mobile/lib/features/scan/scan_review_screen.dart` — loan block `:4163`–`:4774` (~800 lines); lease block `_LeaseProposalSummary:3024` → `_LeaseTermsSection:4014` + `:4825`.
- Evidence: Verified — 54 top-level classes in one file. Three private formatters (`_formatMoney:4137`, `_formatDate:4150`, `_fmtMoney:5474`) and a duplicated field-widget pair `_PlainFieldInput:3914` / `_FieldInput:5037`.
- Why: A per-document-type fan-out (lease / loan / expense / applicant / line items) as sibling blocks in one file.
- Change: One sibling file per document type; merge `_PlainFieldInput` into `_FieldInput`; drop the private formatters in favour of #2.
- Size/risk: 1 file → 4–5, ~0 net lines, high (flagship write path). One PR: no — one document type per PR, verified in the running app.

### 9. Split `web/src/routes/(protected)/scan/[draftId]/+page.svelte` (2,947 lines) the same way
- Where: script ends ~line 1450; ~1,500 lines of markup with 90 `{#if}` blocks rendering 7+ document-type review forms inline; section comments at `:1715, :1763, :2059, :2099, :2181, :2415, :2567, :2577`.
- Evidence: Verified — largest web file. `onboarding/+page.svelte` (2,171) is second and already has a `lib/components/onboarding/` folder of 25 files to hold its steps.
- Change: One `<XReviewFields>` component per draft type under `lib/components/scan/`; move onboarding wizard steps into `lib/components/onboarding/`.
- Size/risk: 2 files → ~12, high (flagship flow). One PR: no — per draft type.

### 10. Merge `lib/scans` into `lib/scan`, `lib/units` into `lib/unit`, and rehome three orphan folders
- Where: `web/src/lib/scan/` (19 files) vs `lib/scans/` (13); `lib/unit/` (2) vs `lib/units/` (2); `lib/qa/` (1 test file), `lib/banking/` (1 test file), `lib/assets/` (`favicon.svg` only).
- Evidence: Verified — `ls` counts. `lib/qa/pass27-ui-regressions.test.ts` is a snapshot of one QA pass imported by nothing; `lib/scans` referenced by 2 files.
- Change: Merge `scans → scan`, `units → unit`; move the two orphan tests next to what they test; move `favicon.svg` to `web/static/`.
- Size/risk: ~18 files moved, import-path churn only, low. One PR: yes, pure rename.

### 11. Merge `mobile/lib/core/navigation/` into `mobile/lib/core/router/`
- Evidence: Verified — `core/navigation/` holds exactly one file (`mobile_restoration_state.dart`, 4 importers).
- Change: Move it into `core/router/`, delete the folder.
- Size/risk: 1 file + 4 import lines, low. Fold into #1.

### 12. Delete the duplicate `PropertyCashFlow` type and the unused `accounting.cashFlow` fetcher
- Where: `web/src/lib/types/index.ts:775` (7 fields) vs `web/src/lib/api/endpoints/cash-flow.ts:10` (9 fields); `endpoints/accounting.ts:112` (`cashFlow:`) vs `endpoints/cash-flow.ts:44` (`cashFlow.get`).
- Evidence: Verified — `comm -12` over exported interface names yields exactly one collision. Only live call is `CashFlowPanel.svelte:62` using `cashFlow.get`. The `accounting.ts` variant has zero call sites.
- Change: Delete `accounting.ts:112-113`; make `YearEndView.cashFlow` (`types/index.ts:829`) reference the `endpoints/cash-flow.ts` types and delete the `types/index.ts:775` copy.
- Size/risk: 2 files, ~−25 lines, low.

### 13. Route the 11 endpoint modules that hand-build query strings through `buildListQuery`
- Where: `web/src/lib/api/list-params.ts` (`buildListQuery`) vs `new URLSearchParams` in `endpoints/{cash-flow,lease-managements,notifications,reports,portal,team,banking,loans,accounting-books,tenant-accounts,accounting}.ts`.
- Evidence: Verified — `buildListQuery` 108 call sites; `new URLSearchParams` in 12 files under `lib/api`. `cash-flow.ts:35` re-implements it just to add repeated `propertyIds`.
- Change: Extend `buildListQuery` to take array values, then delete the 11 hand-rolled builders.
- Size/risk: ~11 files, ~−90 lines, medium (read paths for every list page). One PR: yes.

### 14. Convert the mobile `Navigator.push` sites that already have a declared route
- Where: `app_router.dart` declares 24 paths / 35 `GoRoute`s. Heaviest: `home_shell.dart` (8 pushes), `unit_command_center_screen.dart` (6), `leasing_landing_screen.dart` (6).
- Evidence: Verified — `Navigator.of(context).push` → 66; `context.go(` → 22, `context.push(` → 7. The 131 `pop` calls are correct.
- Why: A screen reachable at `/units/:id` is also pushed as an anonymous `MaterialPageRoute`, so deep-link and back-stack behaviour depend on how you arrived.
- Change: Convert only the pushes whose destination already has a `GoRoute`. Do not add routes for sheets.
- Size/risk: ~20 files, ~150 lines, medium. One PR: no — per feature, `home_shell.dart` first.

### 15. Delete the duplicated assistant intent-parsing logic from one of the two clients
- Where: `web/src/lib/assistant/actions.ts:1-15` and `mobile/lib/features/ai/ai_models.dart:3-40` — same five functions, same regexes, same switch arms.
- Evidence: Verified — read both. Identical regexes (`\b(log|record|add|create|save|enter|book)\b` / `\b(expense|receipt|bill|invoice|paid)\b`). Already drifted on output: web `$1,234.56`, mobile `$1234.56`.
- Why: A business rule maintained in two languages.
- Change: Have the assistant endpoint return the classification and delete both client copies; or delete the mobile copy and call the same endpoint.
- Size/risk: 2 client files + 1 API response field, ~−60 lines, medium (crosses the API contract). One PR: yes.

## Patterns worth a single rule
1. **No source-text assertion tests.** `readFileSync(` in 120 of 226 web test files; `readAsStringSync` in 40 of 109 mobile tests. They assert regexes over `.svelte`/`.dart` source. Every finding above will break dozens of them for reasons unrelated to behaviour. This is the single biggest tax on cleaning the frontend up.
2. **One formatter per concern per app, in `core`/`lib/utils`.** Never define `_formatCurrency`, `_formatDate`, or a month-name array in a screen file.
3. **Folder names are singular or plural, pick one and never both.**
4. **A screen file that exceeds ~1,200 lines gets split by the thing it fans out over.**
5. **Web has no formatter config at all** — no `.prettierrc`, no lint script. Add prettier and run it once.

## Looked at and left alone
- `mobile/lib/core/auth/auth_repository.dart:476` builds a second `Dio` — deliberate, interceptor-free so token refresh cannot recurse.
- `web/src/lib/api/server-fetch.ts` as a second API path — genuinely different problem (SSR loads).
- 275 hand-written `fromJson` factories in mobile — consistent; codegen would be a huge no-behaviour diff.
- Flat `features/<name>/*.dart` with zero subdirectories — the KISS-correct choice at this size.
- `web/src/lib/schemas/` (zod) alongside `lib/types/` — form-input validation vs response DTOs; 1 file uses zod.

## Not covered
- Nothing was built, run, or opened in a browser or simulator.
- `web/src/lib/components/{records,property,leases,leasing,notices,technician,marketing,settings}` counted but not read.
- `mobile/lib/features/{home,owner_portal,technician,voice,places,notices}` not opened beyond grep counts.
- The 42 mobile repositories and 71 web endpoint modules were not compared endpoint-by-endpoint.
- Whether `web/src/routes/activate-team/` is reachable (zero in-app links; may be email-only).
