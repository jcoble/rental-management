<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { portal } from '$lib/api/endpoints/portal';
	import { FileText } from '@lucide/svelte';

	const leasesQuery = createQuery(() => ({ queryKey: ['portal-lease-page'], queryFn: () => portal.leases() }));
	function money(value: number | string | null | undefined) {
		return Number(value ?? 0).toLocaleString(undefined, { style: 'currency', currency: 'USD' });
	}
</script>

<svelte:head><title>Lease - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-5 flex items-center gap-2"><FileText class="h-5 w-5 text-primary" /><h1 class="text-2xl font-semibold">Lease</h1></div>
	<div class="space-y-3">
		{#each leasesQuery.data ?? [] as lease}
			<div class="rounded-lg border border-border bg-card p-4">
				<p class="font-medium">{lease.leaseNumber}</p>
				<p class="mt-1 text-sm text-muted-foreground">{lease.propertyName ?? 'Linked property'} {lease.unitNumber ? `Unit ${lease.unitNumber}` : ''}</p>
				<p class="mt-3 text-sm">Rent {money(lease.monthlyRent)} · Ends {new Date(lease.endDate).toLocaleDateString()}</p>
			</div>
		{:else}
			<p class="text-sm text-muted-foreground">No lease is linked to this account.</p>
		{/each}
	</div>
</div>
