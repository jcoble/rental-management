# TSK-747 Lane 2 Second Pass — Work, Communications, Admin, and Help

**Browser target:** `http://127.0.0.1:5667` using the Azure API
**Required viewport:** actual `window.innerWidth = 1710`, `window.innerHeight = 1107`
**Audit date:** 2026-07-25
**Build/test policy:** no builds or tests were run, per the controller's resource-safety instruction

## Material 3 sources and decisions

| Official source | Decision applied in this lane |
|---|---|
| https://m3.material.io/components/cards/overview | A card contains one subject. Keep low-emphasis outlined/filled surfaces and avoid showing every draft or policy as a fully expanded card. |
| https://m3.material.io/components/menus/overview | Menus are for temporary choices. Persistent filters and primary actions remain visible; the app's shared Select replaces native browser selects. |
| https://m3.material.io/components/date-pickers/overview | Keep date input attached to the shared date/date-time picker and show selected values in familiar, localized date and time formats. |
| https://m3.material.io/components/progress-indicators/overview | Use one consistent loading treatment for the same process and keep loading, error, and empty states distinct. |
| https://m3.material.io/components/text-fields/overview | Inputs retain visible labels, brief actionable helper/error copy, and an obvious interactive state. |
| https://m3.material.io/components/lists/overview | Lists stay short, logically ordered, and consistent. A notice row now exposes its identity and status first, with the long message disclosed on request. |
| https://m3.material.io/foundations/layout/canonical-examples/overview | Use list-detail for messages and supporting-pane/main-secondary hierarchy for dense operational detail. |
| https://m3.material.io/components/data-tables/overview | The official URL currently returns a Material 3 404 and the current component index has no Data tables page. Existing route tables therefore use the Lists and Layout guidance without changing the shared DataGrid. |

## Browser checklist

All successfully rendered screens below were checked for hierarchy, plain-English wording, navigation, component consistency, progressive disclosure, loading/error/empty states, horizontal overflow, and actual viewport size.

### Work and communications

- [x] `/maintenance` — rendered at `1710 × 1107`; valid server-paged list; no horizontal overflow.
- [x] `/maintenance/3` — valid work-order detail; replaced the native technician select and restored record title/status as the first hierarchy.
- [x] `/maintenance/18` — stale record evidence produced the work-order error state; no mutation occurred.
- [x] `/maintenance/inspections/1` — completed inspection; humanized type and date/time; no horizontal overflow.
- [x] `/maintenance/recurring` — empty state rendered; no horizontal overflow.
- [x] `/appointments` — calendar, list, and the existing staged edit flow rendered.
- [x] `/appointments/10` — valid appointment detail rendered.
- [x] `/vendors` — list rendered.
- [x] `/vendors/1` — valid vendor detail and scorecard rendered.
- [x] `/messages` — true empty split-pane state rendered.
- [x] `/messages/1` — no seeded conversation; route rendered the generic empty message shell.
- [x] `/notices` — two seeded drafts rendered as collapsed review rows; first row was expanded to prove subject, body, channels, edit, approve, and dismiss remain reachable.
- [x] `/settings/notifications/my-alerts` — Step 1 journey and saved summary rendered.
- [x] `/settings/notifications/team-routing` — Step 2 responsibility summaries rendered.
- [x] `/settings/notifications/tenant-notices` — Step 3 rendered the correct administrator-locked explanation for the signed-in assignment.
- [x] `/my-work` — role guard redirected the administrator to `/`.
- [x] `/my-work/1` — role guard redirected the administrator to `/`.
- [x] `/my-schedule` — role guard redirected the administrator to `/`.
- [x] `/assignment-inbox` — role guard redirected the administrator to `/`.
- [x] `/leasing/inbox` — role guard redirected the administrator to `/`.

### Admin

- [x] `/admin/users` — team member row loaded after the API request; no horizontal overflow.
- [x] `/admin/audit` — initial recovery state rendered. A later controller pass aligned
  this Platform Admin-only API with navigation and route authorization; the signed-in
  workspace admin now redirects to the permitted `/audit` activity page.

### Help index and every published article

- [x] `/docs`
- [x] `/docs/welcome`
- [x] `/docs/getting-started`
- [x] `/docs/how-scanning-works`
- [x] `/docs/properties-and-units`
- [x] `/docs/property-details`
- [x] `/docs/cost-basis-depreciation`
- [x] `/docs/tenants-and-applications`
- [x] `/docs/leases`
- [x] `/docs/lease-agreement-and-signing`
- [x] `/docs/notices`
- [x] `/docs/recording-payments`
- [x] `/docs/recording-expenses`
- [x] `/docs/accounting-overview`
- [x] `/docs/banking-and-reconciliation`
- [x] `/docs/security-deposits`
- [x] `/docs/taxes-and-1099`
- [x] `/docs/reports`
- [x] `/docs/scanning-documents`
- [x] `/docs/voice-notes`
- [x] `/docs/reviewing-drafts`
- [x] `/docs/maintenance-and-work-orders`
- [x] `/docs/inspections`
- [x] `/docs/appointments`
- [x] `/docs/mobile-overview`
- [x] `/docs/mobile-capture-by-photo`
- [x] `/docs/mobile-running-the-business`
- [x] `/docs/mobile-voice-commands`
- [x] `/docs/mobile-install-and-setup`
- [x] `/docs/daily-briefing`
- [x] `/docs/ask-your-portfolio`
- [x] `/docs/tenant-portal-overview`
- [x] `/docs/settings-and-notifications`
- [x] `/docs/ai-provider`
- [x] `/docs/notification-delivery-channels`

Every help screen rendered an H1 at `1710 × 1107` with no horizontal overflow. The operations article also received direct visual inspection of its left navigation, article column, and on-page navigation.

## Implemented fixes

1. **Work-order hierarchy and controls**
   - Moved the work-order identity/status ahead of assignment management.
   - Renamed “Technician responsibility” to “Who is handling this?”
   - Replaced the native `<select>` with the app's shared Select.
   - Renamed “Reason” to “Assignment note” with a plain prompt.

2. **Work-order and inspection language**
   - Replaced the Work Orders subtitle's “vendor execution” and “compliance checks” wording.
   - Humanized `MoveIn`, `MoveOut`, and `AnnualSafety`.
   - Removed seconds and used localized medium dates with short times.
   - Rewrote checklist-template help in plain English.

3. **Tenant-notice progressive disclosure**
   - Replaced the wall of full legal drafts with compact review rows.
   - Added an accessible Review draft / Hide draft control.
   - Kept one selected draft's body, channels, review, send, and dismiss actions available without changing persistence.

4. **Vendor and admin language**
   - Replaced “Avg response time” and “text-out to their DONE reply” with a plain explanation.
   - Renamed “Audit — forensic” to “Detailed activity log” and explained its purpose without internal terminology.

5. **Notification setup**
   - The lane's earlier pass implemented the shared three-step journey, saved summaries, contextual help, and one-policy-at-a-time disclosure across the three notification routes.

## Unresolved evidence

- Technician-only pages cannot be visually accepted with the signed-in administrator identity because they redirect to `/`. Their role guard should eventually show a permission-safe explanation rather than silently landing on Dashboard.
- `/leasing/inbox` also redirects to `/` for this identity.
- `/messages/[id]` has no seeded conversation, so the populated detail pane remains unproved. Its current missing-record behavior is a generic empty shell rather than a specific “conversation not found” state.
- Platform Admin-populated `/admin/audit` rows remain unproved because that role was
  unavailable. The later controller fix prevents ordinary workspace admins from reaching
  this route and redirects them to `/audit`; see the final UI audit for current status.
- A completed inspection is still necessarily long. The confirmed raw enum/date defects are fixed, but a separate implementation could default to failed/to-do items and disclose passed rooms on request.
- The Work Orders page still combines the primary grid with inspections and checklist templates. The route is now clearer, but a future URL-backed Work Orders / Inspections / Recurring organization would be a larger information-architecture change.

## Files changed in the second pass

- `web/src/lib/components/records/WorkOrderDetail.svelte`
- `web/src/routes/(protected)/maintenance/+page.svelte`
- `web/src/routes/(protected)/maintenance/inspections/[id]/+page.svelte`
- `web/src/routes/(protected)/notices/+page.svelte`
- `web/src/routes/(protected)/vendors/[id]/+page.svelte`
- `web/src/routes/(admin)/admin/audit/+page.svelte`

## Bounded control, pagination, and loading follow-up

The follow-up preserved every existing enum, date string, query parameter, paging offset, and API payload.

### Controls standardized

- `web/src/routes/(admin)/admin/users/+page.svelte`
  - Replaced four native Job / Access scope selects across the invitation and assignment editors with the shared Select.
  - The Team list and assignment dialog now use the shared in-content LoadingState.
  - Assignment pagination is visible even when the result fits on one page.
- `web/src/lib/components/technician/TechnicianAssignmentList.svelte`
  - Replaced the native status select with the shared Select.
  - Replaced both schedule date inputs with shared DatePicker controls.
  - Added an always-visible `start–end of total` range plus `Page n of n`, including the zero-result state.
- `web/src/routes/(protected)/my-work/[id]/+page.svelte`
  - Replaced the progress-status and field-entry native selects with the shared Select.
- Appointment editing continues to pass `type="datetime-local"` through shared `InlineField`, which renders the shared DateTimePicker rather than a browser datetime input.

### Loading and range coverage

- Added shared page loading states to appointment detail, vendor detail, inspection detail, tenant notices, Team, and detailed activity history.
- Existing Work Orders, Appointments, Vendors, and Recurring Work DataGrids retain their server-side pagination status.
- Tenant notices now show `Showing 1–n of n`.
- Messages now show `Showing n of total`, including `Showing 0 of 0 conversations`.
- Recurring Work shows `Showing start–end of total`, including its zero-result state.
- The embedded inspection and checklist-template lists show their visible result counts.
- Admin Team, Team assignments, and audit history show pagination on the first and only page.

### Browser HMR proof

- Rechecked owned administrator-accessible routes at an actual `1710 × 1107` inner viewport.
- `/admin/users` and the open Add team member dialog contained zero native `<select>`, date, or datetime-local inputs; the Team list displayed `Page 1 · 1 shown`.
- `/appointments/10` edit mode contained zero native `<select>`, date, or datetime-local inputs. It rendered two shared DatePicker inputs and two shared Select triggers.
- `/maintenance`, `/maintenance/inspections/1`, `/vendors/1`, `/notices`, `/messages`, and `/admin/audit` rendered without horizontal overflow and without native selects/date inputs.
- `/notices` displayed `Showing 1–2 of 2 notices`.
- Direct `/admin/audit` navigation as the supplied workspace admin redirects to `/audit`,
  matching the API's Platform Admin boundary.

### Exact remaining exceptions

- `web/src/routes/(protected)/maintenance/recurring/+page.svelte` retains one time-only `<Input type="time">`. There is no shared time-only picker, and wrapping it in DateTimePicker would invent a date and change the existing `HH:mm` API contract.
- Shared DateTimePicker intentionally contains a browser time input internally; shared Select and Calendar components also contain the expected library primitives internally.
- `web/src/routes/(protected)/my-work/[id]/+page.svelte` retains the native file input required to invoke the browser/phone photo chooser.
- Technician-only `/my-work`, `/my-work/[id]`, `/my-schedule`, and `/assignment-inbox` still redirect the signed-in administrator to `/`, so their updated visible states remain source-verified rather than role-authenticated browser acceptance.
- No build or test command was run in this follow-up.
