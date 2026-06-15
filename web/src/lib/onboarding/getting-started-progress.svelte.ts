/**
 * Per-portfolio persistence for the getting-started checklist's MANUAL overrides — the set of task
 * keys the user has explicitly "marked done" or "skipped". Auto-completion (data exists) is computed
 * live from the list queries and is NOT stored here; this only remembers the user's manual choices so
 * the checklist is re-runnable and remembers what they dismissed.
 *
 * Mirrors the onboarding wizard's `rc.onboarding.step.<portfolioId>` localStorage convention
 * (`web/src/routes/(protected)/onboarding/+page.svelte`), keyed per portfolio so switching portfolios
 * shows the right state.
 */
import { browser } from '$app/environment';

const KEY_PREFIX = 'rc.getStarted.done.';

function storageKey(portfolioId: number): string {
	return `${KEY_PREFIX}${portfolioId}`;
}

/** Read the persisted set of manually-completed/skipped task keys for a portfolio. */
export function loadManualDone(portfolioId: number): Set<string> {
	if (!browser || portfolioId <= 0) return new Set();
	try {
		const raw = localStorage.getItem(storageKey(portfolioId));
		if (!raw) return new Set();
		const parsed = JSON.parse(raw);
		return Array.isArray(parsed) ? new Set(parsed.filter((v) => typeof v === 'string')) : new Set();
	} catch {
		return new Set();
	}
}

/** Persist the manual-done set for a portfolio (no-op off the browser). */
export function saveManualDone(portfolioId: number, done: ReadonlySet<string>): void {
	if (!browser || portfolioId <= 0) return;
	try {
		localStorage.setItem(storageKey(portfolioId), JSON.stringify([...done]));
	} catch {
		/* storage may be unavailable (private mode / quota) — manual state just won't persist. */
	}
}

/** Clear all manual overrides for a portfolio (used by "start over"). */
export function clearManualDone(portfolioId: number): void {
	if (!browser || portfolioId <= 0) return;
	try {
		localStorage.removeItem(storageKey(portfolioId));
	} catch {
		/* ignore */
	}
}
