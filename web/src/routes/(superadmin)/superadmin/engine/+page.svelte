<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { adminEngine } from '$lib/api/endpoints/adminEngine';
	import type { WorkerStatus, EngineStatusResponse } from '$lib/api/endpoints/adminEngine';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import {
		Activity,
		Server,
		Shield,
		ShieldAlert,
		RefreshCw,
		AlertTriangle,
		CheckCircle,
		XCircle,
		Clock,
		Sparkles,
		Loader2
	} from '@lucide/svelte';

	const authState = getAuthState();

	// 10s polling keeps the at-a-glance view fresh without SignalR plumbing.
	const statusQuery = createQuery(() => ({
		queryKey: ['admin-engine-status'],
		enabled: authState.isAuthenticated,
		queryFn: () => adminEngine.status(),
		refetchInterval: 10_000
	}));

	const status = $derived(statusQuery.data as EngineStatusResponse | undefined);

	// ---- Status styling ----

	const overallStyles: Record<string, { badge: string; icon: typeof CheckCircle; label: string }> = {
		Healthy: { badge: 'm3-tone-chip border m3-tone--success', icon: CheckCircle, label: 'Healthy' },
		Degraded: { badge: 'm3-tone-chip border m3-tone--warning', icon: AlertTriangle, label: 'Degraded' },
		Stale: { badge: 'm3-tone-chip border m3-tone--warning', icon: Clock, label: 'Stale' },
		Down: { badge: 'm3-tone-chip border m3-tone--error', icon: XCircle, label: 'Down' }
	};

	const healthStateStyles: Record<string, string> = {
		Healthy: 'm3-tone-chip border m3-tone--success',
		Degraded: 'm3-tone-chip border m3-tone--warning',
		Stale: 'm3-tone-chip border m3-tone--warning',
		Down: 'm3-tone-chip border m3-tone--error'
	};

	const workerStatusStyles: Record<string, string> = {
		Running: 'm3-tone-chip border m3-tone--success',
		Stopped: 'm3-tone-chip border m3-tone--neutral',
		Error: 'm3-tone-chip border m3-tone--error'
	};

	function overallCfg(s: string) {
		return overallStyles[s] ?? { badge: 'm3-tone-chip border m3-tone--neutral', icon: Clock, label: s };
	}

	// ---- Formatting ----

	function formatAgo(totalSeconds: number | null | undefined): string {
		if (totalSeconds == null || totalSeconds < 0) return 'just now';
		const s = Math.floor(totalSeconds);
		if (s < 60) return `${s}s ago`;
		const m = Math.floor(s / 60);
		if (m < 60) return `${m}m ${s % 60}s ago`;
		const h = Math.floor(m / 60);
		if (h < 24) return `${h}h ${m % 60}m ago`;
		const d = Math.floor(h / 24);
		return `${d}d ${h % 24}h ago`;
	}

	function formatUptime(totalSeconds: number | null | undefined): string {
		if (totalSeconds == null || totalSeconds < 0) return 'N/A';
		const s = Math.floor(totalSeconds);
		if (s < 60) return `${s}s`;
		const m = Math.floor(s / 60);
		if (m < 60) return `${m}m`;
		const h = Math.floor(m / 60);
		if (h < 24) return `${h}h ${m % 60}m`;
		const d = Math.floor(h / 24);
		return `${d}d ${h % 24}h`;
	}

	function prettyModel(id: string): string {
		return id && id.trim() ? id : '(not set)';
	}

	// ---- Error detail modal ----

	let errorModal = $state({ open: false, title: '', message: '' });

	function showErrorModal(worker: WorkerStatus) {
		if (!worker.lastErrorMessage) return;
		errorModal = {
			open: true,
			title: `${worker.workerName} — Last Error`,
			message: worker.lastErrorMessage
		};
	}
</script>

<svelte:head>
	<title>Engine Health - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="engine-health-page">
	<!-- Header -->
	<div class="mb-5 flex flex-wrap items-start justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Engine Health</h1>
			<p class="text-sm text-muted-foreground">
				Background worker liveness and the active extraction model.
			</p>
		</div>
		<div class="flex items-center gap-3">
			{#if status}
				{@const cfg = overallCfg(status.overallStatus)}
				<span
					class="inline-flex items-center gap-2 rounded-full px-4 py-2 text-sm font-semibold {cfg.badge}"
					data-testid="engine-overall-status"
				>
					<cfg.icon class="h-4 w-4" />
					{cfg.label}
				</span>
			{/if}
			<Button
				variant="outline"
				size="sm"
				disabled={statusQuery.isFetching}
				onclick={() => statusQuery.refetch()}
			>
				<RefreshCw class="mr-2 h-4 w-4 {statusQuery.isFetching ? 'animate-spin' : ''}" />
				Refresh
			</Button>
		</div>
	</div>

	{#if statusQuery.isPending}
		<div class="flex items-center justify-center gap-2 py-16 text-sm text-muted-foreground">
			<Loader2 class="h-4 w-4 animate-spin" />
			Loading engine status…
		</div>
	{:else if statusQuery.isError}
		<div class="rounded-lg border border-border py-12 text-center">
			<Activity class="mx-auto h-10 w-10 text-muted-foreground/50" />
			<h3 class="mt-3 text-base font-medium">Unable to load engine status</h3>
			<p class="mt-1 text-sm text-muted-foreground">
				The API may be unavailable, or the Engine has not written any heartbeats yet.
			</p>
			<Button variant="outline" class="mt-4" onclick={() => statusQuery.refetch()}>
				<RefreshCw class="mr-2 h-4 w-4" />
				Try again
			</Button>
		</div>
	{:else if status}
		<!-- LLM provider / model (high-value: stale provider config caused an incident) -->
		<div class="mb-5 rounded-lg border border-border p-4" data-testid="engine-llm-card">
			<div class="mb-3 flex items-center gap-2 text-sm font-medium">
				<Sparkles class="h-4 w-4 text-muted-foreground" />
				Extraction Model
			</div>
			<div class="grid gap-3 sm:grid-cols-2">
				<div>
					<p class="text-xs text-muted-foreground">Provider</p>
					<p class="font-mono text-sm font-medium capitalize" data-testid="engine-llm-provider">
						{status.llm.provider}
					</p>
				</div>
				<div>
					<p class="text-xs text-muted-foreground">Model</p>
					<p class="font-mono text-sm font-medium" data-testid="engine-llm-model">
						{prettyModel(status.llm.modelId)}
					</p>
				</div>
			</div>
		</div>

		<!-- Engine instance -->
		{#if status.instance}
			<div class="mb-5 rounded-lg border border-border p-4" data-testid="engine-instance-card">
				<div class="mb-3 flex items-center gap-2 text-sm font-medium">
					<Server class="h-4 w-4 text-muted-foreground" />
					Engine Instance
				</div>
				<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
					<div>
						<p class="text-xs text-muted-foreground">PID</p>
						<p class="font-mono text-sm font-medium">{status.instance.pid ?? 'N/A'}</p>
					</div>
					<div>
						<p class="text-xs text-muted-foreground">Hostname</p>
						<p class="font-mono text-sm font-medium">{status.instance.hostname ?? 'N/A'}</p>
					</div>
					<div>
						<p class="text-xs text-muted-foreground">Version</p>
						<p class="font-mono text-sm font-medium">{status.instance.version ?? 'N/A'}</p>
					</div>
					<div>
						<p class="text-xs text-muted-foreground">Uptime</p>
						<p class="text-sm font-medium">{formatUptime(status.instance.uptimeSeconds)}</p>
					</div>
					<div>
						<p class="text-xs text-muted-foreground">Advisory Lock</p>
						<div class="flex items-center gap-1">
							{#if status.instance.advisoryLockHeld}
								<Shield class="h-4 w-4 text-[var(--success)]" />
								<span class="text-sm font-medium text-[var(--success)]">Held</span>
							{:else}
								<ShieldAlert class="h-4 w-4 text-[var(--m3c-error)]" />
								<span class="text-sm font-medium text-[var(--m3c-error)]">Not held</span>
							{/if}
							{#if status.instance.lockContested}
								<span class="ml-1 text-xs text-[var(--warning)]">(contested at startup)</span>
							{/if}
						</div>
					</div>
				</div>
			</div>
		{/if}

		<!-- Workers -->
		{#if status.workers.length === 0}
			<div class="rounded-lg border border-border py-12 text-center text-sm text-muted-foreground">
				No worker heartbeats recorded yet.
			</div>
		{:else}
			<div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3" data-testid="engine-worker-grid">
				{#each status.workers as worker (worker.workerName)}
					<div class="rounded-lg border border-border p-4" data-testid="engine-worker-card">
						<div class="flex items-start justify-between">
							<p class="text-sm font-medium" data-testid="engine-worker-name">
								{worker.workerName}
							</p>
							<Activity class="h-4 w-4 text-muted-foreground" />
						</div>
						<div class="mt-2 flex flex-wrap items-center gap-2">
							<span
								class="inline-flex rounded-full px-2 py-0.5 text-xs font-medium {workerStatusStyles[
									worker.status
								] ?? 'bg-gray-100 text-gray-800'}"
							>
								{formatStatusLabel(worker.status)}
							</span>
							<span
								class="inline-flex rounded-full px-2 py-0.5 text-xs font-medium {healthStateStyles[
									worker.healthState
								] ?? 'bg-gray-100 text-gray-800'}"
								data-testid="engine-worker-health"
							>
								{worker.healthState}
							</span>
						</div>
						<p class="mt-2 text-xs text-muted-foreground">
							Last heartbeat: {formatAgo(worker.heartbeatSecondsAgo)}
						</p>
						<p class="mt-0.5 text-xs text-muted-foreground">
							Processed: {worker.processedCount.toLocaleString()} ·
							{worker.cycleCount.toLocaleString()} cycles
						</p>
						{#if worker.lastErrorMessage}
							<button
								class="mt-2 block w-full truncate text-left text-xs text-[var(--m3c-error)] hover:underline"
								onclick={() => showErrorModal(worker)}
							>
								{worker.lastErrorMessage}
							</button>
						{/if}
					</div>
				{/each}
			</div>
		{/if}
	{/if}
</div>

<!-- Error detail modal -->
<Dialog.Root
	open={errorModal.open}
	onOpenChange={(v) => {
		if (!v) errorModal = { open: false, title: '', message: '' };
	}}
>
	<Dialog.Content class="max-w-2xl">
		<Dialog.Header>
			<Dialog.Title>{errorModal.title}</Dialog.Title>
		</Dialog.Header>
		<div class="mt-2 max-h-96 overflow-y-auto rounded-md bg-muted p-4">
			<pre class="whitespace-pre-wrap break-all font-mono text-xs text-foreground">{errorModal.message}</pre>
		</div>
	</Dialog.Content>
</Dialog.Root>
