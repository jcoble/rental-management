# Web to Mobile Parity Ledger

Date: 2026-07-01
Task: TSK-614, refreshed under TSK-627
Baseline: `origin/main` at `5f60fdf1` (`PR #444`, parity ledger)
Mobile device reference: `SM-S906U`, installed current `main` release APK, tested over USB ADB serial `RFCT60SMJNN`

## Lens

The product premise is that mobile can do almost everything web can do. This audit is biased toward recent merges from 2026-06-28 through 2026-07-01, where web and mobile may have diverged.

Status key:

- **Covered**: mobile already has a real workflow.
- **Partial**: mobile has a subset, shortcut, or adjacent flow.
- **Missing**: no clear mobile surface.
- **Web-first OK**: acceptable to keep primarily on web unless product decides otherwise.

## Executive Decision List

| Priority | Area | Mobile status | Recommendation |
| --- | --- | --- | --- |
| P0 | Owners CRUD and owner management | Missing | Add mobile Owners list/detail/add/edit/delete-safe. Web has a full owners module; mobile only has owner reports and property owner selection. |
| P0 | Guided setup / import more | Broken on live phone | Fix the mobile Getting started status call first: current prod build reaches the screen but shows `ApiException(404)`. Then add the mobile-light setup path. |
| P1 | Property loans / mortgage management | Missing, phone-confirmed | Port loan list/add/edit/scan entry to mobile property detail. Amortization can be read-only or phase 2. |
| P1 | Activity history / per-record audit | Missing | Decide whether mobile needs Activity History. If yes, add a global screen plus lightweight per-record history on core details. |
| P1 | Vendor website field | Missing small field, phone-confirmed | Add `website` to mobile vendor model, form, payload, and display after reviewing whether the current vendor add/edit UI is the right pattern. |
| P2 | Lease template library/designer | Web-first OK | Keep the full PDF field designer on web. Consider mobile read-only template/default status later. |
| P2 | Bulk CSV/spreadsheet import and batch lease import | Web-first OK | Keep web-owned. Mobile can point users to web for large imports. |
| Done by phone pass | Turnover workspace, unit ledger/history, inspection run flow, appointment steppers, global FAB | Covered | No next-task action from this batch. Details are in the live phone walkthrough section. |

## Recent Merge Window

Recent merged work that drove the audit:

- `#443`, `#442`, `#440`: mobile quick action FAB / assistant actions.
- `#441`, `#427`, `#412`, `#434`: expense autofill, voice/capture, dense steppers.
- `#439`, `#386`, `#370`: lease notice actions, unit lease create, lease docs/status actions.
- `#438`: turnover workspace.
- `#437`, `#435`: appointment modal stepper on web.
- `#433`, `#432`, `#430`, `#429`, `#428`, `#424`, `#403`, `#401`, `#397`, `#395`, `#393`, `#391`, `#387`: guided setup/onboarding waves.
- `#431`: unit ledger tab and historical lease guardrails.
- `#421`, `#420`: inspection checklist editor and date fields.
- `#418`: Zillow listing handoff.
- `#417`: API query audit.
- `#416`, `#404`, `#399`, `#396`, `#394`, `#392`: web/mobile stepped form passes.
- `#389`: mobile tenant portal parity.
- `#385`, `#373`, `#371`: lease template preview/signing/template fixes.
- `#381`: loan scan validation.
- `#378`, `#377`, `#376`, `#372`: mobile lease signing/add-lease/multi-tenant lease work.

Open PRs still target older release branches or are stale-looking and were not counted as shipped on `main`.

## Live Phone Walkthrough Refresh

Run on 2026-07-01 using the user's `SM-S906U`. The app was rebuilt from current `main` with `FLAVOR=prod`, signed with the local upload key, installed over the existing app without clearing app data, and driven over USB ADB. Screenshots and UI dumps were captured under `/tmp/rc-mobile-parity/`.

Navigation note: Account > Browse all was used only as an audit inventory tool during this walkthrough. It should not be treated as a product recommendation. The user wants fewer duplicate paths because duplicate navigation will drift; primary domain tabs and contextual actions should carry parity.

Phone-confirmed findings:

- Global FAB: covered. Lower-right menu shows Chat, Record, Scan, and adds context-specific actions like New appointment and New inspection.
- Owners: missing. Rentals/Browse inventory shows Getting started, Properties, Units, Tenants, Leases, Applications, but no Owners module.
- Guided setup: reachable but broken in this prod phone build. Getting started opens and then shows `ApiException(404): The requested resource was not found.`
- Property loans: missing. Property detail shows owner, rental spaces, unit actions, and active leases, but no mortgage/loan section.
- Unit ledger: covered. Unit Ledger tab shows summary plus payment and operating-cost rows.
- Turnover: covered. Unit Turnover tab shows status, punch-list counts, make-ready plan, linked work orders, and New turnover task.
- Appointments: covered. Calendar exposes New appointment through the context FAB and opens a 3-step Details / Schedule / Contact form.
- Inspections: covered for create/run/edit-before-complete. New inspection opens a 3-step Checklist / Location / Schedule flow. Source confirms active inspections can patch item result/note, add photos, complete, and view a report; completed inspections are intentionally read-only.
- Vendors: CRUD present, but website missing. Edit Vendor opens a 3-step Contact / Address / Tax form; Contact includes name, service, email, and phone, not website. The user wants a separate review of this vendor add/edit UI before expanding it.
- Activity/Audit: missing. No Activity History or Audit destination appears under the primary tabs or Browse inventory; source also has no mobile audit/activity feature.

## Ledger

| Area | Web shipped on main | Mobile status | Decision |
| --- | --- | --- | --- |
| Mobile shell and domain nav | Web has broad routes; mobile shell has Rentals, Money, Work, Inbox, AI, Admin destinations. | **Covered, but simplify nav** | No parity action for coverage. Browse all duplicates primary nav and should be considered for removal once every destination has a primary path. |
| Global quick action FAB | Web has record/chat/scan affordances in app chrome. | **Covered, phone-confirmed** | No action from this parity batch. Phone shows Chat, Record, Scan, plus page-specific actions where appropriate. |
| Guided setup / onboarding | Web now has a first-class wizard with owner/property selectors, lease-first import, manual property/unit/tenant/lease steps, saved step links, settings deep-links, delete guardrails, and celebrations. | **Broken / Partial** | **Fix first.** Phone reaches Getting started but the setup-state request 404s. After that, keep heavy import/web teaching on web and add a mobile-light path for scan lease, simple manual steps, and saved progress. |
| Owner management | Web has `/owners`, `/owners/[id]`, owner create/edit/delete, structured address, documents, record history, and onboarding owner selectors. | **Missing, phone-confirmed** | **Port.** Add mobile Owners module. It is core data, not desktop-only. Mobile currently has owner reports and property owner picker, but no owner CRUD. |
| Property owner assignment | Web onboarding/property forms support choosing owners and clearing owner assignment. | **Partial** | Add owner create/edit affordance near mobile property owner picker once Owners module exists. |
| Property loans / mortgage management | Web property detail has Mortgage / Loans grid, add/edit stepper, scan/import button, delete, and amortization schedule. | **Missing, phone-confirmed** | **Port.** Phone property detail has no loan/mortgage section. Start with list/add/edit/scan on property detail. Amortization schedule can be read-only later. |
| Lease template library and PDF field designer | Web has upload landlord PDF, template library, active default, dynamic field catalog, drag/drop field placement, whiteout, and filled preview. | **Web-first OK** | Keep desktop-owned. Mobile can later show default template/status and route to web, but the PDF designer is not a good phone-first workflow. |
| Lease creation/editing, eligible tenant picker, multi-tenant support | Web and mobile both received lease-add/signing/picker fixes. | **Covered** | No obvious port gap from this window. Still phone-test add lease with active-tenant filter and multi-tenant selection. |
| Lease notices / create-send notice | Web lease detail has Create / Send notice and notice dialog. | **Covered** | No action unless phone UX falls short. Mobile lease detail opens create tenant notice flow; mobile notice templates and queue exist. |
| Lease e-sign delivery/status | Web had repeated e-sign queue/delivery state fixes. Mobile had signature request body fix. | **Partial / verify** | Verify on phone with real email enabled. Mobile can initiate/handle lease flows, but the web queue/status panel is richer. Decide if mobile needs the same delivery diagnostics. |
| Unit Command Center tabs | Web has unit tabs including listing, lease, applications, ledger, turnover, documents, timeline. | **Covered, phone-confirmed for core tabs** | Phone shows overview/listing/lease/apps/ledger/tenants/turnover/work tabs. Documents/timeline still appear more web-centric, but no urgent parity task came out of this pass. |
| Zillow listing handoff | Web Listing tab can generate/save packet, copy copy, store Zillow listing/application URLs, and open Zillow Rental Manager. | **Covered** | No action. Mobile has listing tab, generate/save, handoff fields, and opens Zillow externally. |
| Unit applications tab / share application link | Web unit Applications tab has unit-scoped applications and create-link empty state. | **Covered / verify** | Mobile Applications list/detail and unit applications tab exist. Verify unit-scoped link context on phone. |
| Unit ledger and historical lease guardrails | Web added unit ledger tab and historical lease guardrails. | **Covered, phone-confirmed for ledger** | Phone Ledger tab shows balance summary plus payment and operating-cost rows. Historical lease edge cases were not force-created in the walkthrough. |
| Turnover workspace | Web added turnover workspace. | **Covered, phone-confirmed** | Phone Turnover tab shows status, make-ready plan, linked work orders, and New turnover task. No parity action. |
| Work order timeline and notes | Web was changed to match mobile timeline behavior. | **Covered** | No action. Mobile was the reference implementation: status changes can add timeline notes; web now has the vertical newest-first timeline. |
| Work order create/edit steppers | Web form steppers were added. | **Covered** | No action. Mobile work order forms already use tabbed step specs. |
| Appointment modal steppers | Web appointment modal was converted to guided stepper. | **Covered, phone-confirmed** | Phone Calendar context FAB opens New appointment with Details / Schedule / Contact steps. No parity action. |
| Expense/payment scan and autofill | Web expense scan fields/order/context were tightened. | **Covered / verify** | Mobile has capture FAB, scan review, expense detail/form tabbed steps, payments/expenses screens. Verify latest web ordering/autofill details on phone. |
| Vendor CRUD | Web vendor form has Basics, Contact, Address, Compliance steps. | **Partial, phone-confirmed** | Mobile has vendor CRUD and a 3-step form, but the user wants the add/edit UI reviewed separately before expanding it. |
| Vendor website | Web stores/validates `website`. | **Missing small field, phone-confirmed** | **Port soon after UI review.** Add to mobile model, form controller, payload, and display. |
| Security Deposits nav | Web has `/deposits` under Money as "Security Deposits." | **Covered** | Mobile Money includes Deposits. Label may be shorter as "Deposits"; decide if exact text should be "Security Deposits." |
| Owner reports / year-end packet | Web has accounting/tax/report pages. | **Partial** | Mobile has Owner Reports and year-end packet PDF opening. Keep detailed tax drilldowns web-first unless users need phone reporting. |
| Activity history / recent activity flyout / per-record history | Web has dashboard Recent Activity, `/audit`, admin audit, ActivityFeed, and per-record RecordHistory. | **Missing, phone-confirmed** | **Decide.** If mobile should be equal, add Activity History under Admin/Inbox and per-record history on core details. If audit is admin-heavy, keep web-first but document that decision. |
| Inspections checklist editing | Web gained editable inspection checklist/date fields. | **Covered for active inspections** | Phone verifies create flow. Source confirms active inspection items can be updated with result, note, and photo; completed inspections are read-only by design. |
| Bulk spreadsheet import | Web has `/import` CSV dry-run/commit and templates. | **Web-first OK** | Keep web-owned. Phone can expose a link/explainer only. |
| Batch lease import | Web has `/scan/batch` lease import. | **Web-first OK / mobile-light path** | Full batch review is desktop-heavy. Mobile should keep single lease scan/import, not necessarily batch management. |
| Admin audit / advanced controls | Web has admin audit and admin routes. | **Web-first OK** | Keep admin forensic audit web-only unless a mobile admin persona becomes explicit. |

## Source Evidence

- Mobile navigation coverage: `mobile/lib/features/home/mobile_destination.dart` imports core feature screens and maps Rentals/Money/Work/Inbox/Admin destinations.
- Mobile FAB coverage: `mobile/lib/features/home/mobile_quick_action_fab.dart` exposes Chat, Record, and Scan actions.
- Web onboarding depth: `web/src/routes/(protected)/onboarding/+page.svelte`, `web/src/lib/onboarding/wizard-steps.ts`, `web/src/lib/onboarding/owner-selection.ts`, `web/src/lib/onboarding/property-payload.ts`.
- Mobile onboarding partial coverage: `mobile/lib/features/onboarding/getting_started_screen.dart` and `mobile/lib/features/scan/guided_rental_flow.dart`; the mobile guided rental flow explicitly notes no existing-link picker yet.
- Web owner CRUD: `web/src/routes/(protected)/owners/+page.svelte`, `web/src/routes/(protected)/owners/[id]/+page.svelte`; mobile has no `mobile/lib/features/owners` directory.
- Web loans: `web/src/lib/components/property/PropertyLoansSection.svelte`; mobile search only found loan as an expense category, not a property loan module.
- Web lease templates: `web/src/routes/(protected)/lease-templates/+page.svelte`, `web/src/lib/components/document-templates/LeaseTemplateDesigner.svelte`; mobile search found no document-template/lease-template module.
- Zillow listing parity: `web/src/lib/components/unit/tabs/ListingTab.svelte`, `mobile/lib/features/units/unit_command_center_screen.dart`, `mobile/lib/features/units/units_repository.dart`.
- Work order timeline parity: `mobile/lib/features/maintenance/work_order_detail_screen.dart`, `mobile/lib/features/maintenance/work_order_timeline.dart`, `web/src/lib/components/shared/WorkOrderTimeline.svelte`.
- Vendor website gap: web vendor form includes `website`; `mobile/lib/features/vendors/vendors_models.dart` and `mobile/lib/features/vendors/vendors_list_screen.dart` do not.
- Activity/audit gap: web has `/audit`, admin audit, `ActivityFeed`, `RecordHistory`, and dashboard Recent Activity; mobile search found no equivalent audit/activity screen.
- Live phone screenshots/UI dumps: `/tmp/rc-mobile-parity/01-fab.png`, `/tmp/rc-mobile-parity/23-appointment-sheet.png`, `/tmp/rc-mobile-parity/26-new-inspection.png`, `/tmp/rc-mobile-parity/29-vendor-edit.png`, `/tmp/rc-mobile-parity/30-getting-started.png`.

## Suggested Next Mobile Tasks

0. **Navigation cleanup guardrail**
   - Remove or de-emphasize Account > Browse all / Explore all if product confirms that direction.
   - Before removing it, make sure every destination has one primary path through bottom tabs, domain tabs, contextual actions, Account, or Settings.
   - Do not use Browse all as the parity answer for any workflow.

1. **Fix mobile Getting Started, then build mobile-light Guided Setup**
   - Fix the live 404 from Getting started setup-state loading.
   - Keep the large guided import and education flow web-owned.
   - After the screen works, support scan lease and simple manual property/unit/tenant/lease steps.
   - Save place server-side or from existing onboarding status endpoints.

2. **Mobile Owners module**
   - Add Owners nav destination under Admin or Rentals.
   - List/search owners using server-side paging.
   - Add/edit/delete-safe owner sheets.
   - Show linked properties and documents/history later.

3. **Mobile Property Loans**
   - Add property loan list to property detail.
   - Add/edit loan with tabbed form.
   - Add scan/import entry.
   - Add read-only amortization in a second pass.

4. **Mobile Activity History**
   - Add global Activity History screen.
   - Add per-record history to lease/payment/expense/work order/property/owner/tenant details if desired.

5. **Vendor Website Parity, after vendor UI review**
   - Review the current mobile vendor add/edit UI before expanding it.
   - Add a website field to mobile vendor model, create/edit payload, and detail display.
