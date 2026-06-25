# TSK-397 Pass 86 - Unit Renewal Next Action

Date: 2026-06-25
Branch: `tsk-397-429-real-user-pass-86-unit-renewal`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

## Scope

Real-user verification of the Unit Command Center renewal action for TSK-429, starting from the sanitized local production-like pass data.

Covered route and controls:

- `/units/19` Unit Command Center
- Unit lifecycle rail Renewal stage
- `Send renewal` / renewal next-best-action link
- Tenant notice draft modal
- Portal, Email, and SMS channel checkboxes
- Send button enabled/disabled states
- Messages conversation deep link after a renewal notice has been sent

Deferred boundaries:

- Production data, sensitive data, real email/SMS delivery, payment processing, Plaid banking, connected QuickBooks/accounting providers, and final Go Live remain out of scope.

## Acceptance Criteria

- A unit in the renewal window shows a renewal-stage action.
- Before a renewal notice exists, the action opens the tenant notice workflow for a `RenewalOffer`.
- The renewal notice modal pre-fills a relevant subject and body for the current tenant/unit/lease.
- At least one delivery channel is required before sending.
- Portal-only delivery is allowed in local example-data mode.
- Sending a renewal notice persists an approved `RenewalOffer`, creates a conversation, and writes the landlord message with the selected channels.
- After a renewal notice has already been sent, the Unit Command Center no longer offers another `Send renewal` action for the same current lease.
- The post-send action links to the sent conversation.

## Risk-Based Edge Cases Covered

- Current-state no-op check: the initial renewal action was clicked from the Unit page and did open the notice workflow.
- Channel guard: unchecking Portal, Email, and SMS disabled Send.
- Portal-only send: leaving only Portal checked allowed the notice to send.
- Duplicate-send prevention: after send, the Unit page rendered `Renewal sent - open conversation` instead of the stale `Send renewal` prompt.
- Conversation deep link: clicking the post-send action opened `/messages?conversation=4` with the renewal message selected.
- DB-side data rule: the new Unit Dashboard renewal notice lookup filters, orders, projects, and limits in SQL against `NoticeDrafts`; regression coverage asserts `ORDER BY` and `LIMIT` on the generated query.

## Evidence Log

Local stack:

- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Playwright/session hygiene:

- Found and shut down one stale Playwright CLI daemon and its temporary headless Chrome tree before continuing.
- Left normal Chrome, Codex browser extension host, Chrome Remote Desktop, and Claude daemon processes alone.

Current-state renewal action:

- Renewal-window unit found in DB: Eastland 8-Plex Unit 1, unit id `19`, tenant Kevin Brown, lease id `17`, lease end `2026-08-25`.
- `/units/19` initially rendered Renewal stage with `Send renewal - lease ends in 61 days`.
- Clicking the action opened `/tenants/19?action=create-notice&noticeType=RenewalOffer`.
- Modal opened as `Create / Send notice` with draft type `Renewal offer`.
- Subject: `Lease renewal for Eastland 8-Plex Unit 1`.
- Body offered a 12-month renewal through August 25, 2027.
- Screenshot proof: `output/playwright/pass86-unit-renewal-modal.png`.

Send behavior:

- Unchecked Email and SMS; Portal-only send remained available.
- Unchecked Portal as well; Send became disabled with no channels selected.
- Rechecked Portal and sent the renewal.
- Toast proof: `Notice sent.`
- DB proof after send:
  - `NoticeDrafts.Id = 4`
  - `NoticeType = RenewalOffer`
  - `Status = Approved`
  - `LeaseId = 17`
  - `TenantId = 19`
  - `ConversationId = 4`
  - `ApprovedChannels = Portal`
  - `ApprovedAt = 2026-06-25 06:42:56.169452+00`
  - `ConversationMessages.Id = 10`
  - `SenderRole = Landlord`
  - `Channels = Portal`

Post-fix Unit page behavior:

- After the API restart, `/units/19` rendered `Renewal sent - open conversation`.
- The action href was `/messages?conversation=4`.
- Clicking the action opened the Messages page with `Lease renewal for Eastland 8-Plex Unit 1` selected and the Portal-delivered renewal message visible.
- Screenshot proof: `output/playwright/pass86-renewal-message-thread.png`.

## Regression Coverage

Focused API regression tests were added in `RentalCommand.Api.Tests/Domain/UnitDashboardServiceTests.cs`:

- `GetDashboardAsync_LinksApprovedRenewalNoticeToConversation`
- `GetDashboardAsync_LinksDraftRenewalNoticeToReviewFlow`

Existing coverage continues to assert the no-notice-yet path:

- `GetDashboardAsync_LinksRenewalNextActionToTenantRenewalNoticeFlow`

Focused command:

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test RentalCommand.Api.Tests/RentalCommand.Api.Tests.csproj --filter FullyQualifiedName~UnitDashboardServiceTests -m:1 --no-restore
```

Result: passed, 6 tests.

## Status

Pass after fix. The original click target was functional on current main, but the Unit Command Center kept offering a stale duplicate renewal send after the renewal notice had already been approved/sent. The fixed behavior now routes sent renewals to the existing conversation and routes open renewal drafts to review.
