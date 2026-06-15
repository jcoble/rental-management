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
const SETTLED_KEY_PREFIX = 'rc.getStarted.settled.';

function storageKey(portfolioId: number): string {
	return `${KEY_PREFIX}${portfolioId}`;
}

function settledKey(portfolioId: number): string {
	return `${SETTLED_KEY_PREFIX}${portfolioId}`;
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
		// "Start over" should also re-open the checklist's settled/dismissed state so the optional-task
		// queries run again and the card can reappear.
		localStorage.removeItem(settledKey(portfolioId));
	} catch {
		/* ignore */
	}
}

/**
 * Has this portfolio's checklist been "settled" — everything (core + optional) seen done at least once?
 * Used as a cheap, persisted gate so the dashboard's hot path can skip the optional-only queries
 * (notification email/settings, sandbox state) for a fully-set-up landlord. Cleared by "start over".
 */
export function isChecklistSettled(portfolioId: number): boolean {
	if (!browser || portfolioId <= 0) return false;
	try {
		return localStorage.getItem(settledKey(portfolioId)) === '1';
	} catch {
		return false;
	}
}

/** Mark this portfolio's checklist settled (called once everything is observed done). */
export function markChecklistSettled(portfolioId: number): void {
	if (!browser || portfolioId <= 0) return;
	try {
		localStorage.setItem(settledKey(portfolioId), '1');
	} catch {
		/* storage may be unavailable — the gate just won't persist, queries run as before. */
	}
}
