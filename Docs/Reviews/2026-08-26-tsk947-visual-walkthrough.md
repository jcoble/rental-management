# TSK-947 visual walkthrough — web, after the Landlord-First Cut lanes

Date: 2026-08-26. Reviewer: controller session. Method: headless Playwright, viewport verified `1710x990`, dev stack with the sandbox sample data (8 properties), admin account. One screenshot per top-level screen, judged for spacing, jammed elements, styling, colour, wayfinding, and whether the flow makes sense to a non-technical landlord.

Mobile (Flutter) could not be run on this machine (the Flutter web build stalls); the mobile lanes were verified with widget tests only. The owner's manual run-through is the first real look at the mobile screens.

## Verdict

The 25 approved items landed and the app reads far more like a landlord's tool than an accountant's. The dashboard "Today" view, "Who's behind", Money, and Notifications are the strongest screens. What is left is mostly wording that slipped through, two navigation bugs, and one page (Activity history) that still shows database internals.

## Screens

| Screen | Screenshot | Verdict |
|---|---|---|
| Dashboard `/` | ![](screenshots/tsk947/dashboard.png) | Good. Clear priority list, one action per row. Intermittently returned a 500 error page while the e2e suite was hammering the same dev server; not reproducible on its own — watch for it in the manual pass. |
| Money overview `/accounting` | ![](screenshots/tsk947/accounting.png) | Good. **Fix:** the "Rent & payments" filters are numeric text boxes labelled "Property ID" / "Unit ID" — a landlord does not know ids. |
| Who's behind `/accounting/past-due` | ![](screenshots/tsk947/accounting_past-due.png) | Good. **Fix:** sidebar highlights both "Who's behind" and "Overview". |
| Reports `/reports` | ![](screenshots/tsk947/reports.png) | Good. |
| Properties | ![](screenshots/tsk947/properties.png) | Good. Naming drifts: sidebar group "Rentals", breadcrumb "Portfolio", page "Properties", button "Add rental". Pick one word (recommend "Rentals" everywhere, or "Properties" everywhere). |
| Units | ![](screenshots/tsk947/units.png) | **Fix:** top bar reads "Rental Command" instead of "Units". Status cell reads "Occupied · Next: Active" which means nothing; only "Renewal due" deserves a second badge. Property filter label sits above its select while the search box has none — slightly uneven row. |
| Tenants | ![](screenshots/tsk947/tenants.png) | Good. Subtitle "Resident contacts and lease participation" is stiff — "Who lives where, and how to reach them" would do. |
| Leases | ![](screenshots/tsk947/leases.png) | Vacant units appear at the top as "No primary tenant · Closed · No signed lease yet" rows. To a landlord these look like broken leases. Recommend labelling them "Vacant" or hiding them behind the status filter. Needs owner call — not changed. |
| Repairs | ![](screenshots/tsk947/maintenance.png) | Good. Note the large gradient "hero" header — see *Header styles* below. |
| Applications | ![](screenshots/tsk947/applications.png) | Good empty state. |
| Messages | ![](screenshots/tsk947/messages.png) | Fine. **Fix:** "Showing 0 of 0 conversations" under the empty state is noise. |
| Security deposits | ![](screenshots/tsk947/deposits.png) | Table is clear. **Fix:** the blue info banner ("A deposit account is prepared with the move-in agreement… do not create another holding") is jargon. |
| Banking | ![](screenshots/tsk947/banking.png) | Good; the "cannot move money" reassurance is exactly right. |
| Tenant notices `/notices` | ![](screenshots/tsk947/notices.png) | **Fix:** sidebar says "Sent notices", the page says "Tenant notices" and opens on Drafts. |
| Settings | ![](screenshots/tsk947/settings.png) | **Fix:** the tab strip wraps so "Activity history" sits alone on a second row; the page is capped at ~900px leaving the right third empty; the sidebar shows "Settings ▸ Settings". The sidebar sub-items and the tabs partly duplicate each other — acceptable for now. |
| Notifications | ![](screenshots/tsk947/settings_notifications.png) | Good — the new single page is easy to follow. **Fix:** "Send alerts to the account email shown **below**" — the address is above. |
| Lease settings | ![](screenshots/tsk947/settings_lease-templates.png) | Good. **Fix:** sidebar highlights both "Lease settings" and "Settings". "Exact PDF overlay" and "Field placement" are technical but tolerable on a setup page. |
| Scan / Add | ![](screenshots/tsk947/scan.png) | Good. **Fix:** tile hint "Becomes an agreement record" (banned word; it is a lease). |
| Activity history `/audit` | ![](screenshots/tsk947/audit.png) | **Fix:** shows raw field names and internal ids ("Updated at utc", "Issued artifact id changed from — to 31", "Possession agreement exception authorized by user id"). A landlord cannot read this. Hide timestamp/id fields, translate the rest. |
| Owners | ![](screenshots/tsk947/owners.png) | Good. |
| Vendors | ![](screenshots/tsk947/vendors.png) | Good. |
| Appointments | ![](screenshots/tsk947/appointments.png) | Good; colour legend helps. |

`/team` and `/inspections` are not routes (404) — they live under Settings and Repairs respectively. Fine.

## Cross-cutting

- **Header styles.** Half the pages use a flat heading (Properties, Units, Tenants, Leases, Banking, Scan, Activity history, Settings); the other half use a tall gradient "hero" card (Repairs, Applications, Deposits, Notices, Owners, Vendors, Appointments, Lease settings). Both look fine alone; together they make the app feel like two apps. Recommend the flat heading everywhere — it puts the table 120px higher. Not changed in this pass; owner call.
- **Colour and spacing** are consistent: dark theme, one accent, status chips readable, nothing jammed at 1710px. Tables have comfortable row height.
- **Wayfinding.** The sidebar groups (Money / Rentals / Work / Inbox / Settings) make sense. Two problems fixed in this pass: double highlighting, and the Units page title.

## Fix list (all merged; screenshots above are AFTER the fixes for the affected screens)

| # | Item | PR |
|---|---|---|
| 1 | Sidebar double highlight (Who's behind + Overview; Lease settings + Settings) | #652 |
| 2 | Units top bar title | #652 |
| 3 | "Sent notices" → "Tenant notices" | #652 |
| 4 | "Settings ▸ Settings" → "General" | #652 |
| 5 | Notifications "shown below" → "shown above" | #652 |
| 6 | Scan tile "agreement record" → "lease record" | #652 |
| 7 | Deposits banner in plain words | #652 |
| 8 | Units "Next: Active" badge | #652 |
| 9 | Messages "0 of 0" footer | #652 |
| 10 | Settings tab strip wrap + page width | #652 |
| 11 | Activity history: hide internal fields, plain labels | #651 |
| 12 | Rent & payments filters: property/unit pickers instead of id boxes | #651 |

Called out, not changed (owner decision): Leases list showing vacant units as closed leases; Rentals/Properties/Portfolio naming; header style unification; Tenants subtitle wording.


Still flagged after the fixes: the Activity history entity chip reads "Lease management #11" / "Tenant account #11" — record-type names a landlord would not use. Needs a small type-name map; not done in this pass.
