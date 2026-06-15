<script lang="ts">
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { audit } from '$lib/api/endpoints/audit';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { signalRService } from '$lib/realtime/signalr';
	import ActivityFeed from '$lib/components/shared/ActivityFeed.svelte';

	// Per-record audit history: the slice of the unified trail for one (entityType, entityId), rendered
	// as the humanized activity feed. Pure read — reuses GET /api/v1/audit?entityType&entityId.
	let {
		entityType,
		entityId,
		take = 50,
	}: { entityType: string; entityId: number; take?: number } = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const historyQuery = createQuery(() => ({
		queryKey: ['audit', 'record', portfolioId, entityType, entityId],
		queryFn: () => audit.list(portfolioId, { entityType, entityId, take, sort: '-timestamp' }),
		enabled: entityId > 0,
	}));

	const rows = $derived(historyQuery.data ?? []);

	// Live refresh. The audit row for an edit is written *after* the entity save, so the history must
	// re-fetch once a change lands rather than showing a stale (or empty) list until the next reload.
	// Two complementary triggers, both contained here so every consumer (payments, expenses, tenants,
	// owners, maintenance, leases, properties) gets it for free without each page wiring invalidation:
	//   1. Local mutations — refetch when *any* create/update/delete on this page settles successfully.
	//      This is the same-tab, no-network-dependency signal and covers edits made right on the record.
	//   2. SignalR EntityUpdated/Deleted for THIS record — covers changes from another tab/user, and is
	//      the backstop when the mutating action lives on a different screen.
	function invalidateHistory() {
		if (entityId > 0) {
			queryClient.invalidateQueries({ queryKey: ['audit', 'record', portfolioId, entityType, entityId] });
		}
	}

	$effect(() => {
		// Re-subscribe when the resolved (portfolioId, entityType, entityId) changes.
		portfolioId;
		entityType;
		entityId;

		const unsubMutations = queryClient.getMutationCache().subscribe((event) => {
			if (event?.mutation?.state.status === 'success') {
				invalidateHistory();
			}
		});

		const unsubSignalR = signalRService.subscribe((_event, payload) => {
			if (payload.entityType === entityType && payload.entityId === entityId) {
				invalidateHistory();
			}
		});

		return () => {
			unsubMutations();
			unsubSignalR();
		};
	});
</script>

<div data-testid="record-history-{entityType}-{entityId}">
	{#if historyQuery.isLoading}
		<p class="px-2 py-4 text-center text-sm text-muted-foreground">Loading history…</p>
	{:else if historyQuery.isError}
		<p class="px-2 py-4 text-center text-sm text-destructive">Couldn't load history.</p>
	{:else if rows.length === 0}
		<p class="px-2 py-4 text-center text-sm text-muted-foreground">No history recorded yet.</p>
	{:else}
		<ActivityFeed activities={rows} />
	{/if}
</div>
