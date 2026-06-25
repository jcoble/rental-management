# TSK-397 Pass 84 - Staff Tenant Notices Inbox

Date: 2026-06-25
Branch: `tsk-397-real-user-pass-84`
Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-397-full-ui-pass-26`

## Scope

Real-user verification of the staff-side tenant notices inbox at `/notices`.

Covered route and controls:

- `/notices`
- Notice status filter
- Generate drafts action and help popover
- Draft card summary, notice type labels, status badges, trigger dates, tenant/property/unit context
- Draft edit, save, cancel, and fair-housing check controls
- Portal/email/SMS channel checkboxes
- Approve & send action, fair-housing review, suggested rewrite, and conversation handoff
- Dismiss action and dismissed filter persistence

Deferred boundaries:

- Real email/SMS delivery is not clicked. Approval testing must use the Portal channel only.
- Production data, sensitive data, Plaid, connected QuickBooks/accounting providers, and final Go Live remain out of scope.

## Acceptance Criteria

- A staff user can open `/notices` and see existing local draft notices without a hard-error state.
- The status filter can switch between Draft, Dismissed, Approved, and All, with list contents matching the selected state.
- Generate drafts is visible, documented by help popover copy, and returns a clear success/no-new-drafts outcome without sending anything.
- Draft cards show readable notice type labels, tenant/property/unit context, subject/body, trigger date, status badge, channel controls, and edit/approve/dismiss actions.
- Editing a draft supports subject/body changes, cancel without persistence, and save with persistence after reload.
- Fair-housing check surfaces either reviewed clean/issues/unavailable state without crashing or losing the draft text.
- Unchecking all delivery channels disables approval; Portal-only approval is allowed.
- Fair-housing issue review can detect protected-class language, explain the concern, and apply a suggested rewrite without saving unsafe draft text.
- Approving a draft updates it to Approved, creates/links a conversation, and exposes `Open conversation`.
- Dismissing a different draft moves it to Dismissed and keeps it visible under the Dismissed filter.
- Clean final load has no browser console warnings/errors and no failed dynamic app requests outside deliberate provider-safe validation/gate responses.

## Evidence Log

Local stack:

- Web: `https://localhost:6042`
- API: `https://localhost:6041` (`http://localhost:6040`)
- DB: PostgreSQL container `rentalcommand-tsk397-pass26-db`, database `rentalcommand_tsk397_pass26_clean`, host port `5583`

Browser setup:

- Closed stale Playwright CLI daemon/browser processes before restarting the pass.
- Logged in with the local dev admin shortcut on `/login`; Dashboard loaded with the example-data safety banner.

Route and list coverage:

- `/notices` loaded with title `Tenant notices - Rental Command`, the staff shell, the example-data banner, and Draft notice cards for late rent, renewal, and move-out notices.
- Status filter options were present: `Draft`, `Approved`, `Dismissed`, and `All`.
- Draft view showed card type, tenant/property/unit context, reason summary, status badge, subject, body, trigger date, channel checkboxes, and `Edit`, `Approve & send`, and `Dismiss`.
- Dismissed view initially showed the empty state `No notice drafts in this view.`; All showed the available drafts.
- Screenshot proof: `output/playwright/pass84-notices-draft-list.png`.

Generate drafts:

- `Help for Generate drafts` opened the explanatory popover describing that the action scans leases for renewal/late-rent/move-out drafts, never sends automatically, and waits for review/edit/approve.
- `Generate drafts` completed without sending and showed `No new notice drafts needed.`
- Screenshot proof: `output/playwright/pass84-notices-generate-help.png`.

Edit, cancel, save, and fair-housing review:

- Editing James Wilson draft `9` exposed subject/body fields, `Save draft`, `Check for fair-housing issues`, help, and `Cancel`.
- Temporary subject/body edits were discarded by `Cancel`; the original saved content returned.
- Saved subject/body marker persisted after a new authenticated load:
  - Subject: `Final notice: overdue rent for Eastland 8-Plex Unit 6 - TSK397 Pass 84`
  - Body included `TSK397 Pass 84 edit proof.`
- Fair-housing check on unsafe test language returned `Possible fair-housing issues`, identified `families with children` as protected-class/familial-status language, and exposed `Use suggested rewrite`.
- Applying the suggested rewrite replaced the body and showed `Suggested rewrite applied. You can re-check it if you like.`
- Canceling after the rewrite restored the saved safe marker body, proving the unsafe test text was not persisted.
- Screenshot proof: `output/playwright/pass84-notices-fair-housing-issues.png`.

Channels and approval:

- Unchecking Portal, Email, and SMS disabled `Approve & send`.
- Re-checking only Portal left Email and SMS unchecked and re-enabled `Approve & send`.
- Approving James Wilson draft `9` with Portal only showed `Notice approved and sent.`, called `POST /api/v1/notices/9/approve => 200`, removed the card from Draft, and later showed it in Approved.
- Approved view rendered the edited Pass 84 subject/body and `Open conversation` linking to `/messages?conversation=3`.
- Opening the conversation loaded Messages, selected James Wilson, and showed the same subject/body with delivery tag `Portal`; no email or SMS delivery was selected for this approval.
- Screenshot proof: `output/playwright/pass84-notices-no-channel-disabled.png`, `output/playwright/pass84-notices-approved-filter.png`, `output/playwright/pass84-notices-conversation-handoff.png`.

Dismiss:

- Dismissing Jordan Smith draft `10` showed `Draft dismissed.`, called `POST /api/v1/notices/10/dismiss => 200`, and removed it from Draft.
- Dismissed filter showed `Late rent · Jordan Smith`, status `Dismissed`, the expected subject/body, and the original trigger date.
- Screenshot proof: `output/playwright/pass84-notices-dismissed-filter.png`.

DB proof:

- `NoticeDrafts.Id = 9` is `Approved`, tenant `James Wilson`, `NoticeType = LateRentNotice`, `ConversationId = 3`, `ApprovedChannels = Portal`, `ApprovedAt` set, `DismissedAt` unset.
- `NoticeDrafts.Id = 10` is `Dismissed`, tenant `Jordan Smith`, `NoticeType = LateRentNotice`, `DismissedAt` set, no `ConversationId`, no approved channels.
- `ConversationMessages.Id = 9` is in conversation `3`, `SenderRole = Landlord`, `Channels = Portal`, and its body prefix matches the approved James Wilson notice.

Final clean check:

- Final `/notices` reload returned route title `Tenant notices - Rental Command`.
- Playwright console proof: `Total messages: 2 (Errors: 0, Warnings: 0)` for both `console error` and `console warning`.
- Dynamic request proof: auth refresh, portfolios, sandbox-state, notifications, hub negotiate, notices, conversations unread count, appointments, approval, and dismiss requests returned `200`.

Status: Pass with documentation-only verification. No code fix or regression test was added in this slice. Deferred boundaries remain production/sensitive data, real SMS/email delivery, Plaid banking, connected QuickBooks/provider accounting, and final Go Live.
