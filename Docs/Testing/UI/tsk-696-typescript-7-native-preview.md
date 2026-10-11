# TSK-696 TypeScript 7 Native Preview Verification

Worktree: `/Users/blackcolours/dev/work/worktrees/rental-management/tsk-696-typescript-7-native-preview`

## Scope

Verify that adopting the Go-based TypeScript 7 compiler alongside the TypeScript 6 compatibility API preserves Svelte diagnostics, production builds, server startup, and browser hydration.

## Static checks

1. Run `pnpm --dir web check` and assert Svelte diagnostics complete with no errors.
2. Run `pnpm --dir web check:native` and assert the TypeScript 7 native compiler completes with no errors.
3. Run `pnpm --dir web build` and assert the SvelteKit production build succeeds.

## Browser checks

1. Start the production preview on an isolated localhost port.
2. Open `/welcome` and assert the Rental Command welcome page renders.
3. Open `/login` and assert the sign-in form renders and remains interactive.
4. Open `/` without authentication and assert the app redirects to the public `/welcome` route.
5. Confirm the browser console has no page errors during those navigations.

## Acceptance

- The existing `check` command continues using the compatibility compiler API required by Svelte.
- `check:native` runs the TypeScript 7.0 Go compiler.
- Production output starts and hydrates in a real browser.

## Evidence

Playwright CLI verification against the production preview at `http://127.0.0.1:5697` on 2026-07-11:

- TypeScript 7.0.2 checked the generated TypeScript/JavaScript graph in 1.20 seconds; the side-by-side TypeScript 6.0.3 compiler took 4.07 seconds.
- `pnpm check:native`, `pnpm check`, `pnpm build`, and all 451 web unit tests passed.
- `/welcome` rendered the Rental Command marketing page with the expected title and navigation.
- The `Sign in` navigation opened `/login`; both login fields accepted input.
- An unauthenticated request to `/` redirected to `/welcome`. The guide initially expected `/login`; inspection showed this was a guide assumption, not a regression.
- The browser reported zero console errors and zero console warnings.
