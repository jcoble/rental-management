/**
 * Active-portfolio selection — Svelte 5 runes.
 *
 * The selection is client state (persisted in localStorage). Two SSR-safety
 * rules apply:
 *
 *  - **Deterministic initial value (L-18).** The store initialises to the same
 *    placeholder (DEFAULT_PORTFOLIO_ID) on BOTH the server and the very first
 *    client render, so SSR markup and first-hydration markup agree. The real
 *    localStorage value is applied only AFTER hydration, via {@link initPortfolio}
 *    (called from the authenticated layout). Reading localStorage at module load
 *    would make the server (always 1) diverge from the client (the stored id) for
 *    any user whose active portfolio isn't 1.
 *  - **Client-only writes (L-13).** This is module-scoped state; under adapter-node
 *    the server shares module scope across all concurrent requests, so a server-side
 *    write would bleed one user's selection into another's SSR render. The setter and
 *    initialiser are therefore hard client-only — they throw if invoked on the server.
 */

import { browser } from '$app/environment';

const STORAGE_KEY = 'rental:currentPortfolioId';
const DEFAULT_PORTFOLIO_ID = 1;

let _portfolioId = $state(DEFAULT_PORTFOLIO_ID);

export function getCurrentPortfolioId(): number {
	return _portfolioId;
}

/**
 * Client-only: reconcile the in-memory selection with the persisted localStorage value, falling
 * back to the authenticated user's real portfolio id when there is no stored selection yet — so a
 * brand-new account whose active portfolio isn't 1 doesn't get stuck on the SSR placeholder (which
 * made the scan duplicate-guard and list queries target the wrong portfolio). Idempotent; safe to
 * call on every navigation. No-ops on the server (the store keeps its deterministic default there).
 */
export function initPortfolio(fallbackPortfolioId?: number): void {
	if (!browser) return;
	const stored = localStorage.getItem(STORAGE_KEY);
	const parsed = stored ? parseInt(stored, 10) : NaN;
	if (Number.isInteger(parsed) && parsed > 0) {
		_portfolioId = parsed;
		return;
	}
	// No valid stored selection yet: seed from the authenticated user's real portfolio so queries
	// and the scan duplicate-guard target the right portfolio. Persist it so the choice sticks.
	if (typeof fallbackPortfolioId === 'number' && Number.isInteger(fallbackPortfolioId) && fallbackPortfolioId > 0) {
		_portfolioId = fallbackPortfolioId;
		localStorage.setItem(STORAGE_KEY, String(fallbackPortfolioId));
	}
}

export function setCurrentPortfolioId(id: number) {
	// Client-only contract: writes must never happen during SSR (would leak across
	// requests under the shared server module scope).
	if (!browser) {
		throw new Error('setCurrentPortfolioId must not be called on the server.');
	}
	_portfolioId = id;
	localStorage.setItem(STORAGE_KEY, String(id));
}
