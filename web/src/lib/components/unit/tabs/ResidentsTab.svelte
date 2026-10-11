<script lang="ts">
	import type { UnitDashboard } from '$lib/types';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import { Button } from '$lib/components/ui/button';
	import { ArrowRight, Users } from '@lucide/svelte';

	let { dashboard }: { dashboard: UnitDashboard } = $props();

	const residents = $derived(
		dashboard.currentTenants?.length
			? dashboard.currentTenants
			: dashboard.currentTenant
				? [dashboard.currentTenant]
				: [],
	);
</script>

<div class="space-y-4" data-testid="unit-residents-tab">
	<div>
		<h2 class="text-lg font-semibold">Residents</h2>
		<p class="text-sm text-muted-foreground">
			People currently connected to this Unit's tenant relationship. Contact/profile changes do not rewrite a signed agreement.
		</p>
	</div>

	{#if residents.length === 0}
		<DetailCard title="No current residents" icon={Users} accent="muted" testid="unit-residents-empty">
			<p class="text-sm text-muted-foreground">This unit does not have a current resident relationship.</p>
		</DetailCard>
	{:else}
		<ul class="divide-y rounded-xl border bg-card" data-testid="unit-residents-list">
			{#each residents as resident (resident.id)}
				<li class="flex items-center justify-between gap-4 p-4">
					<div class="min-w-0">
						<p class="truncate font-medium">{resident.name || `Tenant #${resident.id}`}</p>
						{#if resident.email || resident.phone}
							<p class="mt-1 truncate text-sm text-muted-foreground">{resident.email || resident.phone}</p>
						{/if}
					</div>
					<Button href={`/tenants/${resident.id}`} variant="ghost" size="sm" class="gap-1">
						Open <ArrowRight class="h-4 w-4" />
					</Button>
				</li>
			{/each}
		</ul>
	{/if}
</div>
