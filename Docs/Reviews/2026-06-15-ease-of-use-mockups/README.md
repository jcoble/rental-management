# Ease-of-Use restructure — proposal mockups (2026-06-15)

Visual mockups for the proposed ease-of-use restructure. These are **proposals to react to**, not built screens. They reuse the app's existing design tokens (no new visual style — flow/layout changes only). Pairs with `../2026-06-15-ease-of-use-restructure-audit.md`.

Source: `rc-mockups.html` (single self-contained file; re-render sections to regenerate the PNGs).

| # | File | Shows | Replaces |
|---|------|-------|----------|
| 1 | `rc-mock-1-web-nav.png` | Web sidebar — small "Simple" default (Today/Money/Rentals/Work/Messages/Settings) + a "Pro tools" reveal | the ~19-item exclusive accordion sidebar |
| 2 | `rc-mock-2-mobile-nav.png` | Mobile bottom nav (Today/Money/[Scan]/Work/Messages) + "what moved where" | the 5 tabs + separate 17-tile "Browse" grid |
| 3 | `rc-mock-3-firstrun-web.png` | Calmer first-run dashboard (web): welcome + "try these 3 things" | the dense "14 things need attention" wall on day one |
| 4 | `rc-mock-4-firstrun-mobile.png` | Calmer first-run dashboard (mobile) | same, mobile |
| 5 | `rc-mock-5-money-web.png` | One Money hub, Activity/Summary tabs (Reports/Tax/Owner statements one tap away) | 5+ scattered money surfaces |
| 6 | `rc-mock-6-money-mobile.png` | Money Summary tab (mobile) | separate Banking/Owner-reports/Insights destinations |
| 7a | `rc-mock-7-scan-mobile.png` | **Scan import (mobile, REVISED):** multi-page photo/PDF → real create forms PRE-FILLED + Google-Places address confirm + "add more" → save | the 20-field blank Create-vs-Link form |
| 7b | `rc-mock-7b-scan-web.png` | Scan import (web, same flow) | same |
| 8 | `rc-mock-8-coachmark.png` | Guided walkthrough / coachmark (mobile) — dim + spotlight + Next/Skip | nothing (no mobile teaching today) |

## Decisions / refinements locked in this session

- **Language:** do NOT dumb down professional terms (Net, Work Orders, Receivables, Schedule E, etc.) — target users are experienced landlords who know the domain; they're just not tech-savvy. Simplify **flow/structure**, not vocabulary. Only fix genuine inconsistency (Work Order / Field queue / Maintenance → one term) and truly-techy words (Sandbox).
- **Keep:** the sandbox feature and the ~20s "seeding" screen (deliberate, owner likes them). The existing styling/theme stays untouched.
- **Scan flow (mock 7):** not a one-tap "trust it" card — the scan PRE-FILLS the real create forms; user reviews/edits/adds detail; the address uses Google Places autocomplete to confirm a real address. Input is multi-page (phone photos strung together, or a PDF). Both web + mobile.
- **"Send reminder" must be CONTEXTUAL** — tied to the specific overdue lease/payment/tenant (reuses the per-lease draft from `NoticeDraftWorker`), NOT a standalone Notices screen.
- **Per-notice-type "Auto-send vs Ask me first"** toggle (new task) — let each notice type be auto-sent or held for approval; default Ask-first; set once during onboarding.

## Backend facts confirmed (for the reminder/automation design)

- `RentalCommand.Engine/Workers/`: `LateFeeWorker` (every 6h, applies late fees), `NoticeDraftWorker` (every 12h, runs unconditionally, **drafts** late/expiry notices — never auto-sends, one-tap approval), `RentChargeWorker`, `AutopayChargeWorker`, `DailyBriefingDeliveryWorker`, `RecurringMaintenanceWorker`.
- `NotificationSettings`: `EnableRentCharges`, `EnableLateFees`, `EnableLeaseExpiryReminders` (default true), `NotifyTenants` (global tenant-send, default **off**).
