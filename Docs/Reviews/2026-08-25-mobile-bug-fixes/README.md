# Mobile bug fixes — results index (TSK-1019)

Source: `Docs/Reviews/2026-08-25-mobile-bug-review.html` (42 findings). Lanes were grouped by root
cause; every lane had a SOL medium review and a receipt in this folder.

| Finding | Status | Lane / PR |
|---|---|---|
| 1, 22, 4, 6, 12, 13, 26 (+ `appNowProvider` root cause) | Fixed | A — #621 |
| 2, 3, 35, 7, 11, 33 | Fixed | B — #618 |
| 5, 15, 36, 18, 19, 20, 21, 23, 24, 34 | Fixed | C — #619 |
| 8, 14 | Fixed (API clear flags + mobile) | F — #620 |
| 16, 17, 30, 39, 40, 27, 41, 42, 32 | Fixed | D — #622 |
| 9, 28, 29, 10, 31 | Fixed | E — #623 |
| 25 | Closed — non-reproducible (non-management experiences never reach the tab path) | C |
| 37, 38 | Closed — file deleted in TSK-1018 (`guided_rental_flow.dart`) | — |

Not done: in-app verification on a device or simulator. The Flutter web build stalls at the splash
on the build box for reasons outside these diffs (see `receipts/mob-b` in the structure audit);
coverage is unit/widget tests, with the date fixes run under `TZ=UTC` and `TZ=America/New_York`.
Also open: the two receipt clients still send different payloads (structure-audit `receipts/mob-c.md`);
that divergence was not in the 42 and is tracked separately.
