# Mobile App Information-Architecture Restructure — Design

**Date:** 2026-06-27
**Status:** Draft for review (map-only — no implementation in this pass)
**Surface:** Flutter app (`mobile/`) — landlord shell first, tenant shell cleanup second
**Author:** Produced via a multi-agent design workflow (nav-graph mapping, feature
catalog, IA problem audit, cross-surface conventions, persona/roadmap) → 3 candidate
approaches → 3 diverse-lens judges → synthesis. Grounded in a live emulator walkthrough
(profile/prod build, Android emulator) — see `Docs/Reviews/mobile-ia-shots/`.

---

## 1. Goal

The mobile app has grown to ~35 feature modules and ~23 landlord destinations, but **19
of them are hidden behind a single, unlabeled `grid_view` "Browse" icon in the top-right
of the Today tab.** The primary user is a **non-technical, phone-first landlord** (15–40
units). The IA should be *simple on the surface, full management system underneath*: the
five daily jobs stay 0–1 tap, everything else becomes discoverable through a **flyout
drawer** and **top tabs inside hubs** (the two things you asked for), with a **global
search/command palette** as the long-term scalability valve.

This document is the agreed-on target IA. The build sequence is in the companion plan:
`Docs/superpowers/plans/2026-06-27-mobile-ia-restructure-plan.md`.

---

## 2. The problem (current state)

### 2.1 What it looks like today

| Screen                                                                                          | Real screenshot                                                               |
| ----------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| Today dashboard — note the app bar's`bell + grid(Browse) + logout`                           | `Docs/Reviews/mobile-ia-shots/11-today-top.png`                             |
| **"Browse"** — 19 destinations, ~4 screens of scrolling, behind one icon                 | `Docs/Reviews/mobile-ia-shots/12-browse-top.png`, `…/14-browse-mid2.png` |
| Money tab — only an All/Payments/Expenses filter (Deposits/Banking/Reports buried)             | `Docs/Reviews/mobile-ia-shots/16-money.png`                                 |
| Work tab — only an Open/All filter (Recurring/Inspections/Vendors/Appointments/Notices buried) | `Docs/Reviews/mobile-ia-shots/17-work.png`                                  |

### 2.2 Current navigation map

```
LANDLORD (today)                              TENANT (today)
┌─ Bottom nav ────────────────────────┐       ┌─ Bottom nav ──────────────┐
│ Today · Money · (＋FAB) · Work ·     │       │ Home · Messages ·         │
│ Messages                            │       │ Maintenance · More        │
└─────────────────────────────────────┘       └───────────────────────────┘
Today AppBar  →  🔔 bell   ▦ BROWSE   ⏻ logout
                              │
                              ▼  one tap hides EVERYTHING below:
   BROWSE (more_tab.dart) — 19 items, 5 groups, ~4 screens of scroll
   ├ RENTALS  Getting started · Properties · Tenants · Leases · Applications
   ├ WORK     Recurring maint. · Inspections · Vendors · Appointments · Notices
   ├ MONEY    Payments · Expenses · Security deposits · Banking · Owner reports · Insights
   ├ AI       Assistant
   └ ADMIN    Team · Settings
```

### 2.3 Ranked IA problems (from the audit)

| #  | Severity           | Problem                                                                                                                                                    |
| -- | ------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1  | **critical** | ~19 of ~23 destinations buried behind one unlabeled`grid_view` "Browse" icon on Today.                                                                   |
| 2  | high               | No persistent section navigation (no drawer/flyout); every non-tab screen is push-and-back over a fixed 4-item`IndexedStack` and covers the bottom nav.  |
| 3  | high               | Today AppBar mixes three concept classes (notifications + 19-way navigation + session) and exposes an**unconfirmed Sign-out** as a top-level action. |
| 4  | high               | No global content search — the only "search" is a label-substring filter over the 19 Browse tiles, itself buried behind Browse.                           |
| 5  | high               | Hubs have no top tabs / sibling navigation — each is a dead-end leaf that can only reach its own list.                                                    |
| 6  | high               | Redundant/duplicate entry points and inconsistent dashboard navigation (switch-tab vs push) for the same destinations.                                     |
| 7  | medium             | "Work" the bottom-nav tab and "Work" the Browse group are different things with the same name.                                                             |
| 8  | medium             | Browse is a hardcoded single-level static list with no recents/favorites; becomes a junk drawer past 60 screens.                                           |
| 9  | medium             | Tenant nav fragments/duplicates items across Home, "More", and Maintenance.                                                                                |
| 10 | medium             | Two FABs stack on hub tabs (hub screens are full Scaffolds nested inside the shell Scaffold).                                                              |
| 11 | low                | Dead code:`properties_tab.dart` (nested-Navigator) and an orphaned `scan_tab/scan_list_screen/scan_capture` cluster — signs of IA churn.              |

---

## 3. Recommended IA — "Flyout + Hub-Tabs (+ Command Palette)"

**Why this one.** Of three candidate approaches, this scored in the top band on all
three judging lenses — **landlord simplicity 9, discoverability/web-consistency 8,
implementation feasibility 9** — and two of three judges named it their primary. It keeps
your daily muscle-memory path untouched, reuses the existing `IndexedStack` shell +
`_MorphNavBar` + center-docked Capture FAB verbatim, and ships incrementally behind the
current shell. It absorbs the best, lowest-risk ideas from the bolder "command palette"
approach (text-first global search; Notices-under-Inbox web parity; a Reports sub-hub to
cap hubs at ≤5 tabs) without that approach's risky two-pane/voice subsystems.

> Rejected alternatives: **Broad-hub bottom nav** (Home/Rentals/Money/Work/Account) — 5/5/5;
> it breaks the frozen bottom nav, demotes Messages, and forces a full `StatefulShellRoute`
> rewrite. **Command-Palette-first shell** — 7/9/5; best discoverability but bolts on two
> precedent-free subsystems (adaptive two-pane + voice) for v1.

### 3.1 Bottom nav (landlord) — unchanged spine

```
LANDLORD BOTTOM NAV — 4 items + center-docked Capture FAB

┌──────────────────────────────────────────────────────────────┐
│                         ╭───────╮                              │
│                         │   +   │  ← Scan / Add (FAB)          │
├────────────────────────╯       ╰────────────────────────────┤
│   Today      Money     « gap »     Work      Messages         │
│   ◉today      $          64px       build       chat          │
│  ▔▔▔▔▔                                                         │
│  selected = violet primaryContainer pill, Symbol FILL 0→1     │
│  unselected = icon-only (FILL 0).  height 64, hairline top    │
└──────────────────────────────────────────────────────────────┘
  (tenant mode: NO FAB, no centerGap — Home/Messages/Maint/More)
```

Slots: **Today** (Daily Briefing, index 0) · **Money** (opens Money hub) · **Capture**
(center-docked FAB, "Scan / Add"; not a tab; hidden for tenants) · **Work** (opens Work
hub) · **Messages** (opens Inbox hub; label stays "Messages").

### 3.2 The flyout drawer (replaces the Browse button) — mirrors the web AppShell

```
OPEN LEFT FLYOUT DRAWER — mirrors the web AppShell order

┌──────────────────────────────┬────────────────────┐
│ ◔  Frank Cole            ▾   │░░░░░░░░░░░░░░░░░░░░░│
│    Maple St LLC  (switch)    │░  dimmed hub/scrim ░│
│ ┌──────────────────────────┐ │░░░░░░░░░░░░░░░░░░░░░│
│ │ 🔍 Search or say a command│ │                    │
│ └──────────────────────────┘ │                    │
│  ★ Today                     │                    │
│  ★ Scan / Add                │                    │
│ ──────────────────────────── │                    │
│  MONEY                   [▾]  │                    │
│    Money  Payments  Expenses │                    │
│    Deposits  Banking  Reports│                    │
│  RENTALS                [▸]   │  one group open    │
│  WORK                   [▸]   │  at a time; the    │
│  INBOX                  [▸]   │  active route      │
│ ──────────────────────────── │  auto-expands      │
│    Ask (AI)          Help     │                    │
│ ──────────────────────────── │                    │
│  SETTINGS               [▸]   │                    │
│    Settings · Team · Owners · │                    │
│    Activity history           │                    │
│  ⏻ Sign out   (confirm)       │                    │
└──────────────────────────────┴────────────────────┘
```

Drawer contents:

- **Header (pinned):** account avatar + name + portfolio/entity switcher (→ Settings hub);
  a **"Search or say a command…"** field that opens the Command Palette (mic affordance
  present; voice routing deferred).
- **Pinned links:** Today · Scan / Add (mirrors the web's two pinned links).
- **Money:** Money · Payments · Expenses · Security deposits · Banking · Reports.
- **Rentals:** Getting started · Properties · Tenants · Leases · Applications.
- **Work:** Work orders · Appointments · Inspections · Recurring maintenance · Vendors.
- **Inbox:** Messages · Tenant notices · Notifications.
- **Bottom rail:** Ask (single AI doorway → Briefing + Ask) · Help.
- **Settings hub:** Settings · Team · Owners · Activity history (Admin-only, when added) ·
  Sign out (drawer footer, with a confirm dialog).

### 3.3 Hubs with top tabs (the "more tabs on top")

```
HUB WITH TOP TABS — Money hub (bottom-nav slot 2)

┌──────────────────────────────────────────────────────────────┐
│ ☰   Money                              🔍    🔔³          │ ← hamburger | search | bell
├──────────────────────────────────────────────────────────────┤
│ This week   +$4,200 in   −$1,150 out   [ Who owes me → ]  │ ← collapsing snapshot header
├──────────────────────────────────────────────────────────────┤
│ Snapshot │ Payments │ Expenses │ Deposits │ Banking       │ ← M3 TabBar (≤5, cyan indicator)
│ ════════                                                  │
├──────────────────────────────────────────────────────────────┤
│   « active tab body = existing PaymentsScreen content »    │
│   swipe L/R = change tab · no push, no back-out            │
│                                            ╭───────────╮  │
│                                            │ + Record   │  │ ← ONE contextual hub FAB
├────────────────────────────────────────────┴───────────┴─┤
│  Today      Money      ( + )      Work      Messages       │
└──────────────────────────────────────────────────────────┘
  (Reports = a sub-hub from the Money drawer group: Insights | Owner reports)
```

| Hub (entry)                                                  | Top tabs                                                           |
| ------------------------------------------------------------ | ------------------------------------------------------------------ |
| **Money** (bottom-nav slot 2)                          | Snapshot · Payments · Expenses · Deposits · Banking            |
| **Reports** (sub-hub from Money → "Reports")          | Insights · Owner reports                                          |
| **Work** (bottom-nav slot 4)                           | Work orders · Appointments · Inspections · Recurring · Vendors |
| **Rentals** (drawer → Rentals)                        | Properties · Tenants · Leases · Applications                    |
| **Inbox** (bottom-nav slot 5, label "Messages")        | Messages · Notices · Alerts                                      |
| **Ask** (drawer bottom rail; existing `ai_tab.dart`) | Briefing · Ask                                                    |

Every hub caps at **≤5 tabs**, uses one **contextual hub FAB** whose action swaps per tab
(kills the double-FAB / nested-Scaffold bug), and remembers its last-used tab.

### 3.4 Where the cross-cutting things live

- **Capture:** unchanged center-docked FAB ("Scan / Add") + a pinned drawer link + palette
  actions ("scan a lease", "log expense"). Only docked FAB. No Capture in tenant mode.
- **AI:** single **"Ask"** doorway in the drawer bottom rail → `AiTab` (Briefing + Ask).
  The Today "Ask" quick action and palette suggestion route to the same place. The
  orphaned Browse › AI tile is removed.
- **Notifications:** primary home is Inbox hub → **Alerts** tab; a mirrored bell with
  unread badge stays top-right in each hub AppBar for one-tap access; also listed in the
  drawer Inbox group. Removed from competing with navigation in the AppBar.
- **Account / Sign out:** drawer header account block → Settings hub; **Sign out** lives in
  the drawer footer behind a confirm dialog, removed from all AppBars and de-duplicated.
- **Search:** global **Command Palette** (text-first v1) — full-screen overlay over
  ACTIONS ("Record payment"), RECORDS (tenants/units/properties/leases/payments/expenses/
  work orders/vendors via a **server-side, paged, single cross-entity query** — never
  in-memory), and SCREENS by name; empty state = Recents + suggested actions. Voice
  mic-to-text is a later phase.
- **Sandbox indicator:** unchanged slim `_SandboxIndicator` bar above the hub AppBar/body.

### 3.5 Tenant shell (cleanup only — frozen 4-tab set, no drawer/FAB/palette)

```
TENANT SHELL — 4 tabs, NO FAB, NO drawer, NO palette

┌──────────────────────────────────────────────────────────────┐
│  Home                                            🔔       │
├──────────────────────────────────────────────────────────────┤
│   Balance due $1,200      Next rent  Jul 1                 │
│   [ Pay now → Stripe ]        [ Autopay: Off ]            │
│   Account history →   (single canonical entry)            │
│   Open requests · 1                                        │
├──────────────────────────────────────────────────────────────┤
│  Maintenance tab ▸  [ My requests | New request ]         │
│  (New request is its own clear tab — not list+form mash)  │
├──────────────────────────────────────────────────────────────┤
│   Home        Messages       Maintenance        More      │
└──────────────────────────────────────────────────────────┘
  More ▸  Account history · Lease · Appointments · Settings
          ⏻ Sign out (confirm — appears exactly once)
```

Tenant changes are minimal: split "New request" into its own Maintenance tab; one
canonical Account-history entry; Sign out once, in More, with confirm. No drawer, FAB, or
palette (this surface is small and rarely used).

---

## 4. Full destination mapping (every current item → new home)

> Every one of the 19 buried destinations gets a clear, shallower home. Detail screens
> (payment/expense/tenant/lease/etc. detail) keep their existing addressable routes and
> push within the relevant hub branch.

| Item                                                                   | Old location                          | New home                                                                      | Mechanism             |
| ---------------------------------------------------------------------- | ------------------------------------- | ----------------------------------------------------------------------------- | --------------------- |
| Today dashboard                                                        | Bottom-nav 1                          | Bottom-nav 1 (unchanged)                                                      | bottom nav            |
| Money                                                                  | Bottom-nav 2                          | Money hub → Snapshot tab                                                     | bottom nav → hub tab |
| Capture                                                                | Center FAB                            | Center FAB + pinned drawer link + palette action                              | FAB sheet             |
| Work orders                                                            | Bottom-nav 4                          | Work hub → Work orders tab                                                   | bottom nav → hub tab |
| Messages                                                               | Bottom-nav 5                          | Inbox hub → Messages tab                                                     | bottom nav → hub tab |
| **Browse (MoreTab)**                                             | Today AppBar grid icon                | **Removed** — replaced by drawer + palette                             | deleted               |
| AI Assistant                                                           | Today "Ask"**and** Browse › AI | Drawer "Ask" (single doorway); Today "Ask" = shortcut                         | drawer + shortcut     |
| Notifications                                                          | Today AppBar bell +`/notifications` | Inbox → Alerts tab + per-hub bell + drawer                                   | hub tab + AppBar      |
| Overdue / Who owes me                                                  | Money snapshot + voice                | Money → Snapshot "Who owes me" chip                                          | hub action chip       |
| Getting started                                                        | Today card + Browse › Rentals        | Drawer Rentals + Today card                                                   | drawer + card         |
| Payments                                                               | Browse › Money + capture "Type it"   | Money → Payments tab                                                         | hub tab               |
| Expenses                                                               | Browse › Money                       | Money → Expenses tab                                                         | hub tab               |
| Security deposits                                                      | Browse › Money                       | Money → Deposits tab                                                         | hub tab               |
| Banking                                                                | Browse › Money                       | Money → Banking tab                                                          | hub tab               |
| Owner reports                                                          | Browse › Money                       | Reports sub-hub → Owner reports                                              | hub tab               |
| Insights                                                               | Browse › Money                       | Reports sub-hub → Insights                                                   | hub tab               |
| Properties                                                             | Browse › Rentals                     | Rentals hub → Properties tab                                                 | drawer → hub tab     |
| Tenants                                                                | Browse › Rentals                     | Rentals hub → Tenants tab                                                    | drawer → hub tab     |
| Leases                                                                 | Browse › Rentals                     | Rentals hub → Leases tab                                                     | drawer → hub tab     |
| Applications                                                           | Browse › Rentals                     | Rentals hub → Applications tab                                               | drawer → hub tab     |
| Appointments                                                           | Browse › Work                        | Work hub → Appointments tab                                                  | hub tab               |
| Inspections                                                            | Browse › Work                        | Work hub → Inspections tab                                                   | hub tab               |
| Recurring maintenance                                                  | Browse › Work                        | Work hub → Recurring tab                                                     | hub tab               |
| Vendors                                                                | Browse › Work                        | Work hub → Vendors tab                                                       | hub tab               |
| **Notices**                                                      | Browse › Work                        | **Inbox hub → Notices tab** + drawer (web parity — moved out of Work) | hub tab + drawer      |
| Team                                                                   | Browse › Admin                       | Drawer → Settings hub → Team                                                | drawer                |
| Settings                                                               | Browse › Admin                       | Drawer → Settings hub (via account header)                                   | drawer                |
| Global search                                                          | Substring filter inside Browse        | Command Palette (drawer field + per-hub search icon)                          | command palette       |
| `scan_tab` / `scan_list_screen` / `scan_capture`                 | Dead code (no refs)                   | **Deleted**                                                             | deleted               |
| `properties_tab.dart`                                                | Dead nested-Navigator                 | **Deleted** (→ Rentals hub)                                            | deleted               |
| Detail screens (payment/expense/tenant/lease/property/work-order/etc.) | various +`/…/:id`                  | Push within the owning hub branch; routes unchanged                           | detail route          |

---

## 5. Cross-surface consistency & constraints honored

- **Frozen group names** kept (`Rentals`, `Work`, `Money`, etc.); drawer order mirrors the
  web `AppShell.svelte`; **Notices under Inbox** matches web.
- **Frozen bottom nav** (Today/Money/Capture/Work/Messages) and the **single "Ask"** AI
  doorway preserved; "Money" (not "Accounting").
- **Design system reused**: `M3MorphNavItem` for drawer + nav items, the existing
  `navigationDrawerTheme`, the `ai_tab.dart` TabBar pattern, art bands, tonal icon chips.
- **Data rule honored**: the Command Palette's search is one **server-side, paged,
  cross-entity** query — no in-memory grouping/filtering.

---

## 6. Open questions (your decisions)

1. **Notices placement:** follow web and put Tenant notices under **Inbox** (not Work)?
   Or also keep a Notices shortcut in Work?
2. **Inbox hub vs plain Messages:** OK that tapping "Messages" lands on a tabbed Inbox hub
   (Messages/Notices/Alerts) rather than the raw list?
3. **Command Palette v1 = text-only** (defer voice intent routing) — agreed?
4. **Search backend:** does a cross-entity search endpoint already exist, or is this
   net-new API work? (Gates Phase 4.)
5. **StatefulShellRoute timing:** keep it as an isolated later phase (lower blast radius)
   and let v1 keep the current `IndexedStack` back-stack behavior — okay?
6. **Reports sub-hub:** fold Owner reports + Insights into a "Reports" surface (one extra
   tap) vs six flat Money rows — confirm the fold?
7. **"Who owes me" label:** converge the current "Who's behind" title to the glossary
   canonical "Who owes me"?
8. **Drawer side:** left drawer + hamburger (matches web), or end/right drawer for
   right-handed reach?
9. **Tenant Maintenance:** "New request" as its own tab (proposed) vs a single prominent
   button that opens a sheet?
10. **Activity history / Owners** aren't on mobile yet — show as disabled "coming soon"
    rows in the Settings hub for web parity, or omit until built?
