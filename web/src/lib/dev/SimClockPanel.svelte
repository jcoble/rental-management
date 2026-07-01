<script lang="ts">
	/**
	 * Dev-only floating control for the master simulation clock (spec §7.3). Mounted from the root
	 * +layout.svelte behind `PUBLIC_SIMULATION_ENABLED`. Shows sim-now live and drives the /dev/clock
	 * endpoints (set-date / +1d / +1w / +1m / freeze-unfreeze / reset) via the authenticated api client;
	 * each mutation's response is mirrored into the inline Date shim immediately for snappy feedback (the
	 * ~1s poll keeps it synced thereafter).
	 */
	import { api } from '$lib/api/client';
	import { applySimClock } from '$lib/dev/sim-clock-client';

	interface ClockState {
		simNowUtc: string;
		mode: string;
		timeZoneId: string | null;
		offsetSeconds: number;
	}

	let now = $state('—');
	let mode = $state('real');
	let dateInput = $state('');
	let busy = $state(false);
	let collapsed = $state(false);

	function currentSimIso(): string {
		try {
			return new Date().toISOString(); // shimmed Date → simulated time
		} catch {
			return '—';
		}
	}

	// Client-only (never during SSR): seed the mode from the shim + tick sim-now once a second.
	$effect(() => {
		if (window.__simClock) mode = window.__simClock.getState().mode;
		now = currentSimIso();
		const id = setInterval(() => {
			now = currentSimIso();
		}, 1000);
		return () => clearInterval(id);
	});

	async function run(action: () => Promise<ClockState>): Promise<void> {
		if (busy) return;
		busy = true;
		try {
			const state = await action();
			applySimClock(state); // instant shim update; the poll keeps it synced afterward
			mode = (state.mode || 'real').toLowerCase();
			now = currentSimIso();
		} catch (error) {
			console.error('SimClockPanel action failed', error);
		} finally {
			busy = false;
		}
	}

	const setDate = () =>
		dateInput ? run(() => api.post<ClockState>('/dev/clock/set', { date: dateInput })) : undefined;
	const advance = (days: number) => run(() => api.post<ClockState>('/dev/clock/advance', { days }));
	const freeze = () => run(() => api.post<ClockState>('/dev/clock/freeze'));
	const unfreeze = () => run(() => api.post<ClockState>('/dev/clock/unfreeze'));
	const reset = () => run(() => api.post<ClockState>('/dev/clock/reset'));
</script>

<div class="sim-panel" class:sim-collapsed={collapsed}>
	<button class="sim-header" onclick={() => (collapsed = !collapsed)} title="Toggle sim-clock panel">
		<span class="sim-dot" class:sim-frozen={mode === 'frozen'}></span>
		<span class="sim-name">SIM CLOCK</span>
		<span class="sim-mode">{mode}</span>
	</button>

	{#if !collapsed}
		<div class="sim-body">
			<div class="sim-now" title={now}>{now}</div>

			<div class="sim-row">
				<input type="date" bind:value={dateInput} aria-label="Set simulated date" />
				<button onclick={setDate} disabled={busy || !dateInput}>Set</button>
			</div>

			<div class="sim-row">
				<button onclick={() => advance(1)} disabled={busy}>+1d</button>
				<button onclick={() => advance(7)} disabled={busy}>+1w</button>
				<button onclick={() => advance(30)} disabled={busy}>+1m</button>
			</div>

			<div class="sim-row">
				{#if mode === 'frozen'}
					<button onclick={unfreeze} disabled={busy}>Unfreeze</button>
				{:else}
					<button onclick={freeze} disabled={busy}>Freeze</button>
				{/if}
				<button onclick={reset} disabled={busy}>Reset</button>
			</div>
		</div>
	{/if}
</div>

<style>
	.sim-panel {
		position: fixed;
		left: 12px;
		bottom: 12px;
		z-index: 99999;
		width: 224px;
		font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
		font-size: 12px;
		color: #e9e5f5;
		background: rgba(20, 18, 28, 0.95);
		border: 1px solid rgba(124, 111, 245, 0.5);
		border-radius: 10px;
		box-shadow: 0 6px 24px rgba(0, 0, 0, 0.4);
		overflow: hidden;
	}
	.sim-header {
		display: flex;
		align-items: center;
		gap: 8px;
		width: 100%;
		padding: 8px 10px;
		background: rgba(124, 111, 245, 0.14);
		border: 0;
		color: inherit;
		font: inherit;
		cursor: pointer;
	}
	.sim-dot {
		width: 8px;
		height: 8px;
		border-radius: 50%;
		background: #6ee7a8;
		box-shadow: 0 0 8px #6ee7a8;
	}
	.sim-dot.sim-frozen {
		background: #7cc7ff;
		box-shadow: 0 0 8px #7cc7ff;
	}
	.sim-name {
		font-weight: 700;
		letter-spacing: 0.12em;
	}
	.sim-mode {
		margin-left: auto;
		text-transform: uppercase;
		font-size: 10px;
		opacity: 0.75;
	}
	.sim-body {
		display: flex;
		flex-direction: column;
		gap: 8px;
		padding: 10px;
	}
	.sim-now {
		color: #cfc8e6;
		word-break: break-all;
	}
	.sim-row {
		display: flex;
		gap: 6px;
	}
	.sim-row > * {
		flex: 1;
	}
	.sim-panel button:not(.sim-header) {
		background: rgba(124, 111, 245, 0.18);
		color: #e9e5f5;
		border: 1px solid rgba(124, 111, 245, 0.4);
		border-radius: 6px;
		padding: 5px 6px;
		font: inherit;
		cursor: pointer;
	}
	.sim-panel button:disabled {
		opacity: 0.5;
		cursor: default;
	}
	.sim-panel input[type='date'] {
		flex: 2;
		min-width: 0;
		background: rgba(0, 0, 0, 0.3);
		color: #e9e5f5;
		border: 1px solid rgba(124, 111, 245, 0.35);
		border-radius: 6px;
		padding: 4px 6px;
		font: inherit;
	}
</style>
