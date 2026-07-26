<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import {
		securityDeposits,
		type SecurityDepositStatus,
		type TenantAccountDeposit,
	} from '$lib/api/endpoints/securityDeposits';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { formatRentalLocation, formatResidentName } from '$lib/accounting/money-display';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import { Info } from '@lucide/svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';

	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;
	const initialParams = page.url.searchParams;
	let search = $state(readGridParam(initialParams, 'q'));
	let statusFilter = $state(readGridParam(initialParams, 'status'));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));
	const debouncedSearch = debounced(() => search, 300);

	let searchResetPrimed = false;
	$effect(() => {
		search;
		statusFilter;
		if (!searchResetPrimed) {
			searchResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl(
			{ q: search, status: statusFilter, sort: gridSort, page: gridPage },
			{ page: 1 }
		);
	});

	const depositsQuery = createQuery(() => ({
		queryKey: [
			'deposits',
			portfolioId,
			'page',
			debouncedSearch.value,
			statusFilter,
			gridSort,
			gridPage,
			PAGE_SIZE,
		],
		queryFn: () => securityDeposits.listPage({
			search: debouncedSearch.value,
			status: statusFilter ? (statusFilter as SecurityDepositStatus) : undefined,
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

	const columns: ColumnDef<TenantAccountDeposit>[] = [
		{
			key: 'propertyName',
			title: 'Rental / tenant',
			sortable: true,
			mobileRole: 'title',
			accessor: (account) => account.propertyName ?? `Property #${account.propertyId}`,
			cell: rentalCell,
		},
		{
			key: 'heldBalance',
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

{#snippet rentalCell(account: TenantAccountDeposit)}
	<div class="flex flex-col" data-testid="deposit-rental">
		<span>{formatRentalLocation(account)}</span>
		<span class="text-xs text-muted-foreground">
			{formatResidentName(account.primaryTenantName)}
		</span>
	</div>
{/snippet}

{#snippet statusCell(account: TenantAccountDeposit)}
	<StatusBadge status={account.status} map={depositStatusMap} />
{/snippet}

{#snippet actionsCell(account: TenantAccountDeposit)}
	<div class="flex justify-end" onclick={(event) => event.stopPropagation()} role="none">
		<Button size="sm" variant="outline" class="h-7 text-xs" onclick={() => goto(`/deposits/${account.tenantAccountId}`)}>
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
		description="Track money held for each tenant, including deductions, refunds, and the amount still held."
		data-testid="deposits-header"
	>
		<a
			href="/docs/security-deposits"
			class="mt-3 inline-flex min-h-11 items-center text-sm font-medium text-primary underline-offset-4 hover:underline"
			data-testid="deposits-help-link"
		>
			How security deposits work
		</a>
	</PageHeader>

	<div class="mb-4 flex items-start gap-3 rounded-lg border border-[color-mix(in_srgb,var(--info)_38%,transparent)] bg-[color-mix(in_srgb,var(--info)_8%,var(--card))] p-3 text-sm" data-testid="deposits-explainer">
		<Info class="mt-0.5 h-4 w-4 shrink-0 text-[var(--info)]" />
		<p class="text-foreground">
			A deposit account is prepared with the move-in agreement. Record money when it is actually
			received; do not create another holding. Deposits remain the tenant's money and are separate
			from rental income.
		</p>
	</div>

	{#if depositsQuery.isError}
		<div class="rounded-lg border border-destructive/40 bg-destructive/5 p-4" data-testid="deposits-load-error">
			<div class="flex items-center justify-between gap-4">
				<div>
					<p class="font-medium text-destructive">Security deposits could not be loaded.</p>
					<p class="mt-1 text-sm text-muted-foreground">Try again. No deposit records have been changed.</p>
				</div>
				<Button variant="outline" onclick={() => depositsQuery.refetch()}>Try again</Button>
			</div>
		</div>
	{:else}
	<DataGrid
		data={accounts}
		{columns}
		loading={depositsQuery.isLoading}
		emptyMessage={search.trim() ? 'No security deposit accounts match your search.' : 'No security deposit accounts yet.'}
		emptyDescription={search.trim() ? 'Try a tenant, property, or unit.' : 'A deposit account is prepared when an approved application is prepared for move-in.'}
		getRowKey={(account) => account.securityDepositAccountId}
		getRowTestId={(account) => `deposit-row-${account.securityDepositAccountId}`}
		onRowClick={(account) => goto(`/deposits/${account.tenantAccountId}`)}
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
			<div class="flex max-w-xl flex-1 items-center gap-2">
				<SearchInput bind:value={search} placeholder="Search deposits…" testid="deposit-search" />
				<Select.Root type="single" bind:value={statusFilter}>
					<Select.Trigger class="h-9 w-44 text-sm" data-testid="deposit-status-filter">
						{statusFilter === 'NotFunded' ? 'Not funded' : statusFilter === 'PartiallyReturned' ? 'Partially returned' : statusFilter || 'All statuses'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="All statuses">All statuses</Select.Item>
						<Select.Item value="NotFunded" label="Not funded">Not funded</Select.Item>
						<Select.Item value="Held" label="Held">Held</Select.Item>
						<Select.Item value="PartiallyReturned" label="Partially returned">Partially returned</Select.Item>
						<Select.Item value="Returned" label="Returned">Returned</Select.Item>
						<Select.Item value="Withheld" label="Withheld">Withheld</Select.Item>
					</Select.Content>
				</Select.Root>
			</div>
		{/snippet}
	</DataGrid>
	{/if}
</div>
