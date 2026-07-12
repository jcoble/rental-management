<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { securityDeposits } from '$lib/api/endpoints/securityDeposits';
	import type { SecurityDepositAccount } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Info } from '@lucide/svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';

	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;
	const initialParams = page.url.searchParams;
	let search = $state(readGridParam(initialParams, 'q'));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));
	const debouncedSearch = debounced(() => search, 300);

	let searchResetPrimed = false;
	$effect(() => {
		search;
		if (!searchResetPrimed) {
			searchResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl({ q: search, sort: gridSort, page: gridPage }, { page: 1 });
	});

	const depositsQuery = createQuery(() => ({
		queryKey: ['deposits', portfolioId, 'page', debouncedSearch.value, gridSort, gridPage, PAGE_SIZE],
		queryFn: () => securityDeposits.listPage({
			search: debouncedSearch.value,
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		}),
	}));

	const accounts = $derived(depositsQuery.data?.items ?? []);
	const totalCount = $derived(depositsQuery.data?.totalCount ?? 0);

	const depositStatusMap: Record<string, { label?: string; class: string }> = {
		NotFunded: { label: 'Not funded', class: 'm3-tone-chip border m3-tone--warning' },
		Held: { class: 'm3-tone-chip border m3-tone--info' },
		PartiallyReturned: { label: 'Partially returned', class: 'm3-tone-chip border m3-tone--warning' },
		Returned: { class: 'm3-tone-chip border m3-tone--success' },
		Withheld: { class: 'm3-tone-chip border m3-tone--error' },
	};

	const columns: ColumnDef<SecurityDepositAccount>[] = [
		{
			key: 'account',
			title: 'Rental / tenant',
			sortable: true,
			mobileRole: 'title',
			accessor: (account) => account.propertyName ?? `Property #${account.propertyId}`,
			cell: rentalCell,
		},
		{
			key: 'amount',
			title: 'Held balance',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
			accessor: (account) => account.heldBalance,
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCell,
		},
		{
			key: 'totalReceived',
			title: 'Received',
			format: 'currency',
			mobileRole: 'meta',
		},
		{
			key: 'totalDeductions',
			title: 'Deductions',
			format: 'currency',
			mobileRole: 'meta',
		},
		{
			key: 'actions',
			title: '',
			align: 'right',
			mobileRole: 'hidden',
			cell: actionsCell,
		},
	];
</script>

{#snippet rentalCell(account: SecurityDepositAccount)}
	<div class="flex flex-col" data-testid="deposit-rental">
		<span>{account.propertyName ?? `Property #${account.propertyId}`}{account.unitNumber ? ` · Unit ${account.unitNumber}` : ''}</span>
		<span class="text-xs text-muted-foreground">
			{account.tenantName ?? 'Tenant account'} · {account.relationshipNumber}
		</span>
	</div>
{/snippet}

{#snippet statusCell(account: SecurityDepositAccount)}
	<StatusBadge status={account.status} map={depositStatusMap} />
{/snippet}

{#snippet actionsCell(account: SecurityDepositAccount)}
	<div class="flex justify-end" onclick={(event) => event.stopPropagation()} role="none">
		<Button size="sm" variant="outline" class="h-7 text-xs" onclick={() => goto(`/deposits/${account.id}`)}>
			{account.status === 'NotFunded' ? 'Record funds' : 'Manage'}
		</Button>
	</div>
{/snippet}

<svelte:head>
	<title>Security Deposits - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="deposits-page">
	<PageHeader
		class="mb-4"
		band
		art={1}
		tone="mint"
		eyebrow="Money"
		title="Security Deposits"
		description="Track each tenant account's deposit funds, deductions, refunds, and current held balance."
		data-testid="deposits-header"
	/>

	<div class="mb-4 flex items-start gap-3 rounded-lg border border-[color-mix(in_srgb,var(--info)_38%,transparent)] bg-[color-mix(in_srgb,var(--info)_8%,var(--card))] p-3 text-sm" data-testid="deposits-explainer">
		<Info class="mt-0.5 h-4 w-4 shrink-0 text-[var(--info)]" />
		<p class="text-foreground">
			A deposit account is prepared with the move-in agreement. Record money when it is actually
			received; do not create another holding. Deposits remain the tenant's money and are separate
			from rental income.
		</p>
	</div>

	<DataGrid
		data={accounts}
		{columns}
		loading={depositsQuery.isLoading || depositsQuery.isFetching}
		emptyMessage={search.trim() ? 'No security deposit accounts match your search.' : 'No security deposit accounts yet.'}
		emptyDescription={search.trim() ? 'Try a tenant, property, unit, or account number.' : 'An account is created when an approved application is prepared for move-in.'}
		getRowKey={(account) => account.id}
		getRowTestId={(account) => `deposit-row-${account.id}`}
		onRowClick={(account) => goto(`/deposits/${account.id}`)}
		data-testid="deposits-list"
		pageSize={PAGE_SIZE}
		page={gridPage}
		totalCount={totalCount}
		serverSide
		onPageChange={(nextPage) => (gridPage = nextPage)}
		sort={gridSort}
		onSortChange={(nextSort) => { gridSort = nextSort ?? ''; gridPage = 1; }}
	>
		{#snippet toolbar()}
			<div class="max-w-sm flex-1">
				<SearchInput bind:value={search} placeholder="Search deposits…" testid="deposit-search" />
			</div>
		{/snippet}
	</DataGrid>
</div>
