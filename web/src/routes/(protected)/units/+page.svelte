<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { units } from '$lib/api/endpoints/units';
	import { properties } from '$lib/api/endpoints/properties';
	import type { UnitHealth } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Home } from '@lucide/svelte';
	import * as Select from '$lib/components/ui/select';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';

	const portfolioId = $derived(getCurrentPortfolioId());

	// Search + property filter persisted in the URL so they survive navigating away and back. Paging is
	// handled client-side by the DataGrid over the fetched list (matching the other grids).
	let search = $state(readGridParam(page.url.searchParams, 'q'));
	let propertyFilter = $state(readGridParam(page.url.searchParams, 'property'));
	const debouncedSearch = debounced(() => search, 300);

	$effect(() => {
		syncGridUrl({ q: search, property: propertyFilter });
	});

	const unitsQuery = createQuery(() => ({
		queryKey: ['units', portfolioId, 'health', debouncedSearch.value, propertyFilter],
		queryFn: () =>
			units.listWithHealth({
				search: debouncedSearch.value,
				take: 500,
				propertyId: propertyFilter ? Number(propertyFilter) : undefined,
			}),
	}));

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
	}));

	const list = $derived(unitsQuery.data ?? []);

	// Simplified-stage badge map. Reuses the app's tone-chip recipes (the same class strings StatusBadge
	// uses internally) so the colors flip correctly in dark/light — never raw Tailwind palette literals.
	const stageMap: Record<string, { label?: string; class: string }> = {
		Active: { class: 'm3-tone-chip border m3-tone--success' },
		Renewal: { class: 'm3-tone-chip border m3-tone--warning' },
		'Move-Out': { class: 'm3-tone-chip border m3-tone--error' },
		Lease: { class: 'm3-tone-chip border m3-tone--info' },
		Vacant: { class: 'm3-tone-chip border m3-tone--info' },
		Turnover: { class: 'm3-tone-chip border m3-tone--primary' },
	};

	const columns: ColumnDef<UnitHealth>[] = [
		{ key: 'unitNumber', title: 'Unit', sortable: true, mobileRole: 'title', accessor: (u) => `Unit ${u.unitNumber}` },
		{ key: 'propertyName', title: 'Property', sortable: true, mobileRole: 'subtitle' },
		{ key: 'status', title: 'Status', mobileRole: 'badge', cell: statusCell },
		{ key: 'simpleStage', title: 'Stage', mobileRole: 'meta', cell: stageCell },
		{ key: 'openWorkOrderCount', title: 'Open repairs', format: 'number', sortable: true, mobileRole: 'metric', align: 'right' },
		{ key: 'leaseEndsInDays', title: 'Lease ends', mobileRole: 'meta', accessor: (u) => leaseEndsLabel(u.leaseEndsInDays) },
		{ key: 'docsNeedingReviewCount', title: 'Docs', format: 'number', mobileRole: 'meta', align: 'right' },
		{ key: 'marketRent', title: 'Market rent', format: 'currency', sortable: true, mobileRole: 'metric' },
	];

	function leaseEndsLabel(days?: number): string {
		if (days == null) return '—';
		if (days === 0) return 'Today';
		return `${days}d`;
	}
</script>

{#snippet statusCell(unit: UnitHealth)}
	<StatusBadge status={unit.status} />
{/snippet}

{#snippet stageCell(unit: UnitHealth)}
	<StatusBadge status={unit.simpleStage} map={stageMap} />
{/snippet}

<svelte:head>
	<title>Units - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="units-page">
	<div class="mb-4 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Units</h1>
			<p class="text-sm text-muted-foreground">Every unit at a glance — open a unit for its full story.</p>
		</div>
	</div>

	<DataGrid
		data={list}
		{columns}
		loading={unitsQuery.isLoading}
		emptyMessage="No units yet"
		emptyDescription="Units live under a property. Add a property and its units to start managing them here."
		emptyIcon={Home}
		emptyTone="primary"
		onRowClick={(unit) => goto('/units/' + unit.id)}
		getRowKey={(u) => u.id}
		getRowTestId={(u) => `unit-row-${u.id}`}
		data-testid="units-list"
	>
		{#snippet toolbar()}
			<div class="flex flex-1 flex-wrap items-center gap-2">
				<div class="max-w-sm flex-1">
					<SearchInput bind:value={search} placeholder="Search units…" testid="unit-search" />
				</div>
				<Select.Root type="single" bind:value={propertyFilter}>
					<Select.Trigger class="w-[200px]" data-testid="unit-property-filter">
						{propertiesQuery.data?.find((p) => String(p.id) === propertyFilter)?.name ?? 'All properties'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="All properties">All properties</Select.Item>
						{#each propertiesQuery.data ?? [] as property}
							<Select.Item value={String(property.id)} label={property.name}>{property.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
		{/snippet}
	</DataGrid>
</div>
