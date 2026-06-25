# TSK-397 Pass 93 - Unit Renewal Next Action Recheck

Date: 2026-06-25
Branch: `tsk-397-429-real-user-pass-93-unit-send-renewal`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

## Scope

- Task: TSK-429 - Fix Unit Command Center Send renewal no-op.
- Surface: Unit Command Center lifecycle rail.
- Routes: `/units/[id]`, `/tenants/[id]?action=create-notice&noticeType=RenewalOffer`, `/messages?conversation=[id]`.
- Controls: renewal next-best-action link, tenant notice dialog, notice delivery channel checkboxes.
- User/data: sanitized local production-like pass data.

## Acceptance Criteria

- A renewal-window unit renders a renewal-stage next action.
- The next action is a real browser link, not a button-only no-op.
- The link uses the API-provided `nextBestAction.href`.
- For an unsent or draft renewal, the href points to the tenant notice flow with `noticeType=RenewalOffer`.
- Loading that tenant URL opens the Create / Send notice dialog automatically.
- After a renewal has already been approved, the next action opens the existing conversation instead of offering a duplicate send.

## Risk-Based Edge Cases

- Unit has no renewal notice yet: action should say `Send renewal` and open the renewal notice workflow.
- Unit has a draft renewal notice: action should say `Review renewal draft` and open the same tenant renewal notice workflow.
- Unit has an approved renewal notice with a conversation: action should say `Renewal sent - open conversation` and open `/messages?conversation=[id]`.
- Link behavior must survive tab changes because the action lives outside the unit work tabs.
- The tenant page must parse only supported forced notice types before opening the dialog.

## Regression Coverage

Commands:

```bash
pnpm --dir web exec node --test --experimental-strip-types src/lib/components/unit/lifecycle-next-action.test.ts src/lib/tenants/tenant-notice-action.test.ts
```

Result: passed, 6 tests.

Existing API coverage remains in `RentalCommand.Api.Tests/Domain/UnitDashboardServiceTests.cs`:

- `GetDashboardAsync_LinksRenewalNextActionToTenantRenewalNoticeFlow`
- `GetDashboardAsync_LinksDraftRenewalNoticeToReviewFlow`
- `GetDashboardAsync_LinksApprovedRenewalNoticeToConversation`

Focused command:

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~UnitDashboardServiceTests -m:1 --no-restore
```

Result: passed, 6 tests. The run emitted the existing NU1903 warning for `SQLitePCLRaw.lib.e_sqlite3` 2.1.11.

## Browser Proof

Local stack:

- Web: `https://localhost:6042`
- API: `https://localhost:6041`

Current local data state:

- Eastland 8-Plex Unit 1 is already post-send and now renders `Renewal sent - open conversation` with href `/messages?conversation=4`.
- Short North Condo Unit 4B renders `Review renewal draft` with href `/tenants/18?action=create-notice&noticeType=RenewalOffer`.

Clicked result:

- Started on `/units/18`.
- Clicked `Review renewal draft`.
- Landed on `/tenants/18?action=create-notice&noticeType=RenewalOffer`.
- The `Create / Send notice` modal opened automatically.
- Modal type: `Renewal offer`.
- Draft subject: `Lease renewal for Short North Condo Unit 4B`.
- Portal, Email, and SMS channel checkboxes rendered.
- Screenshot before click: `output/playwright/pass93-unit-renewal-next-action-before.png`.
- Screenshot after click: `output/playwright/pass93-unit-renewal-next-action-after.png`.

## Outcome

Pass. The reported no-op did not reproduce on current main/current local data. Existing API coverage already protects the generated hrefs, and this pass adds a frontend contract test so the Unit page continues passing the dashboard action into a real link and the tenant route continues opening the forced renewal notice dialog.
