<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { leaseManagements, type PrepareMoveInResponse } from '$lib/api/endpoints/lease-managements';
	import type { LeaseManagementSummary } from '$lib/types';
	import PrepareMoveInDialog from '$lib/components/applications/PrepareMoveInDialog.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { recordHref } from '$lib/navigation/record-href';
	import { leaseAgreementStatusLabel, leaseLifecycleLabel } from '$lib/leases/lease-list-labels';
	import { FilePlus2, ScanLine } from '@lucide/svelte';

	const PAGE_SIZE = 20;
	const initial = page.url.searchParams;
	let search = $state(readGridParam(initial, 'q'));
	let statusFilter = $state(readGridParam(initial, 'lifecycle'));
	let gridSort = $state(readGridParam(initial, 'sort') || '-updatedAtUtc');
	let gridPage = $state(readGridParam(initial, 'page', 1));
	let showManualLeaseDialog = $state(false);
	const debouncedSearch = debounced(() => search, 300);

	$effect(() => {
		syncGridUrl(
			{ q: search, lifecycle: statusFilter, sort: gridSort, page: gridPage },
			{ sort: '-updatedAtUtc', page: 1 }
		);
	});

	const leasesQuery = createQuery(() => ({
		queryKey: ['lease-managements', 'page', debouncedSearch.value, statusFilter, gridSort, gridPage],
		queryFn: () =>
			leaseManagements.listPage({
				search: debouncedSearch.value || undefined,
				lifecycle: statusFilter || undefined,
				sort: gridSort || undefined,
				skip: (gridPage - 1) * PAGE_SIZE,
				take: PAGE_SIZE
			})
	}));
	const hasLeaseFilters = $derived(Boolean(search.trim() || statusFilter));
	const leaseEmptyMessage = $derived(
		hasLeaseFilters
			? 'No leases match these filters.'
			: 'No leases yet.'
	);
	const leaseEmptyDescription = $derived(
		hasLeaseFilters
			? 'Try a different household, rental, lease number, or status.'
			: 'Approve an application and prepare a move-in, or import an existing signed lease.'
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
			title: 'Status',
			accessor: (item) => leaseLifecycleLabel(item.lifecycle),
			sortable: true,
			mobileRole: 'badge'
		},
		{
			key: 'agreementStatus',
			title: 'Lease',
			accessor: (item) => {
				const current = leaseAgreementStatusLabel(item.agreementStatus);
				if (!item.upcomingLeaseAgreementId) return current;
				return `${current} · Next lease: ${leaseAgreementStatusLabel(item.upcomingAgreementStatus)}`;
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

	function finishManualLease(result: PrepareMoveInResponse) {
		showManualLeaseDialog = false;
		void goto(recordHref('leaseManagement', { id: result.leaseManagementId }));
	}
</script>

<svelte:head>
	<title>Leases - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="leases-page">
	<PageHeader
		class="mb-4"
		band
		art={10}
		tone="violet"
		eyebrow="Rentals"
		title="Leases"
		description="Find each household's lease, any next lease, and rent account."
		data-testid="leases-header"
	>
		{#snippet actions()}
			<Button href="/scan" variant="outline" class="gap-2"
				><ScanLine class="h-4 w-4" /> Import signed lease</Button
			>
			<Button onclick={() => (showManualLeaseDialog = true)} class="gap-2" data-testid="leases-create-lease">
				<FilePlus2 class="h-4 w-4" /> Create lease
			</Button>
		{/snippet}
	</PageHeader>

	{#if leasesQuery.isError}
		<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="leases-list-error">
			<p class="font-medium text-destructive">Could not load leases.</p>
			<p class="mt-1 text-sm text-muted-foreground">Try again. The lease list is temporarily unavailable.</p>
			<Button class="mt-4" variant="outline" onclick={() => leasesQuery.refetch()}>Try again</Button>
		</div>
	{:else}
	<DataGrid
		data={leasesQuery.data?.items ?? []}
		{columns}
		loading={leasesQuery.isLoading}
		emptyMessage={leaseEmptyMessage}
		emptyDescription={leaseEmptyDescription}
		onRowClick={(item) => goto(recordHref('leaseManagement', { id: item.leaseManagementId, unitId: item.unitId }))}
		getRowKey={(item) => item.leaseManagementId}
		serverSide
		page={gridPage}
		pageSize={PAGE_SIZE}
		totalCount={leasesQuery.data?.totalCount ?? 0}
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
					<SearchInput bind:value={search} placeholder="Search household, rental, or lease…" />
				</div>
				<Select.Root type="single" bind:value={statusFilter} onValueChange={() => (gridPage = 1)}>
					<Select.Trigger class="w-full sm:w-52"
						><Select.Value placeholder="All statuses" /></Select.Trigger
					>
					<Select.Content>
						<Select.Item value="">All statuses</Select.Item>
						<Select.Item value="Preparing">{leaseLifecycleLabel('Preparing')}</Select.Item>
						<Select.Item value="Upcoming">{leaseLifecycleLabel('Upcoming')}</Select.Item>
						<Select.Item value="Occupied">{leaseLifecycleLabel('Occupied')}</Select.Item>
						<Select.Item value="Ending">{leaseLifecycleLabel('Ending')}</Select.Item>
						<Select.Item value="AccountingCloseout">{leaseLifecycleLabel('AccountingCloseout')}</Select.Item>
						<Select.Item value="Closed">{leaseLifecycleLabel('Closed')}</Select.Item>
						<Select.Item value="Canceled">{leaseLifecycleLabel('Canceled')}</Select.Item>
					</Select.Content>
				</Select.Root>
			</div>
		{/snippet}
		{#snippet mobileActions(item)}
			<StatusBadge status={leaseLifecycleLabel(item.lifecycle)} />
		{/snippet}
	</DataGrid>
	{/if}
</div>

{#if showManualLeaseDialog}
	<PrepareMoveInDialog
		mode="manual"
		prefill={{ applicationId: '', unitId: '', tenantId: '' }}
		onclose={() => (showManualLeaseDialog = false)}
		onprepared={finishManualLease}
	/>
{/if}
