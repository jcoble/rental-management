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
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { getUnitsEmptyStateCopy } from '$lib/unit/unit-list-state';
	import { Home, Plus } from '@lucide/svelte';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { Button } from '$lib/components/ui/button';

	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;

	// Search/filter/sort/page persisted in the URL so they survive navigating away and back. The health
	// grid is server-side; the API returns the matching page plus total count.
	const initialParams = page.url.searchParams;
	let search = $state(readGridParam(initialParams, 'q'));
	let propertyFilter = $state(readGridParam(initialParams, 'property'));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));
	const debouncedSearch = debounced(() => search, 300);

	let filterResetPrimed = false;
	$effect(() => {
		search;
		propertyFilter;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl({ q: search, property: propertyFilter, sort: gridSort, page: gridPage }, { page: 1 });
	});

	const unitsQuery = createQuery(() => ({
		queryKey: ['units', portfolioId, 'health', debouncedSearch.value, propertyFilter, gridSort, gridPage, PAGE_SIZE],
		queryFn: () =>
			units.listWithHealthPage({
				search: debouncedSearch.value,
				sort: gridSort || undefined,
				skip: (gridPage - 1) * PAGE_SIZE,
				take: PAGE_SIZE,
				propertyId: propertyFilter ? Number(propertyFilter) : undefined,
			}),
	}));

	async function loadPropertyOptions(params: { search?: string; skip: number; take: number }) {
		const result = await properties.listPage(portfolioId, { ...params, sort: 'name' });
		return {
			...result,
			items: result.items.map((property) => ({
				id: property.id,
				label: property.name,
				description: `${property.addressLine1}${property.city ? `, ${property.city}` : ''}${property.state ? `, ${property.state}` : ''}`
			}))
		};
	}

	const list = $derived(unitsQuery.data?.items ?? []);
	const totalCount = $derived(unitsQuery.data?.totalCount ?? 0);
	const hasActiveFilters = $derived(Boolean(search.trim() || propertyFilter));
	const emptyStateCopy = $derived(getUnitsEmptyStateCopy({ hasActiveFilters }));

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
		{ key: 'openWorkOrderCount', title: 'Open repairs', format: 'number', sortable: true, mobileRole: 'metric', align: 'right' },
		{ key: 'leaseEndsInDays', title: 'Lease ends', mobileRole: 'meta', accessor: (u) => leaseEndsLabel(u.leaseEndsInDays) },
		{ key: 'docsNeedingReviewCount', title: 'Docs', format: 'number', mobileRole: 'meta', align: 'right' },
		{ key: 'marketRent', title: 'Market rent', format: 'currency', sortable: true, mobileRole: 'metric' },
	];

	function leaseEndsLabel(days?: number): string {
		if (days == null) return '—';
		if (days === 0) return 'Today';
		if (days < 0) return `${Math.abs(days)} day${Math.abs(days) === 1 ? '' : 's'} ago`;
		return `In ${days} day${days === 1 ? '' : 's'}`;
	}
</script>

{#snippet statusCell(unit: UnitHealth)}
	<div class="flex flex-wrap items-center gap-1.5">
		<StatusBadge status={unit.status} />
		{#if unit.simpleStage && unit.simpleStage !== unit.status}
			<span class="text-xs text-muted-foreground">Next: </span><StatusBadge status={unit.simpleStage} map={stageMap} />
		{/if}
	</div>
{/snippet}

<svelte:head>
	<title>Units - Rental Command</title>
</svelte:head>

	<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="units-page">
	<PageHeader
		class="mb-4"
		density="compact"
		eyebrow="Rentals"
		title="Units"
		description="Choose a unit to manage its lease, residents, money, maintenance, documents, and history."
		data-testid="units-header"
	/>

	{#if unitsQuery.isError}
		<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="units-list-error">
			<p class="font-medium text-destructive">Could not load units.</p>
			<p class="mt-1 text-sm text-muted-foreground">Try again. The rentals list is temporarily unavailable.</p>
			<Button class="mt-4" variant="outline" onclick={() => unitsQuery.refetch()}>Try again</Button>
		</div>
	{:else}
	<DataGrid
		data={list}
		{columns}
		loading={unitsQuery.isLoading || unitsQuery.isFetching}
		emptyMessage={emptyStateCopy.message}
		emptyDescription={emptyStateCopy.description}
		emptyIcon={Home}
		emptyTone="primary"
		onRowClick={(unit) => goto('/units/' + unit.id)}
		getRowKey={(u) => u.id}
		getRowTestId={(u) => `unit-row-${u.id}`}
		data-testid="units-list"
		pageSize={PAGE_SIZE}
		page={gridPage}
		totalCount={totalCount}
		serverSide
		onPageChange={(page) => (gridPage = page)}
		sort={gridSort}
		onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
	>
		{#snippet toolbar()}
			<div class="flex flex-1 flex-wrap items-center gap-2">
				<div class="max-w-sm flex-1">
					<SearchInput bind:value={search} placeholder="Search units…" testid="unit-search" />
				</div>
				<Button href="/properties?coach=open-property-for-units" variant="outline" class="shrink-0 gap-2" data-testid="unit-create-button">
					<Plus class="h-4 w-4" />
					New unit
				</Button>
				<div class="w-[240px]">
					<RemoteRecordSelect
						queryKey={['unit-property-filter', portfolioId]}
						label="Property"
						bind:value={propertyFilter}
						selectedLabel={propertyFilter ? `Property #${propertyFilter}` : null}
						placeholder="All properties"
						clearLabel="All properties"
						searchPlaceholder="Search properties…"
						emptyLabel="No matching properties"
						loadPage={loadPropertyOptions}
						testid="unit-property-filter"
					/>
				</div>
			</div>
		{/snippet}
	</DataGrid>
	{/if}
</div>
