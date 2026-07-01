# Web to Mobile Parity Ledger

Date: 2026-07-01  
Task: TSK-614  
Baseline: `origin/main` at `8325301b` (`PR #443`, mobile global quick action FAB)  
Mobile device reference: `SM-S906U` over wireless debugging

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
| P0 | Guided setup / import more | Partial | Keep the full onboarding import owned by web, but add a mobile-light path: return to setup, scan a lease, simple manual property/unit/tenant/lease steps, and saved progress. |
| P1 | Property loans / mortgage management | Missing | Port loan list/add/edit/scan entry to mobile property detail. Amortization can be read-only or phase 2. |
| P1 | Activity history / per-record audit | Missing | Decide whether mobile needs Activity History. If yes, add a global screen plus lightweight per-record history on core details. |
| P1 | Vendor website field | Missing small field | Add `website` to mobile vendor model, form, payload, and display. |
| P2 | Lease template library/designer | Web-first OK | Keep the full PDF field designer on web. Consider mobile read-only template/default status later. |
| P2 | Bulk CSV/spreadsheet import and batch lease import | Web-first OK | Keep web-owned. Mobile can point users to web for large imports. |
| Verify | Turnover workspace, unit ledger/history, inspection editor, appointment steppers | Covered/partial by source | These have mobile surfaces, but need a real phone walkthrough for UX parity. |

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

## Ledger

| Area | Web shipped on main | Mobile status | Decision |
| --- | --- | --- | --- |
| Mobile shell and domain nav | Web has broad routes; mobile shell has Rentals, Money, Work, Inbox, AI, Admin destinations. | **Covered** | No parity action. Mobile exposes Properties, Units, Tenants, Leases, Applications, Money, Deposits, Banking, Reports, Work Orders, Calendar, Inspections, Vendors, Automations, Notices, Messages, Notifications, Assistant, Team, Settings. |
| Global quick action FAB | Web has record/chat/scan affordances in app chrome. | **Covered** | No action beyond phone QA. Mobile FAB has primary action plus Chat, Record, Scan actions and scope-aware fallback behavior. |
| Guided setup / onboarding | Web now has a first-class wizard with owner/property selectors, lease-first import, manual property/unit/tenant/lease steps, saved step links, settings deep-links, delete guardrails, and celebrations. | **Partial** | **Port a mobile-light version.** Keep heavy import/web teaching flow on web, but phone should let users return to setup, scan a lease, manually add the first property/unit/tenant/lease in steps, and see progress. |
| Owner management | Web has `/owners`, `/owners/[id]`, owner create/edit/delete, structured address, documents, record history, and onboarding owner selectors. | **Missing** | **Port.** Add mobile Owners module. It is core data, not desktop-only. Mobile currently has owner reports and property owner picker, but no owner CRUD. |
| Property owner assignment | Web onboarding/property forms support choosing owners and clearing owner assignment. | **Partial** | Add owner create/edit affordance near mobile property owner picker once Owners module exists. |
| Property loans / mortgage management | Web property detail has Mortgage / Loans grid, add/edit stepper, scan/import button, delete, and amortization schedule. | **Missing** | **Port.** Start with list/add/edit/scan on property detail. Amortization schedule can be read-only later. |
| Lease template library and PDF field designer | Web has upload landlord PDF, template library, active default, dynamic field catalog, drag/drop field placement, whiteout, and filled preview. | **Web-first OK** | Keep desktop-owned. Mobile can later show default template/status and route to web, but the PDF designer is not a good phone-first workflow. |
| Lease creation/editing, eligible tenant picker, multi-tenant support | Web and mobile both received lease-add/signing/picker fixes. | **Covered** | No obvious port gap from this window. Still phone-test add lease with active-tenant filter and multi-tenant selection. |
| Lease notices / create-send notice | Web lease detail has Create / Send notice and notice dialog. | **Covered** | No action unless phone UX falls short. Mobile lease detail opens create tenant notice flow; mobile notice templates and queue exist. |
| Lease e-sign delivery/status | Web had repeated e-sign queue/delivery state fixes. Mobile had signature request body fix. | **Partial / verify** | Verify on phone with real email enabled. Mobile can initiate/handle lease flows, but the web queue/status panel is richer. Decide if mobile needs the same delivery diagnostics. |
| Unit Command Center tabs | Web has unit tabs including listing, lease, applications, ledger, turnover, documents, timeline. | **Covered / verify** | Mobile has overview, listing, lease, applications, ledger, tenants, turnover, work. Do phone walkthrough for each tab after recent churn. |
| Zillow listing handoff | Web Listing tab can generate/save packet, copy copy, store Zillow listing/application URLs, and open Zillow Rental Manager. | **Covered** | No action. Mobile has listing tab, generate/save, handoff fields, and opens Zillow externally. |
| Unit applications tab / share application link | Web unit Applications tab has unit-scoped applications and create-link empty state. | **Covered / verify** | Mobile Applications list/detail and unit applications tab exist. Verify unit-scoped link context on phone. |
| Unit ledger and historical lease guardrails | Web added unit ledger tab and historical lease guardrails. | **Covered / verify** | Mobile has ledger routes/tabs and lease flows, but run a phone pass for historical lease behavior before calling it fully done. |
| Turnover workspace | Web added turnover workspace. | **Covered / verify** | Mobile has a Turnover tab in Unit Command Center. Run phone UX pass to confirm same fields/actions. |
| Work order timeline and notes | Web was changed to match mobile timeline behavior. | **Covered** | No action. Mobile was the reference implementation: status changes can add timeline notes; web now has the vertical newest-first timeline. |
| Work order create/edit steppers | Web form steppers were added. | **Covered** | No action. Mobile work order forms already use tabbed step specs. |
| Appointment modal steppers | Web appointment modal was converted to guided stepper. | **Covered / verify** | Mobile appointment form uses tabbed step specs. Verify it is not missing any web-only fields from `#437/#435`. |
| Expense/payment scan and autofill | Web expense scan fields/order/context were tightened. | **Covered / verify** | Mobile has capture FAB, scan review, expense detail/form tabbed steps, payments/expenses screens. Verify latest web ordering/autofill details on phone. |
| Vendor CRUD | Web vendor form has Basics, Contact, Address, Compliance steps. | **Partial** | Mobile has vendor CRUD, ratings, W-9/1099 fields, notes, address. Add missing `website`. |
| Vendor website | Web stores/validates `website`. | **Missing small field** | **Port soon.** Add to mobile model, form controller, payload, and display. |
| Security Deposits nav | Web has `/deposits` under Money as "Security Deposits." | **Covered** | Mobile Money includes Deposits. Label may be shorter as "Deposits"; decide if exact text should be "Security Deposits." |
| Owner reports / year-end packet | Web has accounting/tax/report pages. | **Partial** | Mobile has Owner Reports and year-end packet PDF opening. Keep detailed tax drilldowns web-first unless users need phone reporting. |
| Activity history / recent activity flyout / per-record history | Web has dashboard Recent Activity, `/audit`, admin audit, ActivityFeed, and per-record RecordHistory. | **Missing** | **Decide.** If mobile should be equal, add Activity History under Admin/Inbox and per-record history on core details. If audit is admin-heavy, keep web-first but document that decision. |
| Inspections checklist editing | Web gained editable inspection checklist/date fields. | **Partial / verify** | Mobile inspections/new inspection exist. Verify whether mobile can edit completed/checklist detail like web or only run/create inspections. |
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

## Suggested Next Mobile Tasks

1. **Mobile Owners module**
   - Add Owners nav destination under Admin or Rentals.
   - List/search owners using server-side paging.
   - Add/edit/delete-safe owner sheets.
   - Show linked properties and documents/history later.

2. **Mobile-light Guided Setup**
   - Add "Guided Setup" re-entry from Getting Started and Settings.
   - Support scan lease and simple manual entry.
   - Save place server-side or from existing onboarding status endpoints.
   - Keep CSV/batch/full education web-owned.

3. **Mobile Property Loans**
   - Add property loan list to property detail.
   - Add/edit loan with tabbed form.
   - Add scan/import entry.
   - Add read-only amortization in a second pass.

4. **Mobile Activity History**
   - Add global Activity History screen.
   - Add per-record history to lease/payment/expense/work order/property/owner/tenant details if desired.

5. **Vendor Website Parity**
   - Add a website field to mobile vendor model, create/edit payload, and detail display.
