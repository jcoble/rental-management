<script lang="ts">
	import { page } from '$app/state';
	import { createQuery } from '@tanstack/svelte-query';
	import { Info } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import * as Select from '$lib/components/ui/select';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { properties } from '$lib/api/endpoints/properties';
	import { units } from '$lib/api/endpoints/units';
	import {
		accountingBooks,
		type GeneralLedgerRow,
		type GeneralLedgerSort as ApiGeneralLedgerSort,
		type JournalSourceType
	} from '$lib/api/endpoints/accounting-books';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import {
		GENERAL_LEDGER_DEFAULT_SORT,
		GENERAL_LEDGER_SORTS,
		readGeneralLedgerUrlState,
		type GeneralLedgerSort
	} from '$lib/accounting/global-money-state';
	import {
		formatAccountingCurrency,
		formatAccountingDate,
		formatAccountingDateTime,
		formatSourceTypeLabel,
		getIncreaseDecreaseAmounts
	} from '$lib/accounting/accounting-display';
	import { ACCOUNTING_HELP } from '$lib/accounting/accounting-help';
	import { getAccountingDetailMode } from './AccountingDetailMode.svelte';
	import AccountPicker from './AccountPicker.svelte';
	import JournalDetailDrawer from './JournalDetailDrawer.svelte';

	const PAGE_SIZE = 25;
	const SOURCE_TYPES: JournalSourceType[] = [
		'TenantCharge',
		'TenantReceipt',
		'ProviderSettlement',
		'TenantConcession',
		'ReceivableWriteOff',
		'SecurityDepositReceipt',
		'SecurityDepositRefund',
		'SecurityDepositApplication',
		'ExpensePayment',
		'BillIncurred',
		'BillPayment',
		'BankTransfer',
		'LoanPayment',
		'CapitalPurchase',
		'Depreciation',
		'OwnerContribution',
		'OwnerDistribution',
		'OpeningBalance'
	];

	let {
		active = true,
		class: className,
		testid = 'general-ledger-panel'
	}: {
		active?: boolean;
		class?: string;
		testid?: string;
	} = $props();

	const authState = getAuthState();
	const portfolioId = $derived(getCurrentPortfolioId());
	const detailMode = getAccountingDetailMode();
	const advanced = $derived((detailMode?.mode ?? 'simple') === 'advanced');
	const initialState = readGeneralLedgerUrlState(page.url.searchParams);

	let selectedAccountId = $state<number | null>(initialState.accountId);
	let propertyFilter = $state(initialState.propertyId == null ? '' : String(initialState.propertyId));
	let unitFilter = $state(initialState.unitId == null ? '' : String(initialState.unitId));
	let sourceFilter = $state(initialState.sourceType);
	let effectiveFrom = $state(initialState.effectiveFrom);
	let effectiveTo = $state(initialState.effectiveTo);
	let search = $state(initialState.search);
	let pageNumber = $state(initialState.page);
	let sort = $state<GeneralLedgerSort>(initialState.sort);
	let journalPublicId = $state<string | null>(null);
	let filtersOpen = $state(false);
	let filtersPrimed = false;

	const debouncedSearch = debounced(() => search, 300);
	const selectedPropertyId = $derived(propertyFilter ? Number(propertyFilter) : undefined);
	const selectedUnitId = $derived(unitFilter ? Number(unitFilter) : undefined);
	const selectedSourceType = $derived(
		SOURCE_TYPES.includes(sourceFilter as JournalSourceType) ? (sourceFilter as JournalSourceType) : undefined
	);

	$effect(() => {
		selectedAccountId;
		propertyFilter;
		unitFilter;
		sourceFilter;
		effectiveFrom;
		effectiveTo;
		debouncedSearch.value;
		if (!filtersPrimed) {
			filtersPrimed = true;
			return;
		}
		pageNumber = 1;
	});

	$effect(() => {
		if (!active) return;
		selectedAccountId;
		propertyFilter;
		unitFilter;
		sourceFilter;
		effectiveFrom;
		effectiveTo;
		search;
		pageNumber;
		sort;
		syncGridUrl(
			{
				account: selectedAccountId,
				property: propertyFilter,
				unit: unitFilter,
				source: sourceFilter,
				from: effectiveFrom,
				to: effectiveTo,
				q: search,
				page: pageNumber,
				sort
			},
			{ page: 1, sort: GENERAL_LEDGER_DEFAULT_SORT }
		);
	});

	async function loadPropertyOptions(params: { search?: string; skip: number; take: number }) {
		const result = await properties.listPage(portfolioId, { ...params, sort: 'name' });
		return {
			...result,
			items: result.items.map((property) => ({
				id: property.id,
				label: property.name,
				description: `${property.addressLine1}, ${property.city}, ${property.state}`
			}))
		};
	}

	async function loadUnitOptions(params: { search?: string; skip: number; take: number }) {
		const result = await units.listPage({ ...params, propertyId: selectedPropertyId });
		return {
			...result,
			items: result.items.map((unit) => ({
				id: unit.id,
				label: `Unit ${unit.unitNumber}`,
				description: `Property #${unit.propertyId}`
			}))
		};
	}

	const ledgerQuery = createQuery(() => ({
		queryKey: [
			'accounting-general-ledger',
			portfolioId,
			selectedAccountId,
			selectedPropertyId,
			selectedUnitId,
			selectedSourceType,
			effectiveFrom,
			effectiveTo,
			debouncedSearch.value,
			pageNumber,
			sort
		],
		enabled: active && authState.isAuthenticated,
		queryFn: () =>
			accountingBooks.generalLedger({
				skip: (pageNumber - 1) * PAGE_SIZE,
				take: PAGE_SIZE,
				accountId: selectedAccountId ?? undefined,
				propertyId: selectedPropertyId,
				unitId: selectedUnitId,
				sourceType: selectedSourceType,
				effectiveFrom: effectiveFrom || undefined,
				effectiveTo: effectiveTo || undefined,
				search: debouncedSearch.value.trim() || undefined,
				sort: sort as ApiGeneralLedgerSort
			})
	}));
	const balancesQuery = createQuery(() => ({
		queryKey: ['accounting-company-balances', portfolioId],
		enabled: active && authState.isAuthenticated && selectedAccountId == null,
		queryFn: () => accountingBooks.trialBalance()
	}));

	function errorStatus(error: unknown): number | undefined {
		if (!error || typeof error !== 'object' || !('status' in error)) return undefined;
		const status = (error as { status?: unknown }).status;
		return typeof status === 'number' ? status : undefined;
	}

	const unauthorized = $derived(
		!authState.isAuthenticated || [401, 403, 404].includes(errorStatus(ledgerQuery.error) ?? 0)
	);
	const rows = $derived(ledgerQuery.data?.items ?? []);
	const totalCount = $derived(ledgerQuery.data?.totalCount ?? 0);
	const hasAccount = $derived(selectedAccountId != null);

	function resetPage(): void {
		pageNumber = 1;
	}

	function handlePropertyChange(value: string): void {
		propertyFilter = value;
		unitFilter = '';
		resetPage();
	}

	function handleSortChange(value: string | undefined): void {
		if (value && GENERAL_LEDGER_SORTS.includes(value as (typeof GENERAL_LEDGER_SORTS)[number])) {
			sort = value as GeneralLedgerSort;
		} else {
			sort = GENERAL_LEDGER_DEFAULT_SORT;
		}
		resetPage();
	}

	function locationLabel(row: GeneralLedgerRow): string {
		if (row.propertyId == null && row.unitId == null) return '—';
		if (row.propertyId == null) return `Unit #${row.unitId}`;
		return row.unitId == null ? `Property #${row.propertyId}` : `Property #${row.propertyId} · Unit #${row.unitId}`;
	}

	function recordIdLabel(value: number | null): string {
		return value == null ? '—' : `#${value}`;
	}

	function amount(value: number | null | undefined, currency: string, decrease = false): string {
		if (value == null || value === 0) return '—';
		const formatted = formatAccountingCurrency(value, currency);
		return decrease && value > 0 ? `-${formatted}` : formatted;
	}

	function increase(row: GeneralLedgerRow): number {
		return getIncreaseDecreaseAmounts(row).increase;
	}

	function decrease(row: GeneralLedgerRow): number {
		return getIncreaseDecreaseAmounts(row).decrease;
	}

	const simpleColumns: ColumnDef<GeneralLedgerRow>[] = [
		{ key: 'effectiveOn', title: 'Date', sortable: true, mobileRole: 'meta', cell: ledgerDateCell },
		{ key: 'description', title: 'What happened', mobileRole: 'title', cell: ledgerDescriptionCell, maxWidth: '24rem' },
		{ key: 'accountName', title: 'Category', mobileRole: 'subtitle', cell: ledgerAccountCell },
		{ key: 'location', title: 'Property / unit', mobileRole: 'meta', accessor: locationLabel },
		{ key: 'increase', title: 'Increase', mobileRole: 'metric', align: 'right', cell: ledgerIncreaseCell },
		{ key: 'decrease', title: 'Decrease', mobileRole: 'meta', align: 'right', cell: ledgerDecreaseCell }
	];

	const advancedColumns: ColumnDef<GeneralLedgerRow>[] = [
		{ key: 'effectiveOn', title: 'Effective date', sortable: true, mobileRole: 'meta', cell: ledgerDateCell },
		{ key: 'postedAtUtc', title: 'Posted date', sortable: true, mobileRole: 'meta', cell: ledgerPostedDateCell },
		{ key: 'journal', title: 'Journal', mobileRole: 'meta', accessor: (row) => row.sourceBusinessKey || row.journalEntryPublicId },
		{ key: 'accountCode', title: 'Account code', sortable: true, mobileRole: 'meta' },
		{ key: 'accountName', title: 'Account', mobileRole: 'subtitle' },
		{ key: 'description', title: 'Description', mobileRole: 'title', maxWidth: '24rem' },
		{ key: 'debitAmount', title: 'Debit', mobileRole: 'metric', align: 'right', cell: ledgerDebitCell },
		{ key: 'creditAmount', title: 'Credit', mobileRole: 'meta', align: 'right', cell: ledgerCreditCell },
		{ key: 'propertyId', title: 'Property', mobileRole: 'meta', accessor: (row) => recordIdLabel(row.propertyId) },
		{ key: 'unitId', title: 'Unit', mobileRole: 'meta', accessor: (row) => recordIdLabel(row.unitId) },
		{ key: 'tenantAccountId', title: 'Tenant account', mobileRole: 'meta', accessor: (row) => recordIdLabel(row.tenantAccountId) },
		{ key: 'ownerEntityId', title: 'Owner', mobileRole: 'meta', accessor: (row) => recordIdLabel(row.ownerEntityId) }
	];

	const ledgerColumns = $derived.by(() => {
		const columns = advanced ? advancedColumns : simpleColumns;
		return hasAccount
			? [
					...columns,
					{
						key: 'runningBalance',
						title: 'Balance',
						mobileRole: 'metric' as const,
						align: 'right' as const,
						cell: ledgerBalanceCell
					}
				]
			: columns;
	});
</script>

{#snippet ledgerDateCell(row: GeneralLedgerRow)}
	<span>{formatAccountingDate(row.effectiveOn)}</span>
{/snippet}

{#snippet ledgerPostedDateCell(row: GeneralLedgerRow)}
	<span>{formatAccountingDateTime(row.postedAtUtc)}</span>
{/snippet}

{#snippet ledgerDescriptionCell(row: GeneralLedgerRow)}
	<span>{row.description || '—'}</span>
{/snippet}

{#snippet ledgerAccountCell(row: GeneralLedgerRow)}
	<span>{row.accountName || '—'}</span>
{/snippet}

{#snippet ledgerIncreaseCell(row: GeneralLedgerRow)}
	<span class="text-success">{amount(increase(row), row.currency)}</span>
{/snippet}

{#snippet ledgerDecreaseCell(row: GeneralLedgerRow)}
	<span class="text-destructive">{amount(decrease(row), row.currency, true)}</span>
{/snippet}

{#snippet ledgerDebitCell(row: GeneralLedgerRow)}
	<span>{amount(row.debitAmount, row.currency)}</span>
{/snippet}

{#snippet ledgerCreditCell(row: GeneralLedgerRow)}
	<span>{amount(row.creditAmount, row.currency)}</span>
{/snippet}

{#snippet ledgerBalanceCell(row: GeneralLedgerRow)}
	<span>{formatAccountingCurrency(row.runningBalance, row.currency)}</span>
{/snippet}

{#if unauthorized}
	<section class={['rounded-xl border border-border bg-card p-6 text-center', className]} data-testid="general-ledger-unavailable">
		<h2 class="font-semibold">General ledger not available</h2>
		<p class="mt-1 text-sm text-muted-foreground">You do not have access to the general ledger for this workspace.</p>
	</section>
{:else}
	<section class={['space-y-4', className]} data-testid={testid}>
		<div class="rounded-xl border border-border bg-card p-4">
				<div class="mb-3 flex items-center gap-1.5">
					<h2 class="font-semibold">General ledger</h2>
					<HelpPopover
						title={ACCOUNTING_HELP.generalLedger.title}
						summary={ACCOUNTING_HELP.generalLedger.summary}
						learnMoreUrl={ACCOUNTING_HELP.generalLedger.href}
						testid="general-ledger-help"
					/>
				</div>
				<button
					type="button"
					class="flex w-full cursor-pointer items-center justify-between gap-3 lg:hidden"
					aria-expanded={filtersOpen}
					aria-controls="general-ledger-filters"
					onclick={() => filtersOpen = !filtersOpen}
				>
					<span class="font-semibold">Filters</span>
					<span class="text-sm text-muted-foreground">{filtersOpen ? 'Hide' : 'Show'}</span>
				</button>
				<div
					id="general-ledger-filters"
					class={[filtersOpen ? 'grid' : 'hidden', 'gap-3 pt-4 lg:grid lg:grid-cols-12 lg:items-end lg:pt-0']}
				>
					<div class="lg:col-span-4">
						<AccountPicker bind:selectedAccountId allowAll={true} />
					</div>
					<div class="lg:col-span-2">
						<RemoteRecordSelect
							queryKey={['general-ledger-properties', portfolioId]}
							label="Property"
							value={propertyFilter}
							selectedLabel={propertyFilter ? `Property #${propertyFilter}` : null}
							placeholder="All properties"
							clearLabel="All properties"
							searchPlaceholder="Search properties…"
							emptyLabel="No matching properties"
							loadPage={loadPropertyOptions}
						onValueChange={(value) => handlePropertyChange(value)}
							testid="general-ledger-property"
						/>
					</div>
					<div class="lg:col-span-2">
						<RemoteRecordSelect
							queryKey={['general-ledger-units', selectedPropertyId]}
							label="Unit"
							value={unitFilter}
							selectedLabel={unitFilter ? `Unit #${unitFilter}` : null}
							placeholder={propertyFilter ? 'All units' : 'Pick a property first'}
							clearLabel="All units"
							searchPlaceholder="Search units…"
							emptyLabel="No matching units"
							loadPage={loadUnitOptions}
							disabled={!propertyFilter}
							onValueChange={(value) => { unitFilter = value; resetPage(); }}
							testid="general-ledger-unit"
						/>
					</div>
					<div class="lg:col-span-2">
						<label class="grid gap-1.5 text-sm font-medium">Source type
							<Select.Root type="single" bind:value={sourceFilter}>
								<Select.Trigger class="w-full" aria-label="Source type" data-testid="general-ledger-source">
									{sourceFilter ? formatSourceTypeLabel(sourceFilter) : 'All sources'}
								</Select.Trigger>
								<Select.Content>
									<Select.Item value="" label="All sources">All sources</Select.Item>
									{#each SOURCE_TYPES as sourceType}
										<Select.Item value={sourceType} label={formatSourceTypeLabel(sourceType)}>{formatSourceTypeLabel(sourceType)}</Select.Item>
									{/each}
								</Select.Content>
							</Select.Root>
						</label>
					</div>
					<div class="lg:col-span-2">
						<SearchInput bind:value={search} placeholder="Search descriptions or journals…" testid="general-ledger-search" />
					</div>
					<div class="lg:col-span-2">
						<label class="grid gap-1.5 text-sm font-medium">From<DatePicker bind:value={effectiveFrom} testid="general-ledger-from" /></label>
					</div>
					<div class="lg:col-span-2">
						<label class="grid gap-1.5 text-sm font-medium">To<DatePicker bind:value={effectiveTo} min={effectiveFrom || undefined} testid="general-ledger-to" /></label>
					</div>
					<div class="lg:col-span-2 flex justify-end">
						<span class="text-xs text-muted-foreground">{advanced ? 'Advanced details' : 'Plain-language details'}</span>
					</div>
				</div>
		</div>

		{#if !hasAccount}
			<div class="flex items-start gap-3 rounded-lg border border-primary/30 bg-primary/5 px-4 py-3 text-sm" data-testid="general-ledger-no-account-banner">
				<Info class="mt-0.5 size-4 shrink-0 text-primary" />
				<p>Choose one category or account to see its running balance. Every account has its own balance, so there is no single running balance for the entire general ledger.</p>
			</div>
			<section class="overflow-hidden rounded-xl border border-border bg-card" data-testid="company-wide-balances">
				<header class="border-b px-4 py-3"><h3 class="font-semibold">Whole-business account balances</h3><p class="text-sm text-muted-foreground">Every category across the portfolio. Debits equal credits overall; open the <a class="underline" href="/accounting/trial-balance">trial balance</a> for the accounting proof.</p></header>
				{#if balancesQuery.isLoading}<p class="p-4 text-sm text-muted-foreground">Loading balances…</p>{:else}
						<div class="divide-y" data-testid="company-balance-groups">
							{#each (balancesQuery.data?.rows ?? []) as row}
								{#if !row.isZeroBalance}
									<button class="flex w-full justify-between rounded px-4 py-2 text-left text-sm hover:bg-muted" onclick={() => (selectedAccountId = row.accountId)} data-testid={`company-balance-row-${row.accountId}`}>
										<span><span class="mr-2 text-xs font-medium uppercase text-muted-foreground">{row.accountType}</span>{advanced ? `${row.accountCode} · ` : ''}{row.accountName}</span>
										<span class="font-mono">{formatAccountingCurrency(row.debitBalance - row.creditBalance)}</span>
									</button>
								{/if}
							{/each}
							{#if (balancesQuery.data?.zeroBalanceCount ?? 0) > 0}
								<details class="m-4 rounded-lg border border-border/70 px-2 py-1.5" data-testid="company-balance-zero-disclosure">
									<summary class="cursor-pointer text-xs font-medium text-muted-foreground">Show {balancesQuery.data?.zeroBalanceCount} {balancesQuery.data?.zeroBalanceCount === 1 ? 'account' : 'accounts'} with no balance</summary>
									<div class="mt-1 border-t border-border/60 pt-1">
										{#each (balancesQuery.data?.rows ?? []) as row}
											{#if row.isZeroBalance}
												<button class="flex w-full justify-between rounded px-2 py-1 text-left text-xs text-muted-foreground hover:bg-muted hover:text-foreground" onclick={() => (selectedAccountId = row.accountId)} data-testid={`company-zero-balance-row-${row.accountId}`}><span><span class="mr-2 uppercase">{row.accountType}</span>{advanced ? `${row.accountCode} · ` : ''}{row.accountName}</span><span class="font-mono">{formatAccountingCurrency(row.debitBalance - row.creditBalance)}</span></button>
											{/if}
										{/each}
									</div>
								</details>
							{/if}
						</div>
					<p class="border-t px-4 py-3 text-sm font-medium" data-testid="company-trial-balance-note">Debits {formatAccountingCurrency(balancesQuery.data?.totalDebits)} · Credits {formatAccountingCurrency(balancesQuery.data?.totalCredits)} · {balancesQuery.data?.isBalanced ? 'Balanced' : 'Review needed'}</p>
				{/if}
			</section>
		{/if}

		{#if ledgerQuery.isError}
			<div class="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-destructive/40 bg-destructive/5 p-5" role="alert" data-testid="general-ledger-error">
				<p class="text-sm text-destructive">The general ledger could not be loaded.</p>
				<Button variant="outline" size="sm" onclick={() => ledgerQuery.refetch()}>Try again</Button>
			</div>
		{:else if hasAccount}
			<DataGrid
				data={rows}
				columns={ledgerColumns}
				loading={ledgerQuery.isLoading || ledgerQuery.isFetching}
				emptyMessage="No accounting activity for these filters yet."
				getRowKey={(row) => `${row.journalEntryPublicId}-${row.lineId}`}
				getRowTestId={(row) => `general-ledger-row-${row.lineId}`}
				onRowClick={(row) => (journalPublicId = row.journalEntryPublicId)}
				data-testid="general-ledger-grid"
				pageSize={PAGE_SIZE}
				page={pageNumber}
				totalCount={totalCount}
				serverSide
				onPageChange={(nextPage) => (pageNumber = nextPage)}
				sort={sort}
				onSortChange={handleSortChange}
			/>
		{/if}
	</section>
	{/if}

	<JournalDetailDrawer bind:journalPublicId />
