<!--
  Reports Hub — generic report viewer. Resolves the report from /reports/catalog by its [report]
  key, renders a parameter bar (properties multi-select + date range + year/days where the report
  declares them), generates the report on demand, and renders it as a clean table with subtotals,
  totals, semantic money colors and tabular-nums. Actions menu offers CSV / Print / (for external
  reports) deep-links to the existing pages.
-->
<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { createQuery } from '@tanstack/svelte-query';
	import {
		reports,
		type ReportCatalogEntry,
		type ReportParamKey,
		type ReportRequestParams,
		type RentRollResponse,
		type RentLedgerResponse,
		type DelinquencyResponse,
		type CashFlowResponse,
		type GeneralLedgerResponse,
		type PropertyProfitAndLossResponse,
		type OccupancyResponse,
		type LeaseExpirationsResponse,
		type SecurityDepositRegisterResponse,
		type Vendor1099Response,
		type OwnerDistributionsResponse,
		type WorkOrderReportResponse,
	} from '$lib/api/endpoints/reports';
	import { properties as propertiesApi } from '$lib/api/endpoints/properties';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { apiErrorMessage } from '$lib/utils/toast';
	import { formatDate, formatDateOnly } from '$lib/utils/date';
	import {
		formatMoneyCategoryLabel,
		formatMoneyEntryLabel
	} from '$lib/accounting/money-display';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { reportDescription, reportTitle } from '$lib/reports/report-display';
	import RangeDatePicker from '$lib/components/shared/RangeDatePicker.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Popover from '$lib/components/ui/popover';
	import {
		DropdownMenu,
		DropdownMenuContent,
		DropdownMenuItem,
		DropdownMenuTrigger,
	} from '$lib/components/ui/dropdown-menu';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import {
		ArrowLeft,
		Building2,
		Download,
		Printer,
		MoreHorizontal,
		ExternalLink,
		AlertTriangle,
		RefreshCw,
		FileText,
	} from '@lucide/svelte';

	const portfolioId = $derived(getCurrentPortfolioId());
	const reportKey = $derived(page.params.report ?? '');

	// --- Catalog (to resolve this report's title, endpoint, accepted params) -------------------------
	const catalogQuery = createQuery(() => ({
		queryKey: ['reports-catalog'],
		queryFn: () => reports.catalog(),
		staleTime: 5 * 60_000,
	}));

	const entry = $derived.by<ReportCatalogEntry | undefined>(() => {
		for (const cat of catalogQuery.data?.categories ?? []) {
			const found = cat.reports.find((r) => r.key === reportKey);
			if (found) return found;
		}
		return undefined;
	});
	const accepts = $derived(new Set<ReportParamKey>(entry?.params ?? []));

	// External reports are only deep-linked, never viewed here.
	const externalLinks: Record<string, string> = {
		'accounting-profit-and-loss': '/accounting/profit-and-loss',
		'accounting-balance-sheet': '/accounting/balance-sheet',
		'accounting-trial-balance': '/accounting/trial-balance',
		'accounting-general-ledger': '/accounting?tab=general-ledger',
		'accounting-cash-flow': '/accounting?tab=cash-flow',
		'schedule-e': '/tax',
		'year-end-packet': '/tax',
		'owner-statement': '/owners-report',
	};

	// --- Properties (for the multi-select) -----------------------------------------------------------
	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => propertiesApi.list(portfolioId, { take: 200 }),
		enabled: accepts.has('propertyIds') || accepts.has('propertyId'),
	}));
	const propertyList = $derived(propertiesQuery.data ?? []);

	// --- Param state ---------------------------------------------------------------------------------
	let rangeStart = $state('');
	let rangeEnd = $state('');
	let selectedPropertyIds = $state<number[]>([]); // empty = all properties
	let year = $state('');
	let days = $state('90');
	let reportSkip = $state(0);
	let reportTake = $state(20);
	let reportSort = $state('property');

	// The applied params snapshot — only changes when the user clicks Generate, so the table is stable.
	let applied = $state<ReportRequestParams>({});
	let hasGenerated = $state(false);

	// Reset params + result whenever the report changes (navigating between viewers).
	$effect(() => {
		reportKey;
		rangeStart = '';
		rangeEnd = '';
		selectedPropertyIds = [];
		year = '';
		days = '90';
		reportSkip = 0;
		reportTake = 20;
		reportSort = 'property';
		applied = {};
		hasGenerated = false;
	});

	function collectParams(): ReportRequestParams {
		const p: ReportRequestParams = {};
		if (accepts.has('from') && rangeStart) p.from = rangeStart;
		if (accepts.has('to') && rangeEnd) p.to = rangeEnd;
		if (accepts.has('propertyIds') && selectedPropertyIds.length) p.propertyIds = [...selectedPropertyIds];
		// A single-property report (cash-and-operating-activity) reuses the same multi-select; send the first pick.
		if (accepts.has('propertyId') && selectedPropertyIds.length) p.propertyId = selectedPropertyIds[0];
		if (accepts.has('year') && year.trim()) p.year = Number(year);
		if (accepts.has('days') && days) p.days = Number(days);
		if (accepts.has('skip')) p.skip = reportSkip;
		if (accepts.has('take')) p.take = reportTake;
		if (accepts.has('sort')) p.sort = reportSort;
		return p;
	}

	function generate() {
		reportSkip = 0;
		applied = { ...collectParams(), skip: accepts.has('skip') ? 0 : undefined };
		hasGenerated = true;
	}

	function setReportPage(skip: number) {
		const nextSkip = Math.max(0, skip);
		reportSkip = nextSkip;
		applied = { ...collectParams(), skip: nextSkip };
		hasGenerated = true;
	}

	// Auto-run on first load once the catalog entry resolves (reports have sensible server defaults).
	$effect(() => {
		if (entry && !entry.external && !hasGenerated) {
			applied = collectParams();
			hasGenerated = true;
		}
	});

	// --- Report data ---------------------------------------------------------------------------------
	const reportQuery = createQuery(() => ({
		queryKey: ['report', reportKey, portfolioId, applied],
		queryFn: () => reports.run(entry!, applied),
		enabled: Boolean(entry) && !entry?.external && hasGenerated,
	}));

	// --- Property multi-select helpers ---------------------------------------------------------------
	let propPopoverOpen = $state(false);
	function toggleProperty(id: number, checked: boolean) {
		selectedPropertyIds = checked
			? [...selectedPropertyIds, id]
			: selectedPropertyIds.filter((x) => x !== id);
	}
	const propertyLabel = $derived.by(() => {
		if (selectedPropertyIds.length === 0) return 'All properties';
		if (selectedPropertyIds.length === 1) {
			return propertyList.find((p) => p.id === selectedPropertyIds[0])?.name ?? '1 property';
		}
		return `${selectedPropertyIds.length} properties`;
	});

	// --- Formatting ----------------------------------------------------------------------------------
	function money(value: number | null | undefined) {
		return new Intl.NumberFormat('en-US', {
			style: 'currency',
			currency: 'USD',
			minimumFractionDigits: 2,
		}).format(value || 0);
	}
	function pct(value: number | null | undefined) {
		return `${(value ?? 0).toFixed(1)}%`;
	}
	// Semantic color class for a money value (income green / negative red).
	function netClass(value: number) {
		return value < 0 ? 'text-destructive' : value > 0 ? 'text-success' : '';
	}

	// --- Period header label -------------------------------------------------------------------------
	const periodLabel = $derived.by(() => {
		const data = reportQuery.data as Record<string, unknown> | undefined;
		if (!data) return '';
		if (accepts.has('year')) return `Year ${String(data.year ?? applied.year ?? '')}`;
		if (accepts.has('days')) return `Next ${applied.days ?? 90} days`;
		if (data.from && data.to) return `${formatDateOnly(data.from as string)} – ${formatDateOnly(data.to as string)}`;
		if (data.asOf) return `As of ${formatDateOnly(data.asOf as string)}`;
		if (data.generatedAt) return `As of ${formatDate(data.generatedAt as string)}`;
		return '';
	});

	// --- Typed views (narrowed by report key at render time) -----------------------------------------
	const rentRoll = $derived(reportKey === 'rent-roll' ? (reportQuery.data as RentRollResponse | undefined) : undefined);
	const rentLedger = $derived(reportKey === 'rent-ledger' ? (reportQuery.data as RentLedgerResponse | undefined) : undefined);
	const delinquency = $derived(reportKey === 'delinquency' ? (reportQuery.data as DelinquencyResponse | undefined) : undefined);
	const cashFlow = $derived(
		reportKey === 'cash-flow' || reportKey === 'income-expense-statement'
			? (reportQuery.data as CashFlowResponse | undefined)
			: undefined
	);
	const generalLedger = $derived(reportKey === 'cash-and-operating-activity' ? (reportQuery.data as GeneralLedgerResponse | undefined) : undefined);
	const propertyPnl = $derived(reportKey === 'property-pnl-summary' ? (reportQuery.data as PropertyProfitAndLossResponse | undefined) : undefined);
	const occupancy = $derived(reportKey === 'occupancy' ? (reportQuery.data as OccupancyResponse | undefined) : undefined);
	const leaseExp = $derived(reportKey === 'lease-expirations' ? (reportQuery.data as LeaseExpirationsResponse | undefined) : undefined);
	const deposits = $derived(reportKey === 'security-deposit-register' ? (reportQuery.data as SecurityDepositRegisterResponse | undefined) : undefined);
	const vendor1099 = $derived(reportKey === 'vendor-1099' ? (reportQuery.data as Vendor1099Response | undefined) : undefined);
	const ownerDist = $derived(reportKey === 'owner-distributions' ? (reportQuery.data as OwnerDistributionsResponse | undefined) : undefined);
	const workOrders = $derived(reportKey === 'work-orders' ? (reportQuery.data as WorkOrderReportResponse | undefined) : undefined);

	const isEmpty = $derived.by(() => {
		const data = reportQuery.data as Record<string, unknown> | undefined;
		if (!data) return false;
		const rows = (data.rows ?? data.leases ?? data.entries ?? data.months) as unknown[] | undefined;
		return Array.isArray(rows) && rows.length === 0;
	});

	// --- CSV export (client-side, from the loaded rows) ----------------------------------------------
	function csvCell(v: unknown): string {
		const s = v == null ? '' : String(v);
		return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
	}
	function toCsv(headers: string[], rows: (string | number)[][]): string {
		return [headers, ...rows].map((r) => r.map(csvCell).join(',')).join('\n');
	}
	function downloadCsv(filename: string, csv: string) {
		const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
		const url = URL.createObjectURL(blob);
		const a = document.createElement('a');
		a.href = url;
		a.download = filename;
		document.body.appendChild(a);
		a.click();
		document.body.removeChild(a);
		URL.revokeObjectURL(url);
	}

	function exportCsv() {
		const data = reportQuery.data;
		if (!data) return;
		let headers: string[] = [];
		let rows: (string | number)[][] = [];

		if (rentRoll) {
			headers = ['Property', 'Unit', 'Tenant', 'Monthly Rent', 'Deposit', 'Start', 'End', 'Status'];
			rows = rentRoll.rows.map((r) => [r.propertyName, r.unitNumber, r.tenantName, r.monthlyRent, r.securityDeposit, formatDateOnly(r.startOn), r.endOn ? formatDateOnly(r.endOn) : '', formatStatusLabel(r.statusName)]);
		} else if (rentLedger) {
			headers = ['Property', 'Unit', 'Tenant', 'Date', 'Type', 'Description', 'Charge', 'Payment', 'Balance'];
			rows = rentLedger.leases.flatMap((l) =>
				l.entries.map((e) => [l.propertyName, l.unitNumber, l.tenantName, formatDateOnly(e.date), formatMoneyEntryLabel(e.type), e.description, e.charge, e.credit, e.balance])
			);
		} else if (delinquency) {
			headers = ['Property', 'Unit', 'Tenant', '0-30', '31-60', '61-90', '90+', 'Total', 'Oldest (days)'];
			rows = delinquency.rows.map((r) => [r.propertyName, r.unitNumber, r.tenantName, r.buckets.current, r.buckets.days31To60, r.buckets.days61To90, r.buckets.over90, r.total, r.oldestOverdueDays]);
		} else if (cashFlow) {
			headers = ['Month', 'Income', 'Expense', 'Net'];
			rows = cashFlow.months.map((m) => [m.label, m.income, m.expense, m.net]);
		} else if (generalLedger) {
			headers = ['Date', 'Type', 'Description', 'Category', 'Property', 'Counterparty', 'Amount', 'Running Balance'];
			rows = generalLedger.entries.map((e) => [formatDateOnly(e.date), formatMoneyEntryLabel(e.type), e.description, formatMoneyCategoryLabel(e.category), e.propertyName ?? '', e.counterparty ?? '', e.amount, e.runningBalance]);
		} else if (propertyPnl) {
			headers = ['Property', 'Income', 'Expense', 'Net'];
			rows = propertyPnl.rows.map((r) => [r.propertyName, r.income, r.expense, r.net]);
		} else if (occupancy) {
			headers = ['Property', 'Total Units', 'Occupied', 'Vacant', 'Occupancy %'];
			rows = occupancy.rows.map((r) => [r.propertyName, r.totalUnits, r.occupiedUnits, r.vacantUnits, r.occupancyPercent]);
		} else if (leaseExp) {
			headers = ['Property', 'Unit', 'Tenant', 'Monthly Rent', 'End Date', 'Days Until', 'Status'];
			rows = leaseExp.rows.map((r) => [r.propertyName, r.unitNumber, r.tenantName, r.monthlyRent, formatDateOnly(r.endOn), r.daysUntilExpiry, formatStatusLabel(r.statusName)]);
		} else if (deposits) {
			headers = ['Property', 'Unit', 'Tenant', 'Held', 'Deductions', 'Returned', 'Balance', 'Status', 'Held At'];
			rows = deposits.rows.map((r) => [r.propertyName, r.unitNumber, r.tenantName, r.held, r.deductions, r.returned, r.currentBalance, formatStatusLabel(r.statusName), formatDateOnly(r.heldAt)]);
		} else if (vendor1099) {
			headers = ['Vendor', 'Tax ID', 'Total Paid', '1099 Eligible', 'W-9 On File', 'Needs W-9', 'Needs 1099 Review'];
			rows = vendor1099.rows.map((r) => [r.vendorName, r.taxId ?? '', r.totalPaid, r.is1099Eligible ? 'Yes' : 'No', r.w9OnFile ? 'Yes' : 'No', r.needsW9 ? 'Yes' : 'No', r.needs1099Review ? 'Yes' : 'No']);
		} else if (ownerDist) {
			headers = ['Owner', 'Net to Owner', 'Distributed', 'Undistributed'];
			rows = ownerDist.rows.map((r) => [r.ownerName, r.netToOwner, r.totalDistributed, r.undistributed]);
		} else if (workOrders) {
			headers = ['Property', 'Unit', 'Title', 'Category', 'Priority', 'Status', 'Vendor', 'Requested', 'Completed', 'Actual Cost'];
			rows = workOrders.rows.map((r) => [r.propertyName, r.unitNumber ?? '', r.title, formatMoneyCategoryLabel(r.category), formatStatusLabel(r.priorityName), formatStatusLabel(r.statusName), r.vendorName ?? '', formatDate(r.requestedAt), r.completedAt ? formatDate(r.completedAt) : '', r.actualCost ?? '']);
		}
		downloadCsv(`${reportKey}-${new Date().toISOString().slice(0, 10)}.csv`, toCsv(headers, rows));
	}

	function printReport() {
		window.print();
	}
</script>

<svelte:head>
	<title>{reportTitle(reportKey, entry?.title ?? 'Report')} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20 print:overflow-visible print:p-0" data-testid="reports-viewer">
	<!-- Header -->
	<div class="mb-4 flex flex-wrap items-start justify-between gap-3 print:mb-2">
		<div class="min-w-0">
			<a href="/reports" class="mb-1 inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground print:hidden">
				<ArrowLeft class="h-3.5 w-3.5" /> All reports
			</a>
			<h1 class="text-2xl font-bold">{reportTitle(reportKey, entry?.title ?? reportKey)}</h1>
			{#if periodLabel}
				<p class="text-sm text-muted-foreground" data-testid="report-period">{periodLabel}</p>
			{/if}
			<a
				href="/docs/reports"
				class="mt-1 inline-flex min-h-11 items-center text-sm font-medium text-primary underline-offset-4 hover:underline print:hidden"
				data-testid="report-help-link"
			>
				How reports work
			</a>
		</div>

		{#if entry && !entry.external}
			<DropdownMenu>
				<DropdownMenuTrigger class="print:hidden">
					{#snippet child({ props })}
						<Button {...props} variant="outline" size="sm" data-testid="report-actions">
							<MoreHorizontal class="h-4 w-4" /> Actions
						</Button>
					{/snippet}
				</DropdownMenuTrigger>
				<DropdownMenuContent align="end">
					<DropdownMenuItem onSelect={exportCsv} data-testid="report-export-csv">
						<Download class="h-4 w-4" /> Export CSV
					</DropdownMenuItem>
					<DropdownMenuItem onSelect={printReport} data-testid="report-print">
						<Printer class="h-4 w-4" /> Print
					</DropdownMenuItem>
				</DropdownMenuContent>
			</DropdownMenu>
		{/if}
	</div>

	{#if catalogQuery.isLoading}
		<div class="h-40 animate-pulse rounded-lg border bg-muted/40"></div>
	{:else if !entry}
		<Card.Root class="border-destructive/30 bg-destructive/5">
			<Card.Content class="flex items-center justify-between gap-4 p-4">
				<div class="flex items-center gap-3">
					<AlertTriangle class="h-5 w-5 shrink-0 text-destructive" />
					<p class="text-sm text-destructive">Report "{reportKey}" was not found.</p>
				</div>
				<Button variant="outline" size="sm" onclick={() => goto('/reports')}>Back to reports</Button>
			</Card.Content>
		</Card.Root>
	{:else if entry.external}
		<!-- External report: deep-link, not rendered as data here. -->
		<Card.Root>
			<Card.Content class="flex flex-col items-start gap-3 p-5">
				<div class="flex items-center gap-2 text-sm text-muted-foreground">
					<FileText class="h-4 w-4" /> {reportDescription(reportKey, entry.description ?? '')}
				</div>
				<Button onclick={() => goto(externalLinks[entry.key] ?? '/reports')} data-testid="report-open-external">
					<ExternalLink class="h-4 w-4" /> Open {reportTitle(reportKey, entry.title ?? reportKey)}
				</Button>
			</Card.Content>
		</Card.Root>
	{:else}
		<!-- Parameter bar -->
		<Card.Root class="mb-4 print:hidden">
			<Card.Content class="flex flex-wrap items-end gap-3 p-4">
				{#if accepts.has('from') || accepts.has('to')}
					<div class="min-w-[16rem]">
						<label class="mb-1 block text-xs font-medium text-muted-foreground" for="report-range">Date range</label>
						<RangeDatePicker id="report-range" bind:start={rangeStart} bind:end={rangeEnd} presets testid="report-date-range" />
					</div>
				{/if}

				{#if accepts.has('propertyIds') || accepts.has('propertyId')}
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Properties</span>
						<Popover.Root bind:open={propPopoverOpen}>
							<Popover.Trigger>
								{#snippet child({ props })}
									<Button {...props} variant="outline" class="h-10 min-w-[12rem] justify-start gap-2 font-normal" data-testid="report-property-select">
										<Building2 class="h-4 w-4 opacity-70" />
										{propertyLabel}
									</Button>
								{/snippet}
							</Popover.Trigger>
							<Popover.Content class="w-64 p-2" align="start">
								<button
									type="button"
									class="mb-1 w-full rounded px-2 py-1.5 text-left text-sm hover:bg-accent"
									onclick={() => (selectedPropertyIds = [])}
									data-testid="report-property-all"
								>
									All properties
								</button>
								<div class="max-h-64 space-y-0.5 overflow-y-auto">
									{#each propertyList as p (p.id)}
										<label class="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm hover:bg-accent">
											<Checkbox
												checked={selectedPropertyIds.includes(p.id)}
												onCheckedChange={(v) => toggleProperty(p.id, v === true)}
											/>
											<span class="truncate">{p.name}</span>
										</label>
									{/each}
									{#if propertyList.length === 0}
										<p class="px-2 py-1.5 text-sm text-muted-foreground">No properties.</p>
									{/if}
								</div>
								{#if accepts.has('propertyId') && !accepts.has('propertyIds')}
									<p class="mt-1 px-2 text-[11px] text-muted-foreground">This report uses the first selected property.</p>
								{/if}
							</Popover.Content>
						</Popover.Root>
					</div>
				{/if}

				{#if accepts.has('year')}
					<div>
						<label class="mb-1 block text-xs font-medium text-muted-foreground" for="report-year">Year</label>
						<Input id="report-year" type="text" inputmode="numeric" maxlength={4} mask="integer" bind:value={year} class="h-10 w-28" data-testid="report-year-input" />
					</div>
				{/if}

				{#if accepts.has('days')}
					<div>
						<label class="mb-1 block text-xs font-medium text-muted-foreground" for="report-days">Window (days)</label>
						<Input id="report-days" type="text" inputmode="numeric" mask="integer" bind:value={days} class="h-10 w-28" data-testid="report-days-input" />
					</div>
				{/if}

				<Button onclick={generate} data-testid="report-param-generate" disabled={reportQuery.isFetching}>
					{#if reportQuery.isFetching}
						<RefreshCw class="h-4 w-4 animate-spin" /> Generating…
					{:else}
						<RefreshCw class="h-4 w-4" /> {hasGenerated ? 'Update' : 'Generate'}
					{/if}
				</Button>
			</Card.Content>
		</Card.Root>

		<!-- Result -->
		{#if reportQuery.isLoading || (reportQuery.isFetching && !reportQuery.data)}
			<div class="h-60 animate-pulse rounded-lg border bg-muted/40"></div>
		{:else if reportQuery.isError}
			<Card.Root class="border-destructive/30 bg-destructive/5">
				<Card.Content class="flex items-center justify-between gap-4 p-4">
					<div class="flex items-center gap-3">
						<AlertTriangle class="h-5 w-5 shrink-0 text-destructive" />
						<p class="text-sm text-destructive">{apiErrorMessage(reportQuery.error, 'Could not generate this report.')}</p>
					</div>
					<Button variant="outline" size="sm" onclick={() => reportQuery.refetch()}>Retry</Button>
				</Card.Content>
			</Card.Root>
		{:else if isEmpty}
			<Card.Root>
				<Card.Content class="flex flex-col items-center gap-2 p-10 text-center">
					<FileText class="h-8 w-8 text-muted-foreground/50" />
					<p class="font-medium">Nothing to show for this period</p>
					<p class="text-sm text-muted-foreground">Try widening the date range or clearing the property filter.</p>
				</Card.Content>
			</Card.Root>
		{:else if reportQuery.data}
			<Card.Root class="overflow-hidden print:border-0 print:shadow-none">
				<div class="overflow-x-auto">
					<table class="w-full text-sm" data-testid="report-table">
						<!-- ── Rent Roll ────────────────────────────────────────────── -->
						{#if rentRoll}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Property / Unit</th>
									<th class="px-3 py-2 font-medium">Tenant</th>
									<th class="px-3 py-2 text-right font-medium">Monthly Rent</th>
									<th class="px-3 py-2 text-right font-medium">Deposit</th>
									<th class="px-3 py-2 font-medium">Term</th>
									<th class="px-3 py-2 font-medium">Status</th>
								</tr>
							</thead>
							<tbody>
								{#each rentRoll.rows as r (r.agreementId)}
									<tr class="border-b last:border-0 hover:bg-muted/30">
										<td class="px-3 py-2"><div class="font-medium">{r.propertyName}</div><div class="text-xs text-muted-foreground">Unit {r.unitNumber}</div></td>
										<td class="px-3 py-2">{r.tenantName}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{money(r.monthlyRent)}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums">{money(r.securityDeposit)}</td>
										<td class="px-3 py-2 text-xs text-muted-foreground">{formatDateOnly(r.startOn)} – {r.endOn ? formatDateOnly(r.endOn) : 'Month to month'}</td>
										<td class="px-3 py-2">{formatStatusLabel(r.statusName)}</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2" colspan="2">{rentRoll.leaseCount} lease{rentRoll.leaseCount === 1 ? '' : 's'}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{money(rentRoll.totalMonthlyRent)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums">{money(rentRoll.totalSecurityDeposit)}</td>
									<td colspan="2"></td>
								</tr>
							</tfoot>

						<!-- ── Rent Ledger (grouped per lease) ───────────────────────── -->
						{:else if rentLedger}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Date</th>
									<th class="px-3 py-2 font-medium">Type</th>
									<th class="px-3 py-2 font-medium">Description</th>
									<th class="px-3 py-2 text-right font-medium">Charge</th>
									<th class="px-3 py-2 text-right font-medium">Payment</th>
									<th class="px-3 py-2 text-right font-medium">Balance</th>
								</tr>
							</thead>
							<tbody>
								{#each rentLedger.leases as l (l.leaseManagementId)}
									<tr class="border-b bg-muted/20">
										<td class="px-3 py-2 font-semibold" colspan="6">{l.propertyName} · Unit {l.unitNumber} · {l.tenantName}</td>
									</tr>
									{#each l.entries as e, ei (ei)}
										<tr class="border-b last:border-0 hover:bg-muted/30">
											<td class="px-3 py-2">{formatDateOnly(e.date)}</td>
											<td class="px-3 py-2">{formatMoneyEntryLabel(e.type)}</td>
											<td class="px-3 py-2 text-muted-foreground">{e.description}</td>
											<td class="px-3 py-2 text-right font-mono tabular-nums">{e.charge ? money(e.charge) : '—'}</td>
											<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{e.credit ? money(e.credit) : '—'}</td>
											<td class="px-3 py-2 text-right font-mono tabular-nums {e.balance > 0 ? 'text-destructive' : ''}">{money(e.balance)}</td>
										</tr>
									{/each}
									<tr class="border-b bg-muted/10 font-medium">
										<td class="px-3 py-1.5" colspan="3">Subtotal</td>
										<td class="px-3 py-1.5 text-right font-mono tabular-nums">{money(l.totalCharged)}</td>
										<td class="px-3 py-1.5 text-right font-mono tabular-nums text-success">{money(l.totalCredits)}</td>
										<td class="px-3 py-1.5 text-right font-mono tabular-nums {l.balance > 0 ? 'text-destructive' : ''}">{money(l.balance)}</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2" colspan="3">Portfolio total</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums">{money(rentLedger.totalCharged)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{money(rentLedger.totalCredits)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums {rentLedger.totalBalance > 0 ? 'text-destructive' : ''}">{money(rentLedger.totalBalance)}</td>
								</tr>
							</tfoot>

						<!-- ── Delinquency / Aging ───────────────────────────────────── -->
						{:else if delinquency}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Property / Unit</th>
									<th class="px-3 py-2 font-medium">Tenant</th>
									<th class="px-3 py-2 text-right font-medium">0-30</th>
									<th class="px-3 py-2 text-right font-medium">31-60</th>
									<th class="px-3 py-2 text-right font-medium">61-90</th>
									<th class="px-3 py-2 text-right font-medium">90+</th>
									<th class="px-3 py-2 text-right font-medium">Total</th>
								</tr>
							</thead>
							<tbody>
								{#each delinquency.rows as r (r.leaseManagementId)}
									<tr class="border-b last:border-0 hover:bg-muted/30">
										<td class="px-3 py-2"><div class="font-medium">{r.propertyName}</div><div class="text-xs text-muted-foreground">Unit {r.unitNumber}</div></td>
										<td class="px-3 py-2">{r.tenantName}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums">{r.buckets.current ? money(r.buckets.current) : '—'}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums text-[var(--warning)]">{r.buckets.days31To60 ? money(r.buckets.days31To60) : '—'}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums text-[var(--warning)]">{r.buckets.days61To90 ? money(r.buckets.days61To90) : '—'}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums text-destructive">{r.buckets.over90 ? money(r.buckets.over90) : '—'}</td>
										<td class="px-3 py-2 text-right font-mono font-semibold tabular-nums {r.total > 0 ? 'text-destructive' : ''}">{money(r.total)}</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2" colspan="2">Totals</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums">{money(delinquency.totals.current)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-[var(--warning)]">{money(delinquency.totals.days31To60)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-[var(--warning)]">{money(delinquency.totals.days61To90)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-destructive">{money(delinquency.totals.over90)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-destructive">{money(delinquency.totalOutstanding)}</td>
								</tr>
							</tfoot>

						<!-- ── Cash Flow / Income-Expense Statement ──────────────────── -->
						{:else if cashFlow}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Month</th>
									<th class="px-3 py-2 text-right font-medium">Income</th>
									<th class="px-3 py-2 text-right font-medium">Expense</th>
									<th class="px-3 py-2 text-right font-medium">Net</th>
								</tr>
							</thead>
							<tbody>
								{#each cashFlow.months as m (m.monthKey)}
									<tr class="border-b last:border-0 hover:bg-muted/30">
										<td class="px-3 py-2 font-medium">{m.label}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{money(m.income)}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums text-[var(--warning)]">{money(m.expense)}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums {netClass(m.net)}">{money(m.net)}</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2">Total</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{money(cashFlow.totalIncome)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-[var(--warning)]">{money(cashFlow.totalExpense)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums {netClass(cashFlow.totalNet)}">{money(cashFlow.totalNet)}</td>
								</tr>
							</tfoot>

						<!-- ── General Ledger ────────────────────────────────────────── -->
						{:else if generalLedger}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Date</th>
									<th class="px-3 py-2 font-medium">Type</th>
									<th class="px-3 py-2 font-medium">Description</th>
									<th class="px-3 py-2 font-medium">Category</th>
									<th class="px-3 py-2 font-medium">Property</th>
									<th class="px-3 py-2 text-right font-medium">Amount</th>
									<th class="px-3 py-2 text-right font-medium">Balance</th>
								</tr>
							</thead>
							<tbody>
								{#each generalLedger.entries as e (e.type + '-' + e.id)}
									<tr class="border-b last:border-0 hover:bg-muted/30">
										<td class="px-3 py-2">{formatDateOnly(e.date)}</td>
										<td class="px-3 py-2">{formatMoneyEntryLabel(e.type)}</td>
										<td class="px-3 py-2">{e.description}{#if e.counterparty}<span class="text-muted-foreground"> · {e.counterparty}</span>{/if}</td>
										<td class="px-3 py-2 text-muted-foreground">{formatMoneyCategoryLabel(e.category)}</td>
										<td class="px-3 py-2 text-muted-foreground">{e.propertyName ?? '—'}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums {netClass(e.amount)}">{money(e.amount)}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums {netClass(e.runningBalance)}">{money(e.runningBalance)}</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2" colspan="5">Income {money(generalLedger.totalIncome)} · Expense {money(generalLedger.totalExpense)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums">Net</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums {netClass(generalLedger.closingBalance)}">{money(generalLedger.closingBalance)}</td>
								</tr>
							</tfoot>

						<!-- ── Property P&L Summary ──────────────────────────────────── -->
						{:else if propertyPnl}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Property</th>
									<th class="px-3 py-2 text-right font-medium">Income</th>
									<th class="px-3 py-2 text-right font-medium">Expense</th>
									<th class="px-3 py-2 text-right font-medium">Net</th>
								</tr>
							</thead>
							<tbody>
								{#each propertyPnl.rows as r (r.propertyId)}
									<tr class="border-b last:border-0 hover:bg-muted/30">
										<td class="px-3 py-2 font-medium">{r.propertyName}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{money(r.income)}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums text-[var(--warning)]">{money(r.expense)}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums {netClass(r.net)}">{money(r.net)}</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2">Total</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{money(propertyPnl.totalIncome)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-[var(--warning)]">{money(propertyPnl.totalExpense)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums {netClass(propertyPnl.totalNet)}">{money(propertyPnl.totalNet)}</td>
								</tr>
							</tfoot>

						<!-- ── Occupancy / Vacancy ───────────────────────────────────── -->
						{:else if occupancy}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Property</th>
									<th class="px-3 py-2 text-right font-medium">Units</th>
									<th class="px-3 py-2 text-right font-medium">Occupied</th>
									<th class="px-3 py-2 text-right font-medium">Vacant</th>
									<th class="px-3 py-2 text-right font-medium">Occupancy</th>
								</tr>
							</thead>
							<tbody>
								{#each occupancy.rows as r (r.propertyId)}
									<tr class="border-b last:border-0 hover:bg-muted/30">
										<td class="px-3 py-2 font-medium">{r.propertyName}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums">{r.totalUnits}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{r.occupiedUnits}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums {r.vacantUnits > 0 ? 'text-[var(--warning)]' : ''}">{r.vacantUnits}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums">{pct(r.occupancyPercent)}</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2">Portfolio</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums">{occupancy.totalUnits}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{occupancy.occupiedUnits}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums {occupancy.vacantUnits > 0 ? 'text-[var(--warning)]' : ''}">{occupancy.vacantUnits}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums">{pct(occupancy.occupancyPercent)}</td>
								</tr>
							</tfoot>

						<!-- ── Lease Expirations ─────────────────────────────────────── -->
						{:else if leaseExp}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Property / Unit</th>
									<th class="px-3 py-2 font-medium">Tenant</th>
									<th class="px-3 py-2 text-right font-medium">Monthly Rent</th>
									<th class="px-3 py-2 font-medium">End Date</th>
									<th class="px-3 py-2 text-right font-medium">Days Until</th>
									<th class="px-3 py-2 font-medium">Status</th>
								</tr>
							</thead>
							<tbody>
								{#each leaseExp.rows as r (r.agreementId)}
									<tr class="border-b last:border-0 hover:bg-muted/30">
										<td class="px-3 py-2"><div class="font-medium">{r.propertyName}</div><div class="text-xs text-muted-foreground">Unit {r.unitNumber}</div></td>
										<td class="px-3 py-2">{r.tenantName}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{money(r.monthlyRent)}</td>
										<td class="px-3 py-2">{formatDateOnly(r.endOn)}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums {r.daysUntilExpiry < 0 ? 'text-destructive' : r.daysUntilExpiry <= 30 ? 'text-[var(--warning)]' : ''}">{r.daysUntilExpiry}</td>
										<td class="px-3 py-2">{formatStatusLabel(r.statusName)}</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2" colspan="2">{leaseExp.leaseCount} lease{leaseExp.leaseCount === 1 ? '' : 's'} expiring</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{money(leaseExp.totalMonthlyRent)}</td>
									<td colspan="3"></td>
								</tr>
							</tfoot>

						<!-- ── Security Deposit Register ──────────────────────────────── -->
						{:else if deposits}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Property / Unit</th>
									<th class="px-3 py-2 font-medium">Tenant</th>
									<th class="px-3 py-2 text-right font-medium">Held</th>
									<th class="px-3 py-2 text-right font-medium">Deductions</th>
									<th class="px-3 py-2 text-right font-medium">Returned</th>
									<th class="px-3 py-2 text-right font-medium">Balance</th>
									<th class="px-3 py-2 font-medium">Status</th>
								</tr>
							</thead>
							<tbody>
								{#each deposits.rows as r (r.depositId)}
									<tr class="border-b last:border-0 hover:bg-muted/30">
										<td class="px-3 py-2"><div class="font-medium">{r.propertyName}</div><div class="text-xs text-muted-foreground">Unit {r.unitNumber}</div></td>
										<td class="px-3 py-2">{r.tenantName}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums">{money(r.held)}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums {r.deductions > 0 ? 'text-[var(--warning)]' : ''}">{r.deductions ? money(r.deductions) : '—'}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums">{r.returned ? money(r.returned) : '—'}</td>
										<td class="px-3 py-2 text-right font-mono font-semibold tabular-nums text-success">{money(r.currentBalance)}</td>
										<td class="px-3 py-2">{formatStatusLabel(r.statusName)}</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2" colspan="2">Totals</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums">{money(deposits.totalHeld)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-[var(--warning)]">{money(deposits.totalDeductions)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums">{money(deposits.totalReturned)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums text-success">{money(deposits.totalCurrentBalance)}</td>
									<td></td>
								</tr>
								<tr class="border-t bg-background font-normal print:hidden">
									<td class="px-3 py-2 text-xs text-muted-foreground" colspan="4">
										Showing {deposits.totalCount === 0 ? 0 : deposits.skip + 1}-{Math.min(deposits.skip + deposits.rows.length, deposits.totalCount)} of {deposits.totalCount}
									</td>
									<td class="px-3 py-2 text-right" colspan="3">
										<Button variant="outline" size="sm" onclick={() => setReportPage(deposits.skip - deposits.take)} disabled={deposits.skip <= 0 || reportQuery.isFetching}>
											Previous
										</Button>
										<Button class="ml-2" variant="outline" size="sm" onclick={() => setReportPage(deposits.skip + deposits.take)} disabled={deposits.skip + deposits.take >= deposits.totalCount || reportQuery.isFetching}>
											Next
										</Button>
									</td>
								</tr>
							</tfoot>

						<!-- ── Vendor 1099 ───────────────────────────────────────────── -->
						{:else if vendor1099}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Vendor</th>
									<th class="px-3 py-2 font-medium">Tax ID</th>
									<th class="px-3 py-2 text-right font-medium">Total Paid</th>
									<th class="px-3 py-2 font-medium">W-9</th>
									<th class="px-3 py-2 font-medium">Flags</th>
								</tr>
							</thead>
							<tbody>
								{#each vendor1099.rows as r (r.vendorId)}
									<tr class="border-b last:border-0 hover:bg-muted/30">
										<td class="px-3 py-2 font-medium">{r.vendorName}</td>
										<td class="px-3 py-2 font-mono text-xs text-muted-foreground">{r.taxId ?? '—'}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums">{money(r.totalPaid)}</td>
										<td class="px-3 py-2">{r.w9OnFile ? 'On file' : '—'}</td>
										<td class="px-3 py-2">
											{#if r.needsW9}<span class="mr-1 inline-flex rounded-full border border-destructive/40 bg-destructive/10 px-2 py-0.5 text-xs text-destructive">Needs W-9</span>{/if}
											{#if r.needs1099Review}<span class="inline-flex rounded-full border border-[var(--warning)]/40 bg-[var(--warning)]/10 px-2 py-0.5 text-xs text-[var(--warning)]">1099 review</span>{/if}
										</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2" colspan="2">Total paid (threshold {money(vendor1099.threshold)})</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums">{money(vendor1099.totalPaid)}</td>
									<td colspan="2"></td>
								</tr>
							</tfoot>

						<!-- ── Owner Distributions ───────────────────────────────────── -->
						{:else if ownerDist}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Owner</th>
									<th class="px-3 py-2 text-right font-medium">Net to owner</th>
									<th class="px-3 py-2 text-right font-medium">Distributed</th>
									<th class="px-3 py-2 text-right font-medium">Undistributed</th>
								</tr>
							</thead>
							<tbody>
								{#each ownerDist.rows as r (r.ownerId)}
									<tr class="border-b last:border-0 hover:bg-muted/30">
										<td class="px-3 py-2 font-medium">{r.ownerName}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums {netClass(r.netToOwner)}">{money(r.netToOwner)}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums">{money(r.totalDistributed)}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums {netClass(r.undistributed)}">{money(r.undistributed)}</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2">Total to owners</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums {netClass(ownerDist.totalNetToOwners)}">{money(ownerDist.totalNetToOwners)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums">{money(ownerDist.totalDistributed)}</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums {netClass(ownerDist.totalUndistributed)}">{money(ownerDist.totalUndistributed)}</td>
								</tr>
							</tfoot>

						<!-- ── Work Orders / Maintenance ─────────────────────────────── -->
						{:else if workOrders}
							<thead class="border-b bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
								<tr>
									<th class="px-3 py-2 font-medium">Property / Unit</th>
									<th class="px-3 py-2 font-medium">Title</th>
									<th class="px-3 py-2 font-medium">Priority</th>
									<th class="px-3 py-2 font-medium">Status</th>
									<th class="px-3 py-2 font-medium">Vendor</th>
									<th class="px-3 py-2 font-medium">Requested</th>
									<th class="px-3 py-2 text-right font-medium">Cost</th>
								</tr>
							</thead>
							<tbody>
								{#each workOrders.rows as r (r.workOrderId)}
									<tr class="border-b last:border-0 hover:bg-muted/30">
										<td class="px-3 py-2"><div class="font-medium">{r.propertyName}</div>{#if r.unitNumber}<div class="text-xs text-muted-foreground">Unit {r.unitNumber}</div>{/if}</td>
										<td class="px-3 py-2">{r.title}<div class="text-xs text-muted-foreground">{formatMoneyCategoryLabel(r.category)}</div></td>
										<td class="px-3 py-2">{formatStatusLabel(r.priorityName)}</td>
										<td class="px-3 py-2">{formatStatusLabel(r.statusName)}</td>
										<td class="px-3 py-2 text-muted-foreground">{r.vendorName ?? '—'}</td>
										<td class="px-3 py-2">{formatDate(r.requestedAt)}</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums">{r.actualCost != null ? money(r.actualCost) : '—'}</td>
									</tr>
								{/each}
							</tbody>
							<tfoot class="border-t-2 bg-muted/40 font-semibold">
								<tr>
									<td class="px-3 py-2" colspan="6">{workOrders.totalCount} total · {workOrders.openCount} open · {workOrders.completedCount} completed</td>
									<td class="px-3 py-2 text-right font-mono tabular-nums">{money(workOrders.totalActualCost)}</td>
								</tr>
							</tfoot>
						{/if}
					</table>
				</div>
			</Card.Root>
		{/if}
	{/if}
</div>

<!--
  Print: hide the app chrome (sidebar/header/assistant bubble) so the report prints on its own.
  We target the AppShell's structural elements via :global without modifying its source.
-->
<style>
	@media print {
		:global(aside),
		:global(header),
		:global([data-testid='report-actions']) {
			display: none !important;
		}
		:global(main) {
			overflow: visible !important;
		}
		:global(body) {
			background: #fff;
		}
	}
</style>
