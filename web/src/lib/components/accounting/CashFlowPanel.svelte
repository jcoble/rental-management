<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { ChevronDown, ChevronUp, Info, RefreshCw } from '@lucide/svelte';
	import { cashFlow } from '$lib/api/endpoints/cash-flow';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { ACCOUNTING_HELP } from '$lib/accounting/accounting-help';
	import {
		formatAccountingCurrency,
		formatAccountingDate
	} from '$lib/accounting/accounting-display';
	import {
		buildCashFlowChartPoints,
		buildCashFlowMonthlyRanges,
		cashFlowBarPercent,
		cashFlowChartScale,
		cashFlowNetPositionPercent,
		cashFlowPropertyDetails,
		CASH_FLOW_PERIOD_PRESETS,
		resolveCashFlowRange,
		sortCashFlowProperties,
		type CashFlowChartMetric,
		type CashFlowChartPoint,
		type CashFlowMonthlyResponse,
		type CashFlowPeriodPreset,
		type CashFlowPropertySortKey,
		type CashFlowRange,
		type CashFlowSortDirection
	} from '$lib/accounting/cash-flow-state';
	import { getAccountingDetailMode } from './AccountingDetailMode.svelte';
	import CashFlowWaterfall from './CashFlowWaterfall.svelte';
	import RangeDatePicker from '$lib/components/shared/RangeDatePicker.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Table from '$lib/components/ui/table';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';

	let { portfolioId }: { portfolioId?: number | null } = $props();

	const authState = getAuthState();
	const detailMode = getAccountingDetailMode();
	const advanced = $derived(detailMode?.mode === 'advanced');
	const activePortfolioId = $derived(portfolioId ?? getCurrentPortfolioId());
	const reportDate = new Date();

	let selectedPreset = $state<CashFlowPeriodPreset>('thisMonth');
	let customFrom = $state('');
	let customTo = $state('');
	let sortKey = $state<CashFlowPropertySortKey>('cashFlow');
	let sortDirection = $state<CashFlowSortDirection>('desc');
	let expandedPropertyId = $state<number | null>(null);

	const selectedRange = $derived<CashFlowRange | null>(
		resolveCashFlowRange(selectedPreset, reportDate, { from: customFrom, to: customTo })
	);
	const monthlyRanges = $derived(buildCashFlowMonthlyRanges(selectedRange));

	const summaryQuery = createQuery(() => ({
		queryKey: [
			'accounting-cash-flow',
			activePortfolioId,
			selectedRange?.from ?? '',
			selectedRange?.to ?? ''
		],
		enabled: authState.isAuthenticated && activePortfolioId > 0 && !!selectedRange,
		queryFn: () => {
			const range = selectedRange;
			if (!range) throw new Error('A complete cash-flow date range is required.');
			return cashFlow.get(range);
		}
	}));

	const monthlyQuery = createQuery(() => ({
		queryKey: [
			'accounting-cash-flow-monthly',
			activePortfolioId,
			selectedRange?.from ?? '',
			selectedRange?.to ?? ''
		],
		enabled:
			authState.isAuthenticated &&
			activePortfolioId > 0 &&
			monthlyRanges.length > 1 &&
			!summaryQuery.isError,
		queryFn: async (): Promise<CashFlowMonthlyResponse[]> =>
			Promise.all(
				monthlyRanges.map(async (range) => ({
					range,
					response: await cashFlow.get(range)
				}))
			)
	}));

	const response = $derived(summaryQuery.data);
	const properties = $derived(
		sortCashFlowProperties(response?.properties ?? [], sortKey, sortDirection)
	);
	const monthlyResponses = $derived.by<CashFlowMonthlyResponse[]>(() => {
		if (monthlyRanges.length === 1 && response) {
			return [{ range: monthlyRanges[0], response }];
		}
		return monthlyQuery.data ?? [];
	});
	const chartPoints = $derived(buildCashFlowChartPoints(monthlyResponses));
	const chartScale = $derived(cashFlowChartScale(chartPoints));
	const loading = $derived(authState.isLoading || summaryQuery.isLoading);
	const monthlyLoading = $derived(monthlyRanges.length > 1 && monthlyQuery.isLoading);
	const unauthorized = $derived(
		!authState.isLoading &&
		(!authState.isAuthenticated || isAuthorizationError(summaryQuery.error) || isAuthorizationError(monthlyQuery.error))
	);
	const periodLabel = $derived(
		response
			? `${formatAccountingDate(response.from)} – ${formatAccountingDate(response.to)}`
			: selectedRange
				? `${formatAccountingDate(selectedRange.from)} – ${formatAccountingDate(selectedRange.to)}`
				: 'Choose a complete date range'
	);

	const propertyColumns: ReadonlyArray<{
		key: CashFlowPropertySortKey;
		simpleLabel: string;
		advancedLabel: string;
	}> = [
		{ key: 'income', simpleLabel: 'Cash in', advancedLabel: 'Income' },
		{ key: 'operatingExpenses', simpleLabel: 'Cash out', advancedLabel: 'Operating expenses' },
		{ key: 'debtService', simpleLabel: 'Loan payments', advancedLabel: 'Debt service' },
		{ key: 'cashFlow', simpleLabel: 'Cash flow', advancedLabel: 'Net cash flow' }
	];

	function isAuthorizationError(error: unknown): boolean {
		if (!error || typeof error !== 'object' || !('status' in error)) return false;
		const status = (error as { status?: unknown }).status;
		return typeof status === 'number' && [401, 403, 404].includes(status);
	}

	function selectPreset(preset: CashFlowPeriodPreset): void {
		selectedPreset = preset;
		expandedPropertyId = null;
		if (preset !== 'custom') {
			customFrom = '';
			customTo = '';
		}
	}

	function updateCustomRange(range: { start: string; end: string }): void {
		customFrom = range.start;
		customTo = range.end;
		if (range.start && range.end) selectedPreset = 'custom';
	}

	function toggleProperty(propertyId: number): void {
		expandedPropertyId = expandedPropertyId === propertyId ? null : propertyId;
	}

	function selectSort(nextKey: CashFlowPropertySortKey): void {
		if (sortKey === nextKey) {
			sortDirection = sortDirection === 'desc' ? 'asc' : 'desc';
			return;
		}
		sortKey = nextKey;
		sortDirection = nextKey === 'propertyName' ? 'asc' : 'desc';
	}

	function formatOutflow(value: number | null | undefined): string {
		const formatted = formatAccountingCurrency(value);
		if (formatted === '—' || value == null || value < 0) return formatted;
		return `−${formatted}`;
	}

	function propertyDetailLabel(key: 'operatingExpenses' | 'debtService'): string {
		if (key === 'operatingExpenses') return advanced ? 'Operating expenses' : 'Operating costs';
		return advanced ? 'Debt service' : 'Loan payments';
	}

	function chartMetricValue(point: CashFlowChartPoint, metric: CashFlowChartMetric): number {
		return point[metric];
	}

	function chartBarHeight(point: CashFlowChartPoint, metric: CashFlowChartMetric): number {
		return cashFlowBarPercent(chartMetricValue(point, metric), chartScale);
	}

	function chartNetPosition(point: CashFlowChartPoint): number {
		return cashFlowNetPositionPercent(point.cashFlow, chartScale);
	}

	function sortAriaValue(key: CashFlowPropertySortKey): 'ascending' | 'descending' | 'none' {
		if (sortKey !== key) return 'none';
		return sortDirection === 'asc' ? 'ascending' : 'descending';
	}
</script>

<div class="space-y-6" data-testid="cash-flow-panel">
	{#if unauthorized}
		<section class="rounded-2xl border border-border bg-card p-6" data-testid="cash-flow-unauthorized">
			<h2 class="text-base font-semibold">This view is not available</h2>
			<p class="mt-2 text-sm text-muted-foreground">The requested financial view could not be opened.</p>
		</section>
	{:else}
		<section class="rounded-2xl border border-border bg-card px-4 py-4 sm:px-5" aria-labelledby="cash-flow-period-heading">
			<div class="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
				<div>
					<p id="cash-flow-period-heading" class="text-xs font-semibold uppercase tracking-[0.16em] text-muted-foreground">Period</p>
					<p class="mt-1 text-sm text-muted-foreground">{periodLabel}</p>
				</div>
				<div class="flex flex-wrap items-center gap-2" role="group" aria-label="Cash-flow period">
					{#each CASH_FLOW_PERIOD_PRESETS as preset (preset.key)}
						<Button
							variant={selectedPreset === preset.key ? 'secondary' : 'outline'}
							size="sm"
							onclick={() => selectPreset(preset.key)}
							aria-pressed={selectedPreset === preset.key}
							data-testid={`cash-flow-period-${preset.key}`}
						>
							{preset.label}
						</Button>
					{/each}
				</div>
			</div>
			{#if selectedPreset === 'custom'}
				<div class="mt-4 max-w-md" data-testid="cash-flow-custom-range">
					<RangeDatePicker
						start={customFrom}
						end={customTo}
						onchange={updateCustomRange}
						placeholder="Choose a start and end date"
						testid="cash-flow-custom-date-range"
					/>
				</div>
			{/if}
		</section>

		{#if selectedPreset === 'custom' && !selectedRange}
			<section class="rounded-2xl border border-dashed border-border bg-card p-6" data-testid="cash-flow-custom-empty">
			<h2 class="text-base font-semibold">Choose a complete date range</h2>
			<p class="mt-2 text-sm text-muted-foreground">Pick both a start date and an end date to load cash flow.</p>
		</section>
		{:else if loading}
			<section class="rounded-2xl border border-border bg-card p-5" role="status" aria-label="Loading cash flow">
				<div class="h-4 w-36 animate-pulse rounded bg-muted"></div>
				<div class="mt-3 h-10 w-52 animate-pulse rounded bg-muted"></div>
				<div class="mt-4 h-4 w-64 animate-pulse rounded bg-muted"></div>
			</section>
			<CashFlowWaterfall income={0} operatingExpenses={0} noi={0} debtService={0} cashFlow={0} {advanced} loading />
		{:else if summaryQuery.isError}
			<section class="rounded-2xl border border-destructive/40 bg-card p-6" data-testid="cash-flow-error" role="alert">
			<h2 class="text-base font-semibold">Cash flow could not load</h2>
			<p class="mt-2 text-sm text-muted-foreground">The server did not return this cash-flow period.</p>
			<Button class="mt-4" variant="outline" size="sm" onclick={() => void summaryQuery.refetch()}>
				<RefreshCw class="size-4" /> Try again
			</Button>
		</section>
		{:else if response}
			<section class="rounded-2xl border border-border bg-card p-5 sm:p-6" data-testid="cash-flow-headline">
				<div class="flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
					<div>
						<div class="flex items-center gap-1.5">
							<p class="text-xs font-semibold uppercase tracking-[0.16em] text-muted-foreground">Net cash flow</p>
							<HelpPopover
								title={ACCOUNTING_HELP.cashFlow.title}
								summary={ACCOUNTING_HELP.cashFlow.summary}
								learnMoreUrl={ACCOUNTING_HELP.cashFlow.href}
								testid="cash-flow-help"
							/>
						</div>
						<h1 class="mt-2 font-mono text-3xl font-semibold tracking-tight sm:text-4xl" data-testid="cash-flow-headline-value">
							{formatAccountingCurrency(response.totalCashFlow)}
						</h1>
					</div>
					<p class="text-sm text-muted-foreground">{periodLabel}</p>
				</div>
			</section>

			<CashFlowWaterfall
				income={response.totalIncome}
				operatingExpenses={response.totalOperatingExpenses}
				noi={response.totalNoi}
				debtService={response.totalDebtService}
				cashFlow={response.totalCashFlow}
				{advanced}
			/>

			<section class="rounded-2xl border border-border bg-card" data-testid="cash-flow-monthly-chart" aria-labelledby="cash-flow-monthly-heading">
				<header class="flex flex-col gap-3 border-b border-border px-4 py-4 sm:flex-row sm:items-start sm:justify-between sm:px-5">
					<div>
						<p class="text-xs font-semibold uppercase tracking-[0.16em] text-muted-foreground">Trend</p>
						<h2 id="cash-flow-monthly-heading" class="mt-1 text-base font-semibold">Monthly cash flow</h2>
						<p class="mt-1 text-sm text-muted-foreground">Money in, operating costs, loan payments, and net cash flow by month.</p>
					</div>
					<div class="flex flex-wrap gap-x-4 gap-y-2 text-xs text-muted-foreground" aria-label="Chart legend">
						<span class="inline-flex items-center gap-1.5"><span class="size-2 rounded-full bg-emerald-500"></span>{advanced ? 'Income' : 'Money in'}</span>
						<span class="inline-flex items-center gap-1.5"><span class="size-2 rounded-full bg-amber-500"></span>{advanced ? 'Operating expenses' : 'Operating costs'}</span>
						<span class="inline-flex items-center gap-1.5"><span class="size-2 rounded-full bg-orange-500"></span>{advanced ? 'Debt service' : 'Loan payments'}</span>
						<span class="inline-flex items-center gap-1.5"><span class="size-2 rounded-full bg-primary"></span>{advanced ? 'Net cash flow' : 'Cash flow'}</span>
					</div>
				</header>

				{#if monthlyLoading}
					<div class="flex min-h-64 items-end gap-4 overflow-hidden px-5 py-5" role="status" aria-label="Loading monthly cash flow">
						{#each Array(6) as _, index}
							<div class="flex w-20 flex-col items-center gap-3" data-testid={`cash-flow-chart-loading-${index}`}>
								<div class="h-40 w-full animate-pulse rounded-t-lg bg-muted"></div>
								<div class="h-3 w-14 animate-pulse rounded bg-muted"></div>
							</div>
						{/each}
					</div>
				{:else if monthlyQuery.isError && monthlyRanges.length > 1}
					<div class="p-5" data-testid="cash-flow-monthly-error" role="alert">
						<p class="text-sm font-medium">Monthly detail could not load.</p>
						<p class="mt-1 text-sm text-muted-foreground">The headline and property totals are still available for this period.</p>
						<Button class="mt-4" variant="outline" size="sm" onclick={() => void monthlyQuery.refetch()}>
							<RefreshCw class="size-4" /> Try monthly detail again
						</Button>
					</div>
				{:else if chartPoints.length === 0}
					<div class="p-5 text-sm text-muted-foreground" data-testid="cash-flow-monthly-empty">No monthly activity for these filters yet.</div>
				{:else}
					<figure class="overflow-x-auto px-4 py-5 sm:px-5">
						<div class="flex min-w-max items-end gap-4" role="img" aria-label="Monthly cash-flow chart">
							{#each chartPoints as point (point.key)}
								<div class="w-24 shrink-0" data-testid={`cash-flow-month-${point.key}`}>
									<div class="relative h-56 rounded-lg bg-muted/20" role="group" aria-label={`${point.label}: money in ${formatAccountingCurrency(point.income)}, operating costs ${formatOutflow(point.operatingExpenses)}, loan payments ${formatOutflow(point.debtService)}, cash flow ${formatAccountingCurrency(point.cashFlow)}`}>
										<div class="absolute inset-x-2 top-1/2 border-t border-dashed border-border"></div>
										<div class="absolute inset-x-2 top-0 flex h-1/2 items-end justify-center gap-1 px-1 pb-1">
											<span class="w-3 rounded-t bg-emerald-500" style={`height: ${chartBarHeight(point, 'income')}%`} aria-hidden="true"></span>
										</div>
										<div class="absolute inset-x-2 bottom-0 flex h-1/2 items-start justify-center gap-1 px-1 pt-1">
											<span class="w-3 rounded-b bg-amber-500" style={`height: ${chartBarHeight(point, 'operatingExpenses')}%`} aria-hidden="true"></span>
											<span class="w-3 rounded-b bg-orange-500" style={`height: ${chartBarHeight(point, 'debtService')}%`} aria-hidden="true"></span>
										</div>
										<span class="absolute left-1/2 size-3 -translate-x-1/2 rounded-full border-2 border-card bg-primary" style={`bottom: ${chartNetPosition(point)}%`} aria-hidden="true"></span>
									</div>
									<p class="mt-2 text-center text-xs font-medium">{point.label}</p>
									<div class="mt-2 space-y-0.5 text-center text-[11px] leading-tight text-muted-foreground">
										<p>In {formatAccountingCurrency(point.income)}</p>
										<p>Costs {formatOutflow(point.operatingExpenses)}</p>
										<p>Loans {formatOutflow(point.debtService)}</p>
										<p class="font-medium text-foreground">Net {formatAccountingCurrency(point.cashFlow)}</p>
									</div>
								</div>
							{/each}
						</div>
						<figcaption class="mt-4 text-xs text-muted-foreground">Bars show the direction of each server-provided monthly figure. Exact amounts are listed below every month.</figcaption>
					</figure>
				{/if}
			</section>

			<section class="overflow-hidden rounded-2xl border border-border bg-card" data-testid="cash-flow-property-ranking" aria-labelledby="cash-flow-property-heading">
			<header class="flex flex-col gap-3 border-b border-border px-4 py-4 sm:flex-row sm:items-start sm:justify-between sm:px-5">
				<div>
					<p class="text-xs font-semibold uppercase tracking-[0.16em] text-muted-foreground">Portfolio view</p>
					<h2 id="cash-flow-property-heading" class="mt-1 text-base font-semibold">Cash flow by property</h2>
					<p class="mt-1 text-sm text-muted-foreground">Sorted by {sortKey === 'cashFlow' ? (sortDirection === 'desc' ? 'strongest' : 'weakest') : 'the selected column'} cash flow.</p>
				</div>
				<Button variant="outline" size="sm" onclick={() => (sortDirection = sortDirection === 'desc' ? 'asc' : 'desc')} data-testid="cash-flow-sort-direction">
					{sortKey === 'cashFlow' ? (sortDirection === 'desc' ? 'Strongest first' : 'Weakest first') : sortDirection === 'desc' ? 'Largest first' : 'Smallest first'}
				</Button>
			</header>

			{#if properties.length === 0}
				<div class="p-5 text-sm text-muted-foreground" data-testid="cash-flow-property-empty">No cash flow activity for these filters yet.</div>
			{:else}
				<Table.Root>
					<Table.Header>
						<Table.Row>
							<Table.Head class="w-8 px-2 sm:px-4"><span class="sr-only">Expand</span></Table.Head>
							<Table.Head class="min-w-44">Property</Table.Head>
							{#each propertyColumns as column (column.key)}
								<Table.Head class="text-right" aria-sort={sortAriaValue(column.key)}>
									<button class="font-medium underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" onclick={() => selectSort(column.key)}>
										{advanced ? column.advancedLabel : column.simpleLabel}
									</button>
								</Table.Head>
							{/each}
						</Table.Row>
					</Table.Header>
					<Table.Body>
						{#each properties as property (property.propertyId)}
							<Table.Row data-testid={`cash-flow-property-${property.propertyId}`}>
								<Table.Cell class="w-8 px-2 sm:px-4">
									<Button
										variant="ghost"
										size="icon"
										class="size-8"
										onclick={() => toggleProperty(property.propertyId)}
										aria-expanded={expandedPropertyId === property.propertyId}
										aria-label={`${expandedPropertyId === property.propertyId ? 'Collapse' : 'Expand'} ${property.propertyName} details`}
										data-testid={`cash-flow-property-toggle-${property.propertyId}`}
									>
										{#if expandedPropertyId === property.propertyId}<ChevronUp class="size-4" />{:else}<ChevronDown class="size-4" />{/if}
									</Button>
								</Table.Cell>
								<Table.Cell class="font-medium">{property.propertyName}</Table.Cell>
								<Table.Cell class="text-right font-mono tabular-nums">{formatAccountingCurrency(property.income)}</Table.Cell>
								<Table.Cell class="text-right font-mono tabular-nums text-destructive">{formatOutflow(property.operatingExpenses)}</Table.Cell>
								<Table.Cell class="text-right font-mono tabular-nums text-destructive">{formatOutflow(property.debtService)}</Table.Cell>
								<Table.Cell class="text-right font-mono font-medium tabular-nums">{formatAccountingCurrency(property.cashFlow)}</Table.Cell>
							</Table.Row>
							{#if expandedPropertyId === property.propertyId}
								<Table.Row class="bg-muted/20" data-testid={`cash-flow-property-details-${property.propertyId}`}>
									<Table.Cell colspan={6} class="px-4 py-4 sm:px-12">
										<div class="grid gap-3 sm:grid-cols-2">
											{#each cashFlowPropertyDetails(property) as detail (detail.key)}
												<div class="flex items-center justify-between gap-4 rounded-lg border border-border bg-card px-3 py-2 text-sm">
													<span class="text-muted-foreground">{propertyDetailLabel(detail.key)}</span>
													<span class="font-mono tabular-nums">{formatOutflow(detail.amount)}</span>
												</div>
											{/each}
										</div>
										<p class="mt-3 text-xs text-muted-foreground">The cash-flow service currently returns period totals for each property; category and individual loan rows are not included in this response.</p>
									</Table.Cell>
								</Table.Row>
							{/if}
						{/each}
					</Table.Body>
				</Table.Root>
			{/if}
		</section>

		<section class="flex items-start gap-3 rounded-2xl border border-border bg-muted/20 p-4 sm:p-5" data-testid="cash-flow-profit-note">
			<Info class="mt-0.5 size-5 shrink-0 text-primary" aria-hidden="true" />
			<div class="text-sm leading-6">
				<p>Cash flow shows the cash the properties produced after operating costs and full loan payments. Profit can differ because mortgage principal uses cash but is not an expense, while depreciation is an expense that does not use cash.</p>
				<p class="mt-2 text-muted-foreground">This view excludes refundable tenant deposits and non-cash depreciation. Owner contributions and distributions are not property operating cash flow.</p>
			</div>
		</section>
	{/if}
	{/if}
</div>
