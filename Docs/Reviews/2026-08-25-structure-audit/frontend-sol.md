# FRONTEND structure audit — SOL medium

The highest-return work is consolidating duplicate money clients and download logic, then deleting thin wrappers and dead models.

## Shape of the code

- Verified — web: 939 files, 119,675 lines; 411 Svelte files, 109 page routes, and 71 endpoint modules.
- Verified — web state is predominantly Svelte runes: 756 `$state` and 1,044 `$derived` matches.
- Verified — remote web state uses TanStack Query: 231 `createQuery` and 208 `createMutation` matches.
- Verified — mobile: 258 Dart files, 118,177 lines, 35 feature directories, and 41 repositories.
- Verified — mobile networking consistently injects the single `dioProvider` from `mobile/lib/core/api/dio_client.dart:16`.
- Verified — Riverpod uses 119 `FutureProvider`, 41 `NotifierProvider`, six `StateProvider`, and zero `StreamProvider` matches.
- Verified — mobile’s dominant form container is `TabbedFormSheet`: 27 construction sites across 22 files.
- Verified — shared formatting exists, but web still has 35 inline date-format expressions and 24 inline currency expressions.
- Verified — mobile’s `moneyFmt` has 68 references and `dateFmt` has 40, alongside multiple equivalent local helpers.

## Top changes, ranked by simplification-per-effort

### 1. Merge `payments` into `tenantMoney`

- Where: `web/src/lib/api/endpoints/payments.ts:69`, `web/src/lib/api/endpoints/payments.ts:141`, `web/src/lib/api/endpoints/tenant-money.ts:4`, `web/src/lib/api/endpoints/tenant-money.ts:131`
- Evidence: Verified — `rg -c "interface TenantMoneyCommandResponse" web/src/lib/api/endpoints --glob '*.ts'` finds three definitions. `rg -c "\\bpayments\\." …` finds two consumers; `rg -c "\\btenantMoney\\." …` finds five calls across four consumers. Both clients implement receipt, charge, reversal, and refund calls to the same routes.
- Why it is two-ways/over-built: `payments` and `tenantMoney` duplicate request/result types and four mutations. The smaller client uses raw `fetchApi`; the broader client uses the dominant `api.post` convention.
- Change: Keep `tenantMoney`. Move the refund-result helpers needed by `PaymentDetail.svelte` into that module, migrate the two `payments` consumers, then delete `payments.ts` and its barrel export.
- Size/risk: Assumed estimate — five files, ~170 lines net deletion, high because these are money write paths; one PR? yes.

### 2. Replace six hand-rolled authenticated downloads with `downloadFile`

- Where: `web/src/lib/api/client.ts:434`, `web/src/lib/api/endpoints/accounting.ts:136`, `web/src/lib/api/endpoints/import.ts:81`, `web/src/lib/api/endpoints/inspections.ts:119`, `web/src/lib/api/endpoints/securityDeposits.ts:143`
- Evidence: Verified — `rg -c "isTokenExpired\\(120\\)"` reports three blocks in `accounting.ts` and one each in `import.ts`, `inspections.ts`, and `securityDeposits.ts`. `rg -c "downloadFile\\(" web/src/lib/api/endpoints --glob '*.ts'` finds seven existing endpoint consumers of the canonical helper.
- Why it is two-ways/over-built: All six blocks repeat proactive refresh, token lookup, authenticated fetch, blob extraction, and error handling. `downloadFile` already supplies refresh-on-401 and structured API errors.
- Change: Call `downloadFile` in all six flows and leave only each flow’s filename/open/save behavior locally.
- Size/risk: Assumed estimate — five files, ~150 lines deleted, medium because authentication and downloads are involved; one PR? yes.

### 3. Use one mobile receipt command and model pair

- Where: `mobile/lib/features/payments/payments_repository.dart:387`, `mobile/lib/features/payments/payments_repository.dart:413`, `mobile/lib/features/payments/payments_repository.dart:642`, `mobile/lib/features/money/tenant_ledger_models.dart:317`, `mobile/lib/features/money/tenant_ledger_models.dart:544`, `mobile/lib/features/money/tenant_ledger_repository.dart:183`
- Evidence: Verified — `rg -c "Future<RecordTenantReceiptResult> recordReceipt"` finds one implementation in each repository. `rg -c "class RecordTenantReceipt(Input|Result)"` finds two duplicate classes in each model location. `rg -n "\\.recordReceipt\\(" mobile/lib` finds exactly two UI calls, one to each implementation.
- Why it is two-ways/over-built: The same receipt write has two input classes, two result classes, two serialization paths, and two repository methods. `TenantLedgerRepository` is the broader canonical home for tenant-account commands.
- Change: Keep the tenant-ledger models and `TenantLedgerRepository.recordReceipt`; migrate `payments_screen.dart` and delete the duplicate method and models from `PaymentsRepository`.
- Size/risk: Assumed estimate — four files, ~90 lines deleted, high because this is a money write path; one PR? yes.

### 4. Delete the dead core `Vendor` model

- Where: `mobile/lib/core/models/vendor.dart:1`, `mobile/lib/core/models/models.dart:10`, `mobile/lib/features/vendors/vendors_models.dart:6`, `mobile/lib/features/maintenance/create_work_order_sheet.dart:9`, `mobile/lib/features/maintenance/work_order_detail_screen.dart:10`
- Evidence: Verified — `rg -c "vendor\\.dart" mobile/lib/core/models/models.dart` returns one barrel export. The only direct core-model import is in `mobile/test/vendor_address_model_test.dart:2`. `rg -c "vendors_models\\.dart" mobile/lib/features mobile/lib/core --glob '*.dart'` finds seven production imports. Two consumers explicitly use `hide Vendor` to escape the collision.
- Why it is two-ways/over-built: The feature model is the production model and includes rating/scorecard fields. The core version has no production consumer and forces import exclusions.
- Change: Delete `core/models/vendor.dart`, remove its barrel export and test-only comparison, and remove the two `hide Vendor` clauses.
- Size/risk: Assumed estimate — five files, ~75 lines deleted, low; one PR? yes.

### 5. Delete `WorkOrderFormShell` and `WorkOrderFormTabSpec`

- Where: `mobile/lib/features/maintenance/work_order_form_shell.dart:5`, `mobile/lib/features/maintenance/work_order_form_shell.dart:14`, `mobile/lib/features/maintenance/create_work_order_sheet.dart:521`, `mobile/lib/features/maintenance/work_order_detail_screen.dart:2149`
- Evidence: Verified — `WorkOrderFormShell` merely forwards every property to `TabbedFormSheet`; `WorkOrderFormTabSpec` adds no fields or behavior to `TabbedFormStepSpec`. `rg -c "WorkOrderFormShell\\("` finds exactly two consumers, while direct `TabbedFormSheet` construction is dominant at 27 sites.
- Why it is two-ways/over-built: Readers must learn work-order-specific names that are behaviorally identical to the shared form components.
- Change: Use `TabbedFormSheet` and `TabbedFormStepSpec` directly in the two consumers, then delete `work_order_form_shell.dart`.
- Size/risk: Assumed estimate — three files, ~50 lines deleted, low; one PR? yes.

### 6. Remove the one-implementation `AccountingDetailModeStore` interface

- Where: `mobile/lib/features/money/tenant_ledger_view.dart:43`, `mobile/lib/features/money/tenant_ledger_view.dart:49`, `mobile/lib/features/money/tenant_ledger_view.dart:68`
- Evidence: Verified — `rg -c "implements AccountingDetailModeStore" mobile/lib --glob '*.dart'` returns one. `rg -n "AccountingDetailModeStore" mobile` finds no test fake or second implementation; the provider is consumed only for `read` at line 222 and `write` at line 235.
- Why it is two-ways/over-built: The interface adds a type and forwarding boundary without alternate runtime or test behavior.
- Change: Rename the concrete class to `AccountingDetailModeStore`, remove the interface and `implements`, and keep the provider typed to the concrete class.
- Size/risk: Assumed estimate — one file, ~15 lines deleted, low; one PR? yes.

### 7. Remove the dead deposit and reversal APIs from `tenantAccounts`

- Where: `web/src/lib/api/endpoints/tenant-accounts.ts:130`, `web/src/lib/api/endpoints/tenant-accounts.ts:212`, `web/src/lib/api/endpoints/tenant-accounts.ts:234`, `web/src/lib/api/endpoints/tenant-accounts.ts:236`
- Evidence: Verified — `rg "\\btenantAccounts\\.depositsPage" web/src … | wc -l` and `rg "\\btenantAccounts\\.reverseEntry" web/src … | wc -l` both return zero. The competing `securityDeposits.listPage/get` API has three production calls. Reversal is handled through `tenantMoney.reverseLedgerEntry`.
- Why it is two-ways/over-built: `tenantAccounts` duplicates deposit models/path construction and exposes an unused write method already owned elsewhere.
- Change: Delete `TenantAccountDeposit`, `buildTenantAccountDepositsPagePath`, `depositsPage`, `reverseEntry`, their now-unused request/result types, and the test that only references the deposit path builder.
- Size/risk: Assumed estimate — two or three files, ~60 lines deleted, high because one removed surface is a money command; one PR? yes.

### 8. Replace inline web date formatting with `utils/date`

- Where: `web/src/lib/utils/date.ts:16`, `web/src/routes/(protected)/scan/[draftId]/+page.svelte:300`, `web/src/routes/(protected)/banking/+page.svelte:155`, `web/src/routes/(protected)/accounting/+page.svelte:604`
- Evidence: Verified — `rg -l "utils/date'|utils/date\\\"" web/src … | wc -l` finds 28 files already using the shared module. `rg "new Date\\([^\\n]*\\)\\.toLocale(DateString|String)|new Intl\\.DateTimeFormat" web/src … | wc -l` finds 35 inline expressions across 28 files.
- Why it is two-ways/over-built: Date-only values need the UTC-pinned behavior documented in `formatDateOnly`; local formatting silently creates a second date contract. Timestamp and relative-time formatting are also repeatedly recreated.
- Change: Replace equivalent inline date-only, date-time, and relative displays with the existing shared functions. Preserve deliberately different compact/input formats.
- Size/risk: Assumed estimate — up to 29 files, ~80 lines removed, low; one small PR? no, use separate date-only and timestamp PRs.

### 9. Replace inline web currency formatting with `formatAccountingCurrency`

- Where: `web/src/lib/accounting/accounting-display.ts:66`, `web/src/lib/components/unit/money.ts:5`, `web/src/lib/components/records/PaymentDetail.svelte:83`, `web/src/routes/(protected)/banking/+page.svelte:148`
- Evidence: Verified — `rg "formatAccountingCurrency" web/src … | wc -l` finds 114 references. The equivalent inline `Intl.NumberFormat`/currency `toLocaleString` search finds 24 expressions in 24 files. A second shared USD-only helper also exists in `components/unit/money.ts`.
- Why it is two-ways/over-built: Currency symbols, negative-value placement, null handling, fraction digits, and non-USD behavior vary by local helper. One formatter already supports currency codes and invalid values.
- Change: Keep `formatAccountingCurrency`; replace equivalent local `money`/`formatCurrency` helpers and delete `components/unit/money.ts`’s formatting export once its consumers migrate. Preserve explicitly whole-dollar dashboard displays.
- Size/risk: Assumed estimate — roughly 25 files, ~100 lines deleted, high under the audit’s money-risk definition; one small PR? no, split accounting and non-accounting consumers.

### 10. Replace duplicate mobile display-money helpers with `moneyFmt`

- Where: `mobile/lib/features/money/money_format.dart:3`, `mobile/lib/features/maintenance/work_order_detail_screen.dart:45`, `mobile/lib/features/analytics/insights_screen.dart:10`, `mobile/lib/features/owner_reports/owner_reports_screen.dart:17`, `mobile/lib/features/leases/lease_ledger_view.dart:28`
- Evidence: Verified — `rg "\\bmoneyFmt\\(" mobile/lib --glob '*.dart' | wc -l` returns 68. The local-helper search finds 41 candidate `toStringAsFixed(2)`/money-helper expressions; inspection confirmed equivalent display implementations in maintenance, analytics, owner reports, lease ledger, owner landing, banking, and scan review.
- Why it is two-ways/over-built: Several helpers independently implement grouping, signs, decimals, and dollar placement. Input-controller serialization is a separate concern and should remain local.
- Change: Import `money_format.dart` for equivalent display values and delete those local helpers; do not replace form-field serialization or non-USD displays.
- Size/risk: Assumed estimate — seven to ten files, ~70 lines deleted, high because displayed money is affected; one PR? yes.

### 11. Replace equivalent mobile long-date helpers with `dateFmt`

- Where: `mobile/lib/features/money/money_format.dart:38`, `mobile/lib/features/maintenance/work_order_detail_screen.dart:27`, `mobile/lib/features/properties/property_detail_screen.dart:48`, `mobile/lib/features/leases/lease_ledger_view.dart:7`, `mobile/lib/features/deposits/deposits_screen.dart:22`
- Evidence: Verified — `rg "\\bdateFmt\\(" mobile/lib --glob '*.dart' | wc -l` returns 40. `rg "String (_fmtDate|_formatDate|_ownerDate|_shortDate)\\(" … | wc -l` finds 15 local functions, and the month-array search finds 18 arrays. The cited helpers all reproduce `MMM d, yyyy`.
- Why it is two-ways/over-built: Repeated month tables and identical interpolation obscure which date policy applies.
- Change: Replace only equivalent long-date helpers with `dateFmt`; retain ISO wire dates, numeric dates, short dates, and date-times.
- Size/risk: Assumed estimate — five to seven files, ~50 lines deleted, low; one PR? yes.

### 12. Delete the unused `accounting.cashFlow` endpoint

- Where: `web/src/lib/api/endpoints/accounting.ts:111`, `web/src/lib/api/endpoints/cash-flow.ts:47`, `web/src/lib/components/accounting/CashFlowPanel.svelte:62`
- Evidence: Verified — `rg "\\baccounting\\.cashFlow" web/src … | wc -l` returns zero, while `rg "\\bcashFlow\\.get" web/src … | wc -l` returns one production consumer. Both call `/accounting/cash-flow`.
- Why it is two-ways/over-built: Two endpoint modules expose the same read, but only the dedicated cash-flow module supports property filters and monthly detail.
- Change: Delete `accounting.cashFlow`; keep the `CashFlowSummary` type because `YearEndView` still embeds it.
- Size/risk: Assumed estimate — one file, ~5 lines deleted, low; one PR? yes.

### 13. Merge the duplicate `sentenceCaseIdentifier` implementation

- Where: `web/src/lib/accounting/money-display.ts:36`, `web/src/lib/accounting/accounting-display.ts:43`
- Evidence: Verified — `rg -c "sentenceCaseIdentifier"` returns three matches in each file: one definition and two calls. The implementations perform the same separator and case-boundary conversions.
- Why it is two-ways/over-built: Two accounting display modules independently maintain the same fallback algorithm.
- Change: Export the implementation from `money-display.ts`, import it in `accounting-display.ts`, and preserve each caller’s existing empty-value fallback.
- Size/risk: Assumed estimate — two files, ~15 lines deleted, low; one PR? yes.

### 14. Delete the production-only notice test harness

- Where: `mobile/lib/features/notices/create_tenant_notice.dart:493`, `mobile/test/notice_review_edit_test.dart:10`
- Evidence: Verified — `rg -n "ReviewNoticeSheetTestHarness" mobile` finds the definition, its internal references, and two test constructions only. It has no production consumer and duplicates the real subject/body/send UI.
- Why it is two-ways/over-built: Production ships an alternate notice-review widget solely so a test can exercise simplified duplicate behavior. Passing that test does not prove the real private sheet behaves identically.
- Change: Delete the 80-line harness and its duplicate-widget test. Retain direct tests of `noticeContentSafetyIssue`; test the real sheet only if its actual behavior later needs coverage.
- Size/risk: Assumed estimate — two files, ~150 lines deleted, low; one PR? yes.

## Patterns worth a single rule (not a code change)

- One route family has one endpoint client; extending the existing owner is preferred over creating a parallel client.
- Authenticated blobs always go through `downloadFile`; endpoint modules own only filename/open/save behavior.
- Display dates and money use platform-shared formatters; local formatting is limited to wire or form-input serialization.
- Do not create feature-specific aliases around a shared widget unless they add behavior.
- Production source must not export a second UI implementation solely as a test harness.

## Looked at and left alone

- Verified — `fetchApi` and `api` are justified layers: `api` supplies the dominant verb convenience, while `fetchApi` supports custom timeouts and low-level requests at `web/src/lib/api/client.ts:278`.
- Verified — browser `fetchApi` and server `serverFetch` are intentionally separate because server calls use absolute URLs and explicit access tokens (`web/src/lib/api/server-fetch.ts:42`).
- Verified — Riverpod `FutureProvider`, `NotifierProvider`, and `StateProvider` have distinct roles: parameterized reads, refreshable/mutable state, and small local selections respectively.
- Verified — `BiometricPlatform` is a real test seam with a fake in `mobile/test/biometric_auth_service_test.dart:100`.
- Verified — the 5,637-line mobile scan-review and 2,947-line web scan-review units cover several genuinely different document workflows; this pass found formatter duplication, but no honest delete/merge action that would simplify their domain behavior rather than merely move it between files.

## Not covered

- No exhaustive symbol-by-symbol zero-reference scan of all 1,197 frontend files; dead-code review was targeted by structural smell.
- Public marketing/docs pages, visual styling, accessibility, and route correctness were not audited.
- Shell/layout consistency was mapped but not deeply inspected across every management, owner, tenant, technician, and administrator surface.
- Provider semantics were sampled, not individually justified for all 166 Riverpod provider declarations.
- Tests were read only as dead-code or abstraction evidence. No compilation, installation, build, or test command was run.
