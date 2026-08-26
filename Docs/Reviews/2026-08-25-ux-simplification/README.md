# UX simplification review — 25 August 2026 (TSK-947)

Owner's brief: "a round for both the web and mobile to see what we need to make simpler in the UI so
the flows are easier, so that a landlord isn't overwhelmed by so much of this."

Four independent read-only reviews, two per app:

| Report | App | Lens | Reviewer |
|---|---|---|---|
| [web-opus.md](web-opus.md) | web | what the landlord sees (drove the live app at 1710×990, 33 screenshots) | Opus 5 |
| [web-sol.md](web-sol.md) | web | flow and plumbing (static) | SOL medium |
| [mobile-opus.md](mobile-opus.md) | mobile | what the landlord sees (static; the Flutter web build stalls on the build box) | Opus 5 |
| [mobile-sol.md](mobile-sol.md) | mobile | flow and plumbing (static) | SOL medium |

Consolidated proposal for item-by-item approval: **[landlord-first-cut.html](landlord-first-cut.html)** —
29 proposals, two decide-first flags, a 30-row rename table, and the intrinsic complexity that stays.
Decisions made on the page persist in the viewer's browser only; record the outcome in TSK-947.

Where all four reviews landed: recording an expense (7-step/26-field wizard on web, 4-step form on
mobile), logging a repair (6 and 5 steps), adding a property (owner entity, fees and tax basis before
the first rental), bookkeeping vocabulary on daily screens, and database language in the UI
("atomic move-in", "Return possession", "the server will recheck…").

Decide first: the web dashboard, Money strip and Money overview show three different "who owes"
totals on the same sample data (likely a query-scoping bug); and a solo landlord cannot reach
listings on the web because they live only in the Leasing-role shell.

Nothing here is implemented until approved.
