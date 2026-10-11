import { env } from '$env/dynamic/public';

/**
 * Client sync bootstrap for the master simulation clock (dev-only, spec §7.2).
 *
 * Polls the ANONYMOUS `GET /api/v1/dev/clock` (~1s — works pre-login), computes the offset from
 * `simNowUtc` vs the real clock, mirrors it into the inline Date shim (`window.__simClock`), and writes
 * the `rc_sim` cookie so the next warm reload is synchronously correct before this bootstrap re-runs.
 * No-op unless `PUBLIC_SIMULATION_ENABLED === 'true'` and the shim is installed.
 */
const POLL_MS = 1000;

interface DevClockResponse {
	simNowUtc: string;
	mode: string;
	timeZoneId: string | null;
	offsetSeconds: number;
}

export function startSimClockSync(): () => void {
	if (env.PUBLIC_SIMULATION_ENABLED !== 'true' || typeof window === 'undefined') {
		return () => {};
	}

	let stopped = false;
	let timer: ReturnType<typeof setTimeout> | undefined;

	async function tick(): Promise<void> {
		try {
			const res = await fetch('/api/v1/dev/clock', { headers: { accept: 'application/json' } });
			if (res.ok) {
				applySimClock((await res.json()) as DevClockResponse);
			}
		} catch {
			// Transient (API not up yet / offline) — just retry on the next tick.
		}
		if (!stopped) {
			timer = setTimeout(tick, POLL_MS);
		}
	}

	void tick();

	return () => {
		stopped = true;
		if (timer) clearTimeout(timer);
	};
}

/**
 * Mirror a dev-clock state (from a poll or a mutation response) into the inline Date shim and persist
 * the rc_sim cookie. Exported so the SimClockPanel can update the shim instantly after a mutation
 * instead of waiting for the next ~1s poll.
 */
export function applySimClock(body: DevClockResponse): void {
	const sim = window.__simClock;
	const RealDate = window.__RealDate;
	if (!sim || !RealDate) return;

	// Parse via RealDate so a live sim offset can't skew the parse of the incoming instant.
	const simNowMs = new RealDate(body.simNowUtc).getTime();
	if (Number.isNaN(simNowMs)) return;

	const mode = (body.mode || 'Real').toLowerCase(); // 'real' | 'frozen' | 'offset'
	const offsetMs = simNowMs - sim.realNow();

	sim.setOffset(offsetMs, mode, simNowMs);
	writeCookie({ offsetMs, mode, anchorMs: simNowMs });
}

function writeCookie(state: { offsetMs: number; mode: string; anchorMs: number }): void {
	const value = encodeURIComponent(JSON.stringify(state));
	// Session cookie (dev-only), readable synchronously by the inline shim on the next warm load.
	document.cookie = `rc_sim=${value}; path=/; SameSite=Lax`;
}
