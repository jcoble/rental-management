<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { leaseManagements } from '$lib/api/endpoints/lease-managements';
	import type { UnitDashboard } from '$lib/types';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { ArrowRight, FileText, ScanLine, Users } from '@lucide/svelte';

	let { dashboard, onScan }: { dashboard: UnitDashboard; onScan: () => void } = $props();

	const relationshipsQuery = createQuery(() => ({
		queryKey: ['lease-managements', 'unit', dashboard.unit.id],
		queryFn: () => leaseManagements.listPage({ unitId: dashboard.unit.id, take: 50, sort: '-updatedAtUtc' })
	}));
</script>

<div class="space-y-4" data-testid="unit-lease-tab">
	<div class="flex flex-wrap items-center justify-between gap-3">
		<div><h2 class="text-lg font-semibold">Tenant & lease</h2><p class="text-sm text-muted-foreground">Household, possession, agreement versions, and account relationship for this rental.</p></div>
		<div class="flex gap-2"><Button variant="outline" class="gap-2" onclick={onScan}><ScanLine class="h-4 w-4" /> Import agreement</Button><Button href="/applications" class="gap-2"><Users class="h-4 w-4" /> Prepare move-in</Button></div>
	</div>

	{#if relationshipsQuery.isLoading}<p class="text-sm text-muted-foreground">Loading tenant relationships…</p>
	{:else if (relationshipsQuery.data?.items.length ?? 0) === 0}
		<DetailCard title="No tenant relationship yet" icon={FileText}><p class="text-sm text-muted-foreground">Prepare a move-in from an approved application, or scan an existing signed agreement.</p></DetailCard>
	{:else}
		<div class="grid gap-3">
			{#each relationshipsQuery.data?.items ?? [] as relationship}
				<a href={`/leases/${relationship.leaseManagementId}`} class="flex items-center justify-between gap-4 rounded-xl border bg-card p-4 transition-colors hover:bg-muted/40">
					<div class="min-w-0"><div class="flex flex-wrap items-center gap-2"><p class="font-medium">{relationship.primaryTenantName ?? 'No primary tenant'}</p><StatusBadge status={relationship.lifecycle} /></div><p class="mt-1 text-sm text-muted-foreground">{relationship.agreementNumber ?? 'No governing agreement'}{relationship.agreementStatus ? ` · ${relationship.agreementStatus}` : ''}{relationship.termEndOn ? ` · ends ${relationship.termEndOn}` : ''}</p></div><ArrowRight class="h-4 w-4 shrink-0 text-muted-foreground" />
				</a>
			{/each}
		</div>
	{/if}
</div>
