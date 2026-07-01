import type { ClientInit } from '@sveltejs/kit';
import { startSimClockSync } from '$lib/dev/sim-clock-client';

/**
 * Client startup hook. Starts the dev-only master simulation-clock sync (no-op unless
 * PUBLIC_SIMULATION_ENABLED === 'true'). The inline shim in app.html has already patched `Date` by the
 * time this runs; the sync keeps its offset aligned with the server clock and refreshes the rc_sim
 * cookie for warm reloads.
 */
export const init: ClientInit = () => {
	startSimClockSync();
};
