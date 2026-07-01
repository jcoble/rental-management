/**
 * Real (non-simulated) time source for the client.
 *
 * When the dev sim-clock shim (see `sim-clock-shim.ts`) is installed it replaces the global `Date`, so
 * bare `Date.now()` / `new Date()` return SIMULATED time for DISPLAY. Anything that must stay on the REAL
 * clock — above all client-side JWT expiry math (M3): advancing the sim clock must NOT make live tokens
 * read as expired and trigger refresh storms / refresh-family revocation — calls {@link realNow} instead.
 *
 * Safe in every context: on the server (SSR) there is no shim, so this is the real clock; on the client
 * the shim stashes the untouched constructor on `window.__RealDate`; with no shim (production, or the flag
 * off) `Date` was never replaced, so the fallback is already real.
 */
export function realNow(): number {
	if (typeof window !== 'undefined' && window.__RealDate) {
		return window.__RealDate.now();
	}
	return Date.now();
}

/** Shape of the `window.__simClock` control the inline shim installs (used by the client sync bootstrap). */
export interface SimClockController {
	/** Real epoch ms, bypassing the shim. */
	realNow(): number;
	/** Current shim state (for debugging / the dev panel). */
	getState(): { mode: string; offsetMs: number; anchorMs: number };
	/** Point the shim at a new offset/mode/anchor (called ~1s by the sync bootstrap). */
	setOffset(offsetMs: number, mode: string, anchorMs: number): void;
}

declare global {
	interface Window {
		/** The original `Date` constructor, stashed by the shim before it replaces the global. */
		__RealDate?: DateConstructor;
		/** The sim-clock control installed by the inline shim (dev-only). */
		__simClock?: SimClockController;
	}
}
