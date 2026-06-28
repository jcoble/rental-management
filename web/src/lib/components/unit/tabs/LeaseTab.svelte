<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import type { UnitDashboard } from '$lib/types';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import LeaseDetail from '$lib/components/records/LeaseDetail.svelte';
	import { Button } from '$lib/components/ui/button';
	import { FileText, ScanLine, ArrowLeft } from '@lucide/svelte';

	let {
		dashboard,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onScan: () => void;
	} = $props();

	// Which lease to show: an explicit ?lease=<id> (e.g. a prior lease) wins, otherwise the
	// unit's current lease. Inner lease tabs live in LeaseDetail's local state, so they never
	// collide with the unit Command Center's own ?tab=lease.
	const currentLeaseId = $derived(dashboard.currentLease?.id ?? null);
	const requestedLeaseId = $derived(Number(page.url.searchParams.get('lease')) || null);
	const selectedLeaseId = $derived(requestedLeaseId ?? currentLeaseId);
	// Show a "back to current lease" affordance only when viewing a non-current lease via ?lease=.
	const viewingPriorLease = $derived(
		requestedLeaseId !== null && requestedLeaseId !== currentLeaseId
	);

	// Clearing the selection drops ?lease= and falls back to the current lease (or empty state).
	function clearSelection() {
		goto('/units/' + dashboard.unit.id + '?tab=lease', {
			replaceState: true,
			keepFocus: true,
			noScroll: true,
		});
	}
</script>

<div class="space-y-4" data-testid="unit-lease-tab">
	{#if selectedLeaseId}
		{#if viewingPriorLease}
			<Button
				variant="outline"
				size="sm"
				class="gap-1"
				onclick={clearSelection}
				data-testid="lease-back-to-current"
			>
				<ArrowLeft class="h-4 w-4" /> Back to current lease
			</Button>
		{/if}
		<LeaseDetail leaseId={selectedLeaseId} onDeleted={clearSelection} />
	{:else}
		<DetailCard title="No current lease" icon={FileText} accent="muted" testid="lease-empty">
			<p class="text-sm text-muted-foreground">
				This unit has no current lease. Scan an existing signed lease to extract its terms, or create one from the Leases page.
			</p>
		</DetailCard>
	{/if}

	<div class="flex flex-wrap gap-2">
		<Button variant="outline" class="gap-2" onclick={() => onScan()} data-testid="lease-scan">
			<ScanLine class="h-4 w-4" /> Scan / upload lease
		</Button>
	</div>
</div>
