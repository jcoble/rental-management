<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { leaseManagements } from '$lib/api/endpoints/lease-managements';
	import type { LeaseManagementSummary } from '$lib/types';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { ScanLine, Users } from '@lucide/svelte';

	const PAGE_SIZE = 20;
	const initial = page.url.searchParams;
	let search = $state(readGridParam(initial, 'q'));
	let lifecycle = $state(readGridParam(initial, 'lifecycle'));
	let gridSort = $state(readGridParam(initial, 'sort') || '-updatedAtUtc');
	let gridPage = $state(readGridParam(initial, 'page', 1));
	const debouncedSearch = debounced(() => search, 300);

	$effect(() => {
		syncGridUrl(
			{ q: search, lifecycle, sort: gridSort, page: gridPage },
			{ sort: '-updatedAtUtc', page: 1 }
		);
	});

	const relationshipsQuery = createQuery(() => ({
		queryKey: ['lease-managements', 'page', debouncedSearch.value, lifecycle, gridSort, gridPage],
		queryFn: () =>
			leaseManagements.listPage({
				search: debouncedSearch.value || undefined,
				lifecycle: lifecycle || undefined,
				sort: gridSort || undefined,
				skip: (gridPage - 1) * PAGE_SIZE,
				take: PAGE_SIZE
			})
	}));
	const hasRelationshipFilters = $derived(Boolean(search.trim() || lifecycle));
	const relationshipEmptyMessage = $derived(
		hasRelationshipFilters
			? 'No tenant and lease relationships match these filters.'
			: 'No tenant and lease relationships yet.'
	);
	const relationshipEmptyDescription = $derived(
		hasRelationshipFilters
			? 'Try a different household, rental, agreement number, or relationship stage.'
			: 'Approve an application and prepare a move-in, or import an existing signed agreement.'
	);

	const columns: ColumnDef<LeaseManagementSummary>[] = [
		{
			key: 'tenantName',
			title: 'Household',
			accessor: (item) => item.primaryTenantName ?? 'No primary tenant',
			sortable: true,
			mobileRole: 'title'
		},
		{
			key: 'propertyName',
			title: 'Rental',
			accessor: (item) => `${item.propertyName}${item.unitNumber ? ` · ${item.unitNumber}` : ''}`,
			sortable: true,
			mobileRole: 'subtitle'
		},
		{
			key: 'lifecycle',
			title: 'Relationship',
			sortable: true,
			mobileRole: 'badge'
		},
		{
			key: 'agreementStatus',
			title: 'Agreement',
			accessor: (item) => {
				const current = item.agreementStatus ?? 'No governing agreement';
				return item.upcomingLeaseAgreementId ? `${current} · Upcoming prepared` : current;
			}
		},
		{
			key: 'hasReconciliationException',
			title: 'Review',
			accessor: (item) => (item.hasReconciliationException ? 'Needs reconciliation' : '—')
		},
		{ key: 'termEndOn', title: 'Term ends', format: 'date' },
		{
			key: 'rent',
			title: 'Base rent',
			accessor: (item) => item.baseRentAmount,
			format: 'currency',
			sortable: true,
			mobileRole: 'metric'
		}
	];
</script>

<svelte:head>
	<title>Leases - Rental Command</title>
</svelte:head>

<div class="space-y-6">
	<PageHeader
		title="Leases"
		description="Find a tenant relationship, its governing agreement, upcoming agreement, and account context."
	>
		{#snippet actions()}
			<Button href="/scan" variant="outline" class="gap-2"
				><ScanLine class="h-4 w-4" /> Import agreement</Button
			>
			<Button href="/applications" class="gap-2"><Users class="h-4 w-4" /> Prepare move-in</Button>
		{/snippet}
	</PageHeader>

	{#if relationshipsQuery.isError}
		<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="leases-list-error">
			<p class="font-medium text-destructive">Could not load tenant and lease relationships.</p>
			<p class="mt-1 text-sm text-muted-foreground">Try again. The relationship list is temporarily unavailable.</p>
			<Button class="mt-4" variant="outline" onclick={() => relationshipsQuery.refetch()}>Try again</Button>
		</div>
	{:else}
	<DataGrid
		data={relationshipsQuery.data?.items ?? []}
		{columns}
		loading={relationshipsQuery.isLoading}
		emptyMessage={relationshipEmptyMessage}
		emptyDescription={relationshipEmptyDescription}
		onRowClick={(item) => goto(`/leases/${item.leaseManagementId}`)}
		getRowKey={(item) => item.leaseManagementId}
		serverSide
		page={gridPage}
		pageSize={PAGE_SIZE}
		totalCount={relationshipsQuery.data?.totalCount ?? 0}
		sort={gridSort}
		onPageChange={(next) => (gridPage = next)}
		onSortChange={(next) => {
			gridSort = next ?? '';
			gridPage = 1;
		}}
	>
		{#snippet toolbar()}
			<div class="flex w-full flex-col gap-3 sm:flex-row sm:items-center">
				<div class="w-full sm:max-w-md">
					<SearchInput bind:value={search} placeholder="Search household, rental, or agreement…" />
				</div>
				<Select.Root type="single" bind:value={lifecycle} onValueChange={() => (gridPage = 1)}>
					<Select.Trigger class="w-full sm:w-52"
						><Select.Value placeholder="All relationships" /></Select.Trigger
					>
					<Select.Content>
						<Select.Item value="">All relationships</Select.Item>
						<Select.Item value="Preparing">Preparing</Select.Item>
						<Select.Item value="Upcoming">Upcoming</Select.Item>
						<Select.Item value="Occupied">Occupied</Select.Item>
						<Select.Item value="Ending">Ending</Select.Item>
						<Select.Item value="AccountingCloseout">Accounting closeout</Select.Item>
						<Select.Item value="Closed">Closed</Select.Item>
						<Select.Item value="Canceled">Canceled</Select.Item>
					</Select.Content>
				</Select.Root>
			</div>
		{/snippet}
		{#snippet mobileActions(item)}
			<StatusBadge status={item.lifecycle} />
		{/snippet}
	</DataGrid>
	{/if}
</div>
