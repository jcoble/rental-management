# Harden auth refresh under heavy concurrent load (TSK-14)

> **For agentic workers:** small, surgical web-only change. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the web client from emitting multiple near-simultaneous token refreshes during a burst of concurrent API calls, so a page that fires N requests on mount triggers exactly ONE refresh instead of several (which trip the server's single-use / reuse-detection logic and bounce the user to `/login`).

**Architecture:** Client-side only. Extend the existing client single-flight (`web/src/lib/api/client.ts`, `web/src/lib/utils/single-flight.ts`) with a brief *result-reuse window*, mirroring the proven durable-cache design already used server-side in `web/src/lib/server/token-refresh.ts`. The SERVER token rotation, single-use semantics, and reuse-revokes-family security are **unchanged**.

**Tech Stack:** SvelteKit 5, TypeScript, Node v24 built-in test runner (`node:test`) with native TS type-stripping (no new test dependency added).

---

## The race, precisely

Symptom: under the full e2e suite, list pages that fan out many queries on mount (Appointments fires ~4: `appointmentsQuery`, `calendarQuery`, `propertiesQuery`, `tenantsQuery`; Leases similar) intermittently bounce to `/login`. They pass in isolation. Each test logs in fresh, so it is NOT token expiry.

Root cause is a **client refresh burst**, not a server bug:

1. On mount, N queries enter `fetchApi` near-simultaneously while the access token is at/near expiry.
2. The proactive guard `isTokenExpired(120)` is per-call and timing-sensitive: some calls refresh proactively, others skip it (token still has >120s) and send with the current token, then receive a **401** from the API.
3. Each 401 enters the reactive retry branch and calls `refreshToken()`.
4. `refreshToken` is `createSingleFlight(performTokenRefresh)`. `createSingleFlight` clears its in-flight promise the instant it settles. So it only coalesces calls whose lifetimes **overlap in time**. A 401 that arrives a few ms *after* the first refresh already resolved starts a **second** real `POST /api/auth/refresh`.
5. The first refresh already rotated the refresh cookie (single-use). The second refresh now races the server's reuse-detection / 30s grace window. Under extreme burst the second (or third) rotation, plus a straggler still holding a prior-generation token, trips the server's reuse-revokes-family protection → the whole session is killed → bounce to `/login`.

The Wave-1 fix (durable 30s server cache keyed by the OLD token value + a 30s API reuse-grace window) collapses *most* of these into one rotation, but it cannot help when the **client** presents the freshly-rotated NEW cookie to a second refresh (that token is legitimately valid, so the API rotates again), creating a second generation that a straggler can still reuse-trip.

**Fix:** make the client emit ONE refresh per burst. After a successful refresh, the client holds the result briefly so any refresh request arriving within a short window returns immediately (the in-memory access token is already fresh) instead of POSTing again. This is the same "durable reuse window" idea already used server-side — applied to the client single-flight.

## Why this does NOT weaken security

- No server change. Token rotation, single-use refresh tokens, and reuse-revokes-family stay exactly as-is.
- The client window only *suppresses redundant client-initiated refreshes*; it never reuses a refresh **token** against the API (the httpOnly refresh cookie is server-owned and never read by client JS). It reuses the *access token already in the store*, which is the normal, valid token for that window.
- A genuine later expiry (beyond the short window) refreshes normally. A failed refresh is NOT cached, so failures still surface immediately.

## File structure

- Modify: `web/src/lib/utils/single-flight.ts` — add an opt-in `createSingleFlightWithReuse(operation, reuseWindowMs)` helper: coalesces overlapping calls AND re-serves the last successful result for `reuseWindowMs`. Existing `createSingleFlight` is left intact (used elsewhere? verify) so the change is additive.
- Modify: `web/src/lib/api/client.ts` — build `refreshToken` from the reuse-window variant with a short TTL (e.g. 1500ms — long enough to cover a same-mount burst of concurrent + slightly-staggered 401s, far short of the access token's ~15-min life). No other logic change.
- Create: `web/src/lib/utils/single-flight.test.ts` — `node:test` unit tests for both coalescing behaviours.

---

### Task 1: Add reuse-window single-flight helper (TDD)

**Files:**
- Modify: `web/src/lib/utils/single-flight.ts`
- Test: `web/src/lib/utils/single-flight.test.ts`

- [ ] **Step 1: Write failing tests** in `web/src/lib/utils/single-flight.test.ts`:

```ts
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createSingleFlight, createSingleFlightWithReuse } from './single-flight.ts';

// Existing behaviour: overlapping calls share one in-flight run.
test('createSingleFlight coalesces overlapping calls', async () => {
	let calls = 0;
	let release!: () => void;
	const gate = new Promise<void>((r) => (release = r));
	const op = async () => {
		calls++;
		await gate;
		return calls;
	};
	const sf = createSingleFlight(op);
	const a = sf();
	const b = sf();
	release();
	assert.equal(await a, 1);
	assert.equal(await b, 1);
	assert.equal(calls, 1);
});

test('createSingleFlight runs again after the previous settled', async () => {
	let calls = 0;
	const op = async () => ++calls;
	const sf = createSingleFlight(op);
	await sf();
	await sf();
	assert.equal(calls, 2);
});

// New behaviour: a burst that is not perfectly overlapping still shares ONE run
// within the reuse window.
test('createSingleFlightWithReuse re-serves the last result within the window', async () => {
	let calls = 0;
	const op = async () => ++calls;
	const sf = createSingleFlightWithReuse(op, 1000);
	const first = await sf(); // runs once
	const second = await sf(); // within window -> reuse, no new run
	assert.equal(first, 1);
	assert.equal(second, 1);
	assert.equal(calls, 1);
});

test('createSingleFlightWithReuse refreshes again after the window elapses', async () => {
	let calls = 0;
	const op = async () => ++calls;
	const sf = createSingleFlightWithReuse(op, 20);
	await sf();
	await new Promise((r) => setTimeout(r, 40));
	await sf();
	assert.equal(calls, 2);
});

test('createSingleFlightWithReuse does NOT cache failures', async () => {
	let calls = 0;
	const op = async () => {
		calls++;
		throw new Error('boom');
	};
	const sf = createSingleFlightWithReuse(op, 1000);
	await assert.rejects(sf());
	await assert.rejects(sf());
	assert.equal(calls, 2); // each failure retried, no poisoned cache
});

test('createSingleFlightWithReuse coalesces strictly-overlapping calls too', async () => {
	let calls = 0;
	let release!: () => void;
	const gate = new Promise<void>((r) => (release = r));
	const op = async () => {
		calls++;
		await gate;
		return calls;
	};
	const sf = createSingleFlightWithReuse(op, 1000);
	const a = sf();
	const b = sf();
	const c = sf();
	release();
	assert.deepEqual(await Promise.all([a, b, c]), [1, 1, 1]);
	assert.equal(calls, 1);
});
```

- [ ] **Step 2: Run tests, confirm they fail** (helper not yet exported):

Run: `cd web && node --test --experimental-strip-types src/lib/utils/single-flight.test.ts`
Expected: FAIL — `createSingleFlightWithReuse` is not exported.

- [ ] **Step 3: Implement** in `web/src/lib/utils/single-flight.ts` — keep `createSingleFlight`, add:

```ts
/**
 * Like {@link createSingleFlight}, but additionally re-serves the LAST
 * SUCCESSFUL result for `reuseWindowMs` after it settles. This coalesces a
 * *burst* of calls (e.g. N concurrent + slightly-staggered 401 retries on page
 * mount) into a single underlying run, not just calls whose lifetimes overlap.
 *
 * Used for token refresh: N concurrent API 401s must share ONE real refresh.
 * Firing several would replay the server's single-use refresh token and trip
 * reuse-detection. Failures are never cached, so a failed refresh retries
 * immediately and surfaces to the caller.
 */
export function createSingleFlightWithReuse<TArgs extends unknown[], TResult>(
	operation: (...args: TArgs) => Promise<TResult>,
	reuseWindowMs: number
): (...args: TArgs) => Promise<TResult> {
	let inFlight: Promise<TResult> | null = null;
	let cached: { result: TResult; expiresAt: number } | null = null;

	return (...args: TArgs): Promise<TResult> => {
		if (cached && Date.now() < cached.expiresAt) {
			return Promise.resolve(cached.result);
		}
		cached = null;

		if (!inFlight) {
			inFlight = Promise.resolve(operation(...args))
				.then((result) => {
					cached = { result, expiresAt: Date.now() + reuseWindowMs };
					return result;
				})
				.finally(() => {
					inFlight = null;
				});
		}

		return inFlight;
	};
}
```

- [ ] **Step 4: Run tests, confirm pass.**

Run: `cd web && node --test --experimental-strip-types src/lib/utils/single-flight.test.ts`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit.**

---

### Task 2: Use the reuse window for the client token refresh

**Files:**
- Modify: `web/src/lib/api/client.ts`

- [ ] **Step 1: Verify** `createSingleFlight` is still imported/used elsewhere so removal would break things; we keep it. Add the new import:

```ts
import { createSingleFlightWithReuse } from '$lib/utils/single-flight';
```

- [ ] **Step 2:** Replace the `refreshToken` export. Add a named constant for the window and a short comment:

```ts
/**
 * Coalesce a BURST of refreshes (page mount fans out N API calls; some 401 and
 * each enters the retry path). A pure single-flight only dedupes calls that
 * overlap in time, so a 401 arriving a few ms after the prior refresh settled
 * would start a SECOND real refresh and replay the server's single-use token.
 * Re-serving the just-completed refresh for a brief window collapses the whole
 * burst into ONE rotation. Far shorter than the ~15-min access-token lifetime,
 * so a genuine later expiry still refreshes normally.
 */
const REFRESH_REUSE_WINDOW_MS = 1500;

export const refreshToken = createSingleFlightWithReuse(
	performTokenRefresh,
	REFRESH_REUSE_WINDOW_MS
);
```

- [ ] **Step 3: Verify** `cd web && npx svelte-check --threshold error` → 0 errors.

- [ ] **Step 4: Commit.**

---

### Task 3: Verify end-to-end

- [ ] `cd web && npx svelte-check --threshold error` → 0 errors.
- [ ] `cd web && pnpm build` → succeeds.
- [ ] `dotnet build` → succeeds (no server change expected; sanity only).
- [ ] Unit tests pass: `cd web && node --test --experimental-strip-types src/lib/utils/single-flight.test.ts`.
- [ ] If the API + web dev servers can be booted in this environment, run the Playwright e2e suite and confirm Appointments/Leases no longer bounce. If they cannot be booted here, state that explicitly — do not claim e2e passed.
- [ ] Code review (superpowers:requesting-code-review), with extra scrutiny that server-side auth security is untouched. Fix findings.

## Self-review checklist

- Spec coverage: race stated precisely (proactive/reactive split + eager single-flight clear); fix coalesces N concurrent 401s into one refresh. ✓
- No server/DB change; rotation + reuse-revokes-family intact. ✓
- Failures not cached (no poisoned auth state). ✓
- Window << access-token lifetime, so normal re-refresh still happens. ✓
