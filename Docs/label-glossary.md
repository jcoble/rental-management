# Label Glossary — canonical cross-surface terms

One landlord-facing concept should read the **same word** everywhere it appears —
web nav, web page heading, mobile nav, mobile screen title, and any copy that
refers to it. This file is the single source of truth for those words so web and
mobile stop drifting apart.

When you add or rename a user-facing label, **check here first**. If a concept is
missing, add a row. If you're tempted to coin a new synonym for something already
listed, use the canonical term instead.

> Scope: this is a *labels* glossary (the words the non-technical landlord reads),
> not a data dictionary. Schema/enum names (e.g. `Accounting`, `pastDueCount`) can
> stay as-is in code — just don't surface them in the UI.

## Why this exists

The 2026-06-14 holistic review (`Docs/Reviews/2026-06-14-batch1-holistic-review.md`,
findings C-8 / C-10) found the same concept wearing different names across surfaces:
the Properties/Tenants/Leases group was "Rentals" on web but "Portfolio" on mobile;
the Money tab opened a page once headed "Accounting"; the delinquency view was
"tenants behind" in one place and "Who's behind" in another. Small drifts, but they
make a designed product feel un-proofread — and for an app whose whole pitch is
hand-holding a non-technical landlord, the words have to line up.

## Canonical concept labels

| Concept | Canonical label | Web | Mobile | Notes |
|---|---|---|---|---|
| Financial hub (rent, expenses, deposits, banking, reports) | **Money** | nav group **Money**; `/accounting` page heading **Money** | bottom tab **Money**; Browse group **Money** | The schema word "Accounting" stays in code/DTOs; never show it to the landlord. |
| Properties / Tenants / Leases / Applications group | **Rentals** | nav group **Rentals** (`AppShell.svelte`) | Browse group **Rentals** (`more_tab.dart`) | Frozen group name per the unified-restructure roadmap. Was "Portfolio" on mobile (fixed). |
| Maintenance / Appointments / Vendors group | **Work** | nav group **Work** | bottom tab **Work**; Browse group still **Operations** | "Work" is the frozen group name; mobile Browse's "Operations" header is a known residual to align when mobile's group headers are reconciled. |
| Tenant ↔ landlord communications | **Messages** | nav item **Messages** (Inbox group); portal **Messages** | bottom tab **Messages** | Don't reintroduce "Requests" as a second name for the same conversation list. |
| Landlord-issued notices (renewal, late-rent, move-out) | **Tenant notices** | nav item **Tenant notices**; `/notices` heading **Tenant notices** | Browse item **Notices** | Mobile shortens to "Notices" inside the Rentals/Operations context; the full cross-surface term is "Tenant notices." |
| Who is past due on rent (delinquency) | **Who owes me** | dashboard "N tenants behind" KPI → (Wave 2 hub) | KPI "tenants behind" → screen **Who's behind** | The IA's chosen destination label is **"Who owes me."** Until web's hub lands, keep the KPI copy "tenants behind" on both, and converge the destination on "Who owes me." |
| Account change log / audit trail | **Activity history** | nav item **Activity history**; `/audit` heading **Activity history** | Browse item (Admin) — mirror **Activity history** when added | Not "Audit" / "Activity log" in the UI. |
| Capture a document → draft record (the flagship) | **Scan** | pinned nav **Scan / Add** | Capture FAB (**Capture**) | C-10 flagged three names (nav "Scan / Add", title "Scan Receipts", heading "Scan a Document"). Converge on a single word — **Scan** — across nav, `<title>`, and heading. |

## Frozen navigation group names (web)

Pinned single links above the groups: **Dashboard**, **Scan / Add**.
Four landlord-noun groups, in frequency order:

1. **Money**
2. **Rentals**
3. **Work**
4. **Inbox** (contains Messages + Tenant notices)

Mobile bottom-nav daily destinations: **Today**, **Money**, **Work**, **Messages**,
plus the **Capture** action. Everything else is discoverable under the **Browse**
screen, whose group headers should mirror the web group names above.

## A note on the past-due numbers

The dashboard "N **tenants** behind" and the Money snapshot "across N **payments**"
measure different things (distinct behind-leases vs. total past-due payment rows),
so they can legitimately differ. When you surface either, **label the unit
explicitly** ("9 tenants" vs. "10 payments") so the landlord doesn't read two true
numbers as a contradiction.
