# Mobile App IA Restructure — Implementation Plan

**Date:** 2026-06-27
**Status:** Draft for review (planning only — no code changed in this pass)
**Spec:** `Docs/superpowers/specs/2026-06-27-mobile-ia-restructure-design.md`
**Surface:** `mobile/` (Flutter, go_router + Riverpod, M3-Expressive)

The strategy is **incremental behind the existing shell** — the `IndexedStack` +
`_MorphNavBar` + center-docked Capture FAB stay verbatim. Each phase is independently
shippable and reviewable. Phases 1–3 deliver everything you asked for (flyout + top tabs);
4–7 add the scalability/hardening layers.

---

## Phasing at a glance

| Phase | Goal | Effort | Risk |
|---|---|---|---|
| **0 — Cleanup & foundations** | Delete dead code; add unwired `HubScaffold` | S | Low |
| **1 — Flyout drawer** | Replace the Browse grid with the left flyout; de-risk the AppBar | M | Low |
| **2 — Money & Work hub tabs** | Top tabs on the two daily hubs; kill double-FAB | L | Medium |
| **3 — Rentals, Inbox, Reports hubs** | Finish hub coverage; Notices-under-Inbox parity | M | Low–Med |
| **4 — Command Palette (text v1) + search API** | Real content search; the scalability valve | M | Medium |
| **5 — Tenant shell cleanup** | Kill tenant fragmentation/duplication | S | Low |
| **6 — Router hardening (isolated)** | `StatefulShellRoute` per-hub back-stacks | M | Medium |
| **7 — Voice & adaptive (future/optional)** | Voice palette + tablet NavigationRail | L | Med–High |

> **The user-visible win lands at the end of Phase 2** (flyout + the two daily hubs with
> tabs). Phase 3 completes the picture. Everything after is depth.

---

## Phase 0 — Cleanup & foundations  ·  S · Low risk
**Goal:** remove dead code and lay reusable scaffolding without changing visible nav.

- Delete orphaned `scan_tab.dart`, `scan_list_screen.dart`, `scan_capture.dart` and the
  stale `properties_tab.dart` (verify **zero references** first).
- Add `lib/core/widgets/hub_scaffold.dart` — reusable `HubScaffold` (AppBar with hamburger
  + title + search icon + bell; `DefaultTabController` + M3 `TabBar`; `TabBarView`; one
  **contextual FAB** that swaps action per tab; `rememberLastTab`). Unwired for now.
- Confirm `navigationDrawerTheme` in `app_theme.dart` is sufficient for the drawer.

## Phase 1 — Flyout drawer  ·  M · Low risk
**Goal:** replace the Browse grid with the left flyout mirroring the web AppShell; de-risk the AppBar.

- Add `lib/features/home/app_drawer.dart` — `NavigationDrawer` built from `M3MorphNavItem`
  (reuse, don't hand-roll): account header + portfolio switcher + Command-Palette search
  field; pinned Today + Scan/Add; accordion groups Money/Rentals/Work/Inbox (one open at a
  time, active route auto-expands, persist open-group like web's `rc.nav.groups`); divider;
  Ask + Help rail; Settings hub; Sign out with confirm.
- Wire `Scaffold.drawer` + a hamburger leading; **delete the Today AppBar Browse grid icon
  and the Sign-out icon**; keep only the bell on Today.
- Remove Sign-out from the tenant Home AppBar; route it to a single confirmed entry.
- Migrate the group/label data out of `more_tab.dart`, then **delete `more_tab.dart`**.

## Phase 2 — Money & Work hub top-tabs (the daily 80%)  ·  L · Medium risk
**Goal:** give the two daily hubs top tabs; eliminate double-FAB / nested Scaffolds.

- Add `money_hub.dart` (Snapshot/Payments/Expenses/Deposits/Banking) with the collapsing
  cash-snapshot header + a "Who owes me" action chip, and `work_hub.dart`
  (Work orders/Appointments/Inspections/Recurring/Vendors).
- Refactor the ~10 corresponding list screens **from full Scaffolds to plain tab-body
  widgets**; hoist their create-FABs into the hub's one contextual FAB.
- Swap the `IndexedStack` Money/Work children to the hubs; add `/money?tab=` and
  `/work?tab=` routes.
- **Sequence the per-project rebuilds — do not swarm `flutter build`/test.**

## Phase 3 — Rentals, Inbox, Reports hubs  ·  M · Low–Medium risk
**Goal:** complete hub coverage and land Notices-under-Inbox web parity.

- Add `rentals_hub.dart` (Properties/Tenants/Leases/Applications), `inbox_hub.dart`
  (Messages/Notices/Alerts), `reports_hub.dart` (Insights/Owner reports).
- Refactor the remaining list screens to tab bodies; move Notices from Work to Inbox.
- Point the bottom-nav **Messages** slot at `InboxHub`; add `/rentals`, `/inbox?tab=`,
  `/reports?tab=` routes.

## Phase 4 — Global Command Palette (text v1) + search backend  ·  M · Medium risk
**Goal:** replace the buried label filter with real content search.

- Add a **server-side cross-entity search endpoint** — one paged SQL/EF query, **no
  in-memory filtering** (project data rule) — + `search_repository.dart`.
- Add `command_palette.dart` (typeahead over actions/records/screens, Recents); wire the
  drawer field + per-hub AppBar search icon.
- Resolve palette results to canonical `hub?tab` / detail routes. Mic affordance stubbed.

## Phase 5 — Tenant shell cleanup  ·  S · Low risk
- Split "New request" into its own Maintenance tab.
- Single canonical Account-history entry.
- Sign out only in More, with confirm.

## Phase 6 — Router hardening (isolated)  ·  M · Medium risk
**Goal:** predictable per-hub back-stacks and fully addressable deep links.

- Migrate the hand-rolled `IndexedStack` → `StatefulShellRoute.indexedStack`, one branch
  per hub (Today/Money/Work/Inbox + Rentals).
- Move detail pushes to branch-local navigators; **verify every push deep link**.
- Persist last-used tab per hub.
- *Decoupled from the IA change to contain deep-link regression risk.*

## Phase 7 — Voice & adaptive (future, optional)  ·  L · Med–High risk
- Add mic-to-text + intent routing into the Command Palette ("Maria's lease", "record payment").
- At a breakpoint, swap the drawer for a permanent `NavigationRail` (reuse `M3MorphNavItem`);
  defer the expensive two-pane master/detail.

---

## Component changes (file-by-file)

### New files
| File | Purpose |
|---|---|
| `lib/features/home/app_drawer.dart` | The flyout drawer (replaces Browse). |
| `lib/core/widgets/hub_scaffold.dart` | Reusable hub: AppBar + TabBar + TabBarView + one contextual FAB + remember-last-tab. |
| `lib/features/money/money_hub.dart` | Money hub (Snapshot/Payments/Expenses/Deposits/Banking). |
| `lib/features/money/reports_hub.dart` | Reports sub-hub (Insights/Owner reports). |
| `lib/features/maintenance/work_hub.dart` | Work hub (Work orders/Appointments/Inspections/Recurring/Vendors). |
| `lib/features/rentals/rentals_hub.dart` | Rentals hub (Properties/Tenants/Leases/Applications). |
| `lib/features/messages/inbox_hub.dart` | Inbox hub (Messages/Notices/Alerts). |
| `lib/features/search/command_palette.dart` | Full-screen typeahead over actions/records/screens. |
| `lib/features/search/search_repository.dart` | Cross-entity search repository (server-side, paged). |

### Modified
| File | Change |
|---|---|
| `lib/features/home/home_shell.dart` | Add `Scaffold.drawer`; swap Money/Work/Messages `IndexedStack` children to the hubs; delete Today AppBar Browse + Sign-out icons, add hamburger; keep IndexedStack/`_MorphNavBar`/FAB/`_SandboxIndicator` verbatim; tenant Sign-out → More. |
| `payments_screen.dart`, `expenses_list_screen.dart`, `deposits_screen.dart`, `banking_screen.dart`, `owner_reports_screen.dart`, `insights_screen.dart`, `work_orders_screen.dart`, `recurring_maintenance_list_screen.dart`, `inspections_list_screen.dart`, `vendors_list_screen.dart`, `appointments_screen.dart`, `notices_screen.dart`, `properties_list_screen.dart`, `tenants_list_screen.dart`, `leases_list_screen.dart`, `applications_list_screen.dart` | Strip own `Scaffold`/`AppBar`/`FAB`; become tab bodies; create-actions lift into the hub's contextual FAB. |
| tenant tabs in `home_shell.dart` (`_TenantMaintenanceTab`, `_TenantMoreTab`) | Split "New request" tab; single Account-history entry; Sign out once + confirm. |

### Deleted
`lib/features/home/more_tab.dart` · `lib/features/properties/properties_tab.dart` ·
`lib/features/scan/scan_tab.dart` · `lib/features/scan/scan_list_screen.dart` ·
`lib/features/scan/scan_capture.dart`

---

## Router changes (`lib/core/router/app_router.dart`)

- **Keep all existing flat detail routes** (`/work-orders/:id`, `/payments/:id`,
  `/expenses/:id`, `/scan/:draftId`, `/messages/:id`, `/notifications`) so push
  notifications + deep links keep resolving.
- **Additive (Phases 1–3):** add `/money?tab=`, `/work?tab=`, `/rentals?tab=`,
  `/inbox?tab=`, `/reports?tab=`; map existing `/money` and `/work` builders to the new
  hubs (default Snapshot / Work orders).
- Add `/search` (or `showGeneralDialog`) for the Command Palette + result→route resolution.
- Fix the dashboard inconsistency: "Open Money" and "Work Orders › View all" both target
  the hub route/tab (no more switch-tab-vs-push divergence).
- Drop references to the deleted `ScanTab`/`ScanListScreen`/`ScanCaptureSheet`/`PropertiesTab`.
- **Phase 6 (isolated, later):** migrate `IndexedStack` → `StatefulShellRoute.indexedStack`;
  realize remember-last-tab-per-hub.

---

## Build / process guardrails

- **No build swarm.** When refactoring many screens in parallel, parallelize the *editing*
  but **sequence the `flutter build`/test** steps (and never rebuild the same target
  concurrently). One emulator session for verification.
- **Verify each phase on the lightweight emulator** (profile/prod build, Android emulator,
  driven via `adb`) and capture before/after screenshots alongside `Docs/Reviews/mobile-ia-shots/`.
- **Branch naming** for tracked work: `tsk-<NN>-mobile-ia-<slug>` so the GitHub→Notion sync
  links the task.

---

## Decisions needed before Phase 1 (see spec §6)

Notices placement · Inbox-hub-vs-plain-Messages · Command-Palette text-only v1 · whether a
cross-entity search API exists (gates Phase 4) · StatefulShellRoute timing · Reports fold ·
"Who owes me" rename · drawer side · tenant Maintenance "New request" tab · "coming soon"
rows for Owners/Activity history.
