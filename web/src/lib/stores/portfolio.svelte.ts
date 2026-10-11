/**
 * Active-portfolio selection — Svelte 5 runes.
 *
 * The selection is client state (persisted in localStorage). Two SSR-safety
 * rules apply:
 *
 *  - **Deterministic module value (L-18).** The store initialises to the same
 *    placeholder (DEFAULT_PORTFOLIO_ID) on BOTH the server and the client module
 *    load. The real browser value is applied only by {@link initPortfolio}
 *    (called from the authenticated layout), never at module load. Reading
 *    localStorage at module load would make the server (always 1) diverge from
 *    the client (the stored id) for any user whose active portfolio isn't 1.
 *  - **Client-only writes (L-13).** This is module-scoped state; under adapter-node
 *    the server shares module scope across all concurrent requests, so a server-side
 *    write would bleed one user's selection into another's SSR render. The setter and
 *    initialiser are therefore hard client-only — they throw if invoked on the server.
 */

import { browser } from '$app/environment';
import { resolveInitialPortfolioId } from './portfolio-selection';

const STORAGE_KEY = 'rental:currentPortfolioId';
const DEFAULT_PORTFOLIO_ID = 1;

let _portfolioId = $state(DEFAULT_PORTFOLIO_ID);

export function getCurrentPortfolioId(): number {
	return _portfolioId;
}

/**
 * Client-only: reconcile the in-memory selection with the active access envelope's workspace id
 * and persisted localStorage value. The server-selected workspace is authoritative, so a
 * stale localhost value from another account must not make the first protected route query the SSR
 * placeholder. Idempotent; safe to call on every navigation. No-ops on the server (the store keeps
 * its deterministic default there).
 */
export function initPortfolio(fallbackPortfolioId?: number): void {
	if (!browser) return;
	const resolved = resolveInitialPortfolioId(localStorage.getItem(STORAGE_KEY), fallbackPortfolioId);
	if (resolved.id !== null) {
		_portfolioId = resolved.id;
		if (resolved.shouldPersist) {
			localStorage.setItem(STORAGE_KEY, String(resolved.id));
		}
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
