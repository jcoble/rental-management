# TSK-102 M3 Design Port

## Scope

Port the customized M3 Expressive direction proven in EdiPlatform into Rental Command without rewriting every screen in one pass or making the landlord workflow feel decorative.

## Acceptance

- Web has shared M3-aligned color, type, shape, surface, elevation, and motion tokens.
- Web shell and dashboard use the new surface language with restrained expressive color.
- Buttons, cards, common fields, dialogs, date pickers, tooltips, and inline read-only values gain M3 state and motion treatment through shared primitives.
- Native browser `title` tooltips are removed from the migrated shell and help tooltip surfaces.
- Mobile theme uses matching M3 roles, radius, typography, and component theming through `ThemeData`.
- Verification includes web static checks/build, Flutter analysis, and browser screenshots for desktop/mobile dashboard plus one inline detail surface.

## Plan

- [x] Add web M3 token layer and Google Sans Flex import.
- [x] Retune shared web button/card/input/select/dialog/tooltip primitives for M3 shape, state, and motion.
- [x] Update shell/dashboard visuals using shared tokens, delegated M3 tooltips, page route motion, and restrained expressive color.
- [x] Improve shared inline read-only fields so large detail forms are more scannable but still compact.
- [x] Integrate Flutter mobile theme parity and auth-screen first treatment.
- [x] Verify with `pnpm --dir web check`, `pnpm --dir web build`, Flutter analysis, and Playwright browser evidence.

## Notes

- Keep the product quiet and operational: the user is a non-technical landlord managing real tasks, not browsing a creative landing page.
- Use expressive color as muted surface patterning and state accents, not colored body text everywhere.
- No backend, API, data model, or deployment architecture impact expected.

## Verification

- `rtk git diff --check` passed.
- `rtk pnpm --dir web check` passed: `svelte-check found 0 errors and 0 warnings`.
- `rtk pnpm --dir web build` passed. Existing dependency warnings observed: Node `module.register()` deprecation, Rollup pure-comment warnings in `@microsoft/signalr`, and `@internationalized/date` circular dependency notices.
- `rtk flutter analyze` in `mobile/` passed: `No issues found!`.
- Local stack reused: web `https://localhost:5667`, API `https://localhost:5666`.
- Login used for browser verification: `admin@rentalcommand.local`.
- Playwright evidence:
  - Dashboard desktop screenshot: `output/playwright/tsk-102-m3/dashboard-desktop.png`.
  - Dashboard mobile screenshot: `output/playwright/tsk-102-m3/dashboard-mobile.png`.
  - Property inline-field screenshot: `output/playwright/tsk-102-m3/property-inline-fields.png`.
  - Header tooltip hover screenshot: `output/playwright/tsk-102-m3/header-tooltip.png`.
- Browser assertions passed:
  - dashboard hero visible and using softened radial M3 expressive fields.
  - no repeating-line pattern remains in the hero backdrop.
  - Google Sans Flex active on `body`.
  - route transition frame present.
  - delegated M3 tooltip targets present, with `nativeTitleCount=0`.
  - property detail read-only inline fields rendered with compact `m3-readonly-field` surfaces.
  - mobile dashboard has no horizontal overflow at `390x844`.
