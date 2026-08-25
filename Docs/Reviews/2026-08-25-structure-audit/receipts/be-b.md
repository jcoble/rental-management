# Lane be-b receipt

- Lane: be-b
- Start time: 2026-08-25T16:04:30-04:00
- Worktree: `/home/blackcolours/dev/work/worktrees/rental-management/simplify-be-b`
- Branch: `simplify-be-b`

## Work log

- Started audit and recorded the complete interface inventory.
- Removed confirmed-dead read overloads and ported affected API, engine, and integration tests to
  workspace-scope reads.
- Kept three `GetAsync` overloads whose implementations have live production read-back callers;
  kept the two named controller calls because their target services expose no scope overload.

## Item 1 — interface overload inventory

The following `int portfolioId` read declarations have a `WorkspaceReadScope` read counterpart in the
same interface. Line numbers are from the pre-change working tree. Each entry records the final
decision.

- `IApplicationService.cs:43` — `ListAsync` → `ListAuthorizedAsync` — **DELETE**.
- `IApplicationService.cs:50` — `ListPageAsync` → `ListPageAuthorizedAsync` — **DELETE**.
- `IApplicationService.cs:57` — `GetAsync` → `GetAuthorizedAsync` — **KEEP**; `ApplicationService.cs:375,408,420` use it for authorized write read-back.
- `IAppointmentService.cs:32` — `ListAsync` → `ListAuthorizedAsync` — **DELETE**.
- `IAppointmentService.cs:33` — `ListPageAsync` → `ListPageAuthorizedAsync` — **DELETE**.
- `IAppointmentService.cs:34` — `GetAsync` → `GetAuthorizedAsync` — **DELETE**.
- `ICapitalAssetService.cs:28` — `ListAsync` → `ListAuthorizedAsync` — **DELETE**.
- `ICapitalAssetService.cs:31` — `ListPageAsync` → `ListPageAuthorizedAsync` — **DELETE**.
- `ICapitalAssetService.cs:34` — `GetAsync` → `GetAuthorizedAsync` — **KEEP**; `CapitalAssetService.cs:109,125,141` use it for authorized write read-back.
- `IConversationService.cs:18` — `ListAsync` → `ListAuthorizedAsync` — **DELETE**.
- `IConversationService.cs:19` — `ListPageAsync` → `ListPageAuthorizedAsync` — **DELETE**.
- `IConversationService.cs:21` — `GetUnreadCountAsync` → `GetUnreadCountAuthorizedAsync` — **DELETE**.
- `IConversationService.cs:32` — `GetAsync` → `GetAuthorizedAsync` — **DELETE**.
- `IExpenseService.cs:19` — `ListAsync` → scoped `ListAsync` — **DELETE**.
- `IExpenseService.cs:21` — `ListPageAsync` → scoped `ListPageAsync` — **DELETE**.
- `IExpenseService.cs:32` — `GetAsync` → scoped `GetAsync` — **DELETE**.
- `IEvictionCaseService.cs:26` — `ListAsync` → `ListAuthorizedAsync` — **DELETE**.
- `IEvictionCaseService.cs:29` — `ListPageAsync` → `ListPageAuthorizedAsync` — **DELETE**.
- `IEvictionCaseService.cs:32` — `GetAsync` → `GetAuthorizedAsync` — **DELETE**.
- `ILoanService.cs:13` — `ListAsync` → scoped `ListAsync` — **DELETE**.
- `ILoanService.cs:15` — `ListPageAsync` → scoped `ListPageAsync` — **DELETE**.
- `ILoanService.cs:17` — `GetAsync` → scoped `GetAsync` — **DELETE**.
- `ILoanService.cs:27` — `GetPaymentsAsync` → scoped `GetPaymentsAsync` — **DELETE**.
- `INotificationService.cs:28` — `GetUnreadCountAsync` → scoped `GetUnreadCountAsync` — **DELETE**.
- `IOwnerDistributionService.cs:8` — `ListAsync` → scoped `ListAsync` — **DELETE**.
- `IOwnerDistributionService.cs:13` — `ListPageAsync` → scoped `ListPageAsync` — **DELETE**.
- `IOwnerDistributionService.cs:18` — `GetAsync` → scoped `GetAsync` — **DELETE**.
- `IPropertyDispositionService.cs:20` — `ListAsync` → `ListAuthorizedAsync` — **DELETE**.
- `IPropertyDispositionService.cs:23` — `ListPageAsync` → `ListPageAuthorizedAsync` — **DELETE**.
- `IPropertyDispositionService.cs:26` — `GetAsync` → `GetAuthorizedAsync` — **KEEP**; `PropertyDispositionService.cs:111,131` use it for post-write read-back.
- `IRecurringExpenseService.cs:13` — `ListAsync` → scoped `ListAsync` — **DELETE**.
- `IRecurringExpenseService.cs:15` — `ListPageAsync` → scoped `ListPageAsync` — **DELETE**.
- `IRecurringExpenseService.cs:17` — `GetAsync` → scoped `GetAsync` — **DELETE**.
- `IRecurringMaintenanceTaskService.cs:36` — `ListAsync` → `ListAuthorizedAsync` — **DELETE**.
- `IRecurringMaintenanceTaskService.cs:37` — `ListPageAsync` → `ListPageAuthorizedAsync` — **DELETE**.
- `IRecurringMaintenanceTaskService.cs:38` — `GetAsync` → `GetAuthorizedAsync` — **DELETE**.
- `ITenantService.cs:29` — `ListAsync` → `ListAuthorizedAsync` — **DELETE**.
- `ITenantService.cs:30` — `ListPageAsync` → `ListPageAuthorizedAsync` — **DELETE**.
- `ITenantService.cs:31` — `GetAsync` → `GetAuthorizedAsync` — **DELETE**.
- `IWorkOrderService.cs:32` — `ListAsync` → `ListAuthorizedAsync` — **DELETE**.
- `IWorkOrderService.cs:33` — `ListPageAsync` → `ListPageAuthorizedAsync` — **DELETE**.
- `IWorkOrderService.cs:39` — `GetAsync` → `GetAuthorizedAsync` — **DELETE**.

Not included because no same-read scope counterpart exists in the interface: the int reads in
`IBankingService`, `IConversationService` tenant-only reads,
`IDocumentService`, `ILeaseQaService`, `IListingWorkspaceService`, `INotificationFoundationService`,
`IPortalService`, `IPortfolioService`, `ISandboxService`, `IUnitDashboardService`,
`IUnitService`, and `IVendorDispatchService`. Writes and non-portfolio `int` parameters were not
considered.

## Item 2 — required production-caller checks

The required commands were run for each distinct listed method name. `ListAsync` had one dot-call,
`RentalCommand.Api/Controllers/DocumentsController.cs:272`, but it targets the excluded
`IDocumentService.ListAsync(int portfolioId, ...)`, which has no scope twin. `ListPageAsync`,
`GetUnreadCountAsync`, and `GetPaymentsAsync` had no matches. `GetAsync` reported only the two known
`GetPortfolioId()` controller calls plus `UnitController.cs:102`; those calls target the excluded
`IPortfolioService.GetAsync`, `IListingWorkspaceService.GetAsync`, and `IUnitService.GetAsync`
methods, which have no scope twin.

The required dot-call scans do not match same-class calls. The supplemental production scan found
the three retained read-back callers recorded in Item 1: `ApplicationService.cs:375,408,420`,
`CapitalAssetService.cs:109,125,141`, and `PropertyDispositionService.cs:111,131`. All other
listed overloads had no production caller after excluding test code. The old list/page chains in
the three retained classes were removed with their dead list/page overloads.

Evidence commands:

- `rg -n 'ListAsync\(\s*GetPortfolioId\(\)' RentalCommand.Api/Controllers` — exit 1, no matches.
- `rg -n '\.ListAsync\(\s*\w*[pP]ortfolioId' RentalCommand.Api RentalCommand.Engine --glob '*.cs'` — exit 0; only `RentalCommand.Api/Controllers/DocumentsController.cs:272`.
- `rg -n 'ListPageAsync\(\s*GetPortfolioId\(\)' RentalCommand.Api/Controllers` — exit 1, no matches.
- `rg -n '\.ListPageAsync\(\s*\w*[pP]ortfolioId' RentalCommand.Api RentalCommand.Engine --glob '*.cs'` — exit 1, no matches.
- `rg -n 'GetUnreadCountAsync\(\s*GetPortfolioId\(\)' RentalCommand.Api/Controllers` — exit 1, no matches.
- `rg -n '\.GetUnreadCountAsync\(\s*\w*[pP]ortfolioId' RentalCommand.Api RentalCommand.Engine --glob '*.cs'` — exit 1, no matches.
- `rg -n 'GetAsync\(\s*GetPortfolioId\(\)' RentalCommand.Api/Controllers` — exit 0; `PortfolioController.cs:41` and `UnitController.cs:135`.
- `rg -n '\.GetAsync\(\s*\w*[pP]ortfolioId' RentalCommand.Api RentalCommand.Engine --glob '*.cs'` — exit 0; `UnitController.cs:102`, `UnitController.cs:135`, and `PortfolioController.cs:41`.
- `rg -n 'GetPaymentsAsync\(\s*GetPortfolioId\(\)' RentalCommand.Api/Controllers` — exit 1, no matches.
- `rg -n '\.GetPaymentsAsync\(\s*\w*[pP]ortfolioId' RentalCommand.Api RentalCommand.Engine --glob '*.cs'` — exit 1, no matches.

Supplemental caller evidence:

- `git grep -n` for the old read method calls in `RentalCommand.Api` and `RentalCommand.Engine` —
  verified only the three retained service read-back groups above, the two named controller calls,
  and excluded no-scope services.

## Item 3 — implementation cleanup

The implementation deletion mirrors every **DELETE** decision above in the corresponding service
class. `EvictionCaseService.cs` also drops its now-unused `BaseQuery(int portfolioId)` helper;
`ExpenseService.cs`, `LoanService.cs`, and `RecurringExpenseService.cs` drop their unused int-based
list-query helper overloads. `InspectionService.cs` removes its internal int list/page/detail,
template, and report entry points; the template and report helpers now receive the already-validated
scope used by their authorized callers. `NotificationService.cs` drops its class-only int list
adapter and its deleted unread-count overload. No write method or read method without a scope twin
was changed.

## Item 4 — named controller sites

- **KEEP**, `RentalCommand.Api/Controllers/PortfolioController.cs:41`: `IPortfolioService.GetAsync`
  has no `WorkspaceReadScope` overload; converting would require an out-of-scope service contract
  change.
- **KEEP**, `RentalCommand.Api/Controllers/UnitController.cs:135`: `IListingWorkspaceService.GetAsync`
  has no `WorkspaceReadScope` overload; the existing capability check remains in place.

`UnitController.cs:102` was also left unchanged because `IUnitService.GetAsync` has no scope twin.

## Item 5 — tests ported

No behavioral test was deleted. The following tests were changed only to construct or reuse a
`WorkspaceReadScope` and call the surviving authorized read surface:

- `RentalCommand.Api.Tests/Domain/ApplicationServiceTests.cs` — application list/page reads.
- `RentalCommand.Api.Tests/Domain/AppointmentServiceListTests.cs` — appointment paging.
- `RentalCommand.Api.Tests/Domain/ConversationNotificationTests.cs` — landlord conversation reads
  and notification list/unread reads.
- `RentalCommand.Api.Tests/Domain/ExpenseServiceTests.cs` — expense list/page/detail reads.
- `RentalCommand.Api.Tests/Domain/InspectionChecklistServiceTests.cs` — inspection list/detail,
  template, and report reads.
- `RentalCommand.Api.Tests/Domain/LoanServiceTests.cs` — loan list/detail/payment reads.
- `RentalCommand.Api.Tests/Domain/OwnerDistributionAuthorizationTests.cs` and
  `OwnerDistributionServiceTests.cs` — owner-distribution list/detail reads.
- `RentalCommand.Api.Tests/Domain/RecurringExpenseServiceTests.cs` — recurring-expense reads.
- `RentalCommand.Api.Tests/Domain/RecurringMaintenanceTaskServiceTests.cs` — recurring-maintenance
  list/page reads; the existing shared scope also prevents duplicate fixture-user seeding.
- `RentalCommand.Api.Tests/Domain/TenantServiceTests.cs` — tenant list/detail reads.
- `RentalCommand.Api.Tests/Domain/WorkOrderCostsTimingAndProjectionTests.cs`,
  `WorkOrderServiceListTests.cs`, and `WorkOrderStatusTimelineTests.cs` — work-order list/detail
  reads.
- `RentalCommand.Engine.Tests/Automation/RecurringMaintenanceServiceTests.cs` — two API payload
  reads after automation runs.
- `RentalCommand.IntegrationTests/LeaseLifecycleApplicationPostgreSqlTests.cs` — application page
  read, with a valid administrator scope seeded for the existing actor.

The three retained application/capital-asset/property-disposition `GetAsync` overloads were not
ported in tests because they remain required by production read-back callers.

## Verification

Required commands were run sequentially in the specified order. The solution build was blocked by
the installed .NET SDK environment before compilation; the exact quiet-build output was:

```
Build FAILED.
    0 Warning(s)
    0 Error(s)

Time Elapsed: 00:00:01.47
```

- `MSBUILDDISABLENODEREUSE=1 dotnet build RentalCommand.sln -c Debug --nologo -v q` — exit 1;
  output above.
- `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests --no-build --nologo -v q` — exit 0;
  no output.
- `MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Core.Tests --no-build --nologo -v q` — exit 0;
  no output.
- `rg -c '\(int portfolioId' RentalCommand.Api/Services/Domain` — exit 0; pre-change total 133
  (`git grep -n -F '(int portfolioId' HEAD -- RentalCommand.Api/Services/Domain | wc -l`),
  post-change total 71 (the required command's per-file output totals 71). Final output ended with:
  `IListingWorkspaceService.cs:3`, `WorkspaceLlmCredentialService.cs:2`, `ISandboxService.cs:1`,
  `NotificationFoundationService.cs:9`, and `ApplicationService.cs:1`.
- `dotnet build-server shutdown` — exit 0; last lines were `MSBuild server shut down successfully.`
  and `VB/C# compiler server shut down successfully.`

`git diff --check` — exit 0.

## Commit

No commit was created. **Verified** `git add ...` failed with:
`fatal: Unable to create '/home/blackcolours/dev/work/rental-management/.git/worktrees/simplify-be-b/index.lock': Read-only file system`.
The worktree’s `.git` file points to that shared administrative directory, while the allowed write
root is only this worktree. **Verified** `git branch --show-current` reports `tsk-1018-be-b`; no
branch was created, renamed, or removed.
