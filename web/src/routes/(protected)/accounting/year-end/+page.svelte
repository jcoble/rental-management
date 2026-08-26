<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { accounting, downloadYearEndPacket } from '$lib/api/endpoints/accounting';
	import { showError, apiErrorMessage } from '$lib/utils/toast';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import { Download, Wallet, Receipt, Home, AlertCircle, Info } from '@lucide/svelte';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';

	// Default to the current operating year; completed prior-year packets stay one select away.
	const currentYear = new Date().getFullYear();
	let year = $state(currentYear);
	const yearOptions = Array.from({ length: 6 }, (_, i) => currentYear - i);

	let selectedPropertyId = $state<number | 'all'>('all');

	const query = createQuery(() => ({
		queryKey: ['year-end', year, selectedPropertyId],
		queryFn: () => accounting.yearEnd(year, selectedPropertyId === 'all' ? undefined : selectedPropertyId)
	}));
	const view = $derived(query.data);

	let propertyOptions = $state<{ id: number; name: string }[]>([]);
	$effect(() => {
		if (selectedPropertyId !== 'all' || !view) return;
		const map = new Map<number, string>();
		for (const p of view.cashFlow.properties) map.set(p.propertyId, p.propertyName);
		for (const p of view.scheduleE.properties) map.set(p.propertyId, p.propertyName);
		for (const p of view.propertyDispositions) {
			if (p.propertyName) map.set(p.propertyId, p.propertyName);
		}
		propertyOptions = [...map.entries()]
			.map(([id, name]) => ({ id, name }))
			.sort((a, b) => a.name.localeCompare(b.name));
	});

	const cashRows = $derived(view?.cashFlow.properties ?? []);
	const taxRows = $derived(view?.scheduleE.properties ?? []);
	const dispositionRows = $derived(view?.propertyDispositions ?? []);
	const rentRoll = $derived(view?.rentRoll ?? []);

	const totalCashFlow = $derived(view?.cashFlow.totalCashFlow ?? 0);
	const totalTaxable = $derived(view?.scheduleE.netIncome ?? 0);

	let downloading = $state(false);
	async function exportPacket() {
		downloading = true;
		try {
			await downloadYearEndPacket(year);
		} catch (err) {
			showError(apiErrorMessage(err));
		} finally {
			downloading = false;
		}
	}
</script>

<svelte:head><title>Year-end — Rental Command</title></svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="year-end-page">
	<div class="mb-4">
		<PageBreadcrumb crumbs={[{ label: 'Accounting', href: '/accounting' }, { label: 'Year-end' }]} />
	</div>

	<!-- Header + controls -->
	<div class="mb-6 flex flex-wrap items-end justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Year-end</h1>
			<p class="mt-1 text-sm text-muted-foreground">Compare the cash you kept with the rental income your accountant may report for taxes.</p>
			<a
				href="/docs/taxes-and-1099"
				class="mt-2 inline-flex min-h-11 items-center text-sm font-medium text-primary underline-offset-4 hover:underline"
				data-testid="year-end-help-link"
			>
				How year-end reporting works
			</a>
		</div>
		<div class="flex flex-wrap items-end gap-2">
			<div class="flex flex-col gap-1 text-xs font-medium text-muted-foreground">
				<span>Tax year</span>
				<Select.Root
					type="single"
					value={String(year)}
					onValueChange={(value) => {
						year = Number(value);
						selectedPropertyId = 'all';
					}}
				>
					<Select.Trigger class="h-10 w-28 text-sm text-foreground" data-testid="year-end-year-select">
						{year}
					</Select.Trigger>
					<Select.Content>
						{#each yearOptions as y}
							<Select.Item value={String(y)} label={String(y)}>{y}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<div class="flex flex-col gap-1 text-xs font-medium text-muted-foreground">
				<span>Property</span>
				<Select.Root
					type="single"
					value={String(selectedPropertyId)}
					onValueChange={(value) => (selectedPropertyId = value === 'all' ? 'all' : Number(value))}
				>
					<Select.Trigger class="h-10 min-w-48 text-sm text-foreground" data-testid="year-end-property-select">
						{selectedPropertyId === 'all'
							? 'All properties'
							: propertyOptions.find((property) => property.id === selectedPropertyId)?.name ?? 'Select property'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="all" label="All properties">All properties</Select.Item>
						{#each propertyOptions as property}
							<Select.Item value={String(property.id)} label={property.name}>{property.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<Button variant="outline" class="gap-2" onclick={exportPacket} disabled={downloading} data-testid="year-end-export">
				<Download class="h-4 w-4" />
				{downloading ? 'Preparing…' : 'Export packet'}
			</Button>
		</div>
	</div>

	{#if query.isLoading}
		<div class="flex items-center justify-center py-20 text-muted-foreground" data-testid="year-end-loading">
			<div class="h-6 w-6 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
		</div>
	{:else if query.isError}
		<div class="rounded-lg border border-destructive/30 bg-destructive/10 p-6 text-center" data-testid="year-end-error">
			<AlertCircle class="mx-auto mb-2 h-8 w-8 text-destructive" />
			<p class="font-medium text-destructive">Could not load the year-end view</p>
			<Button variant="outline" class="mt-4" onclick={() => query.refetch()}>Retry</Button>
		</div>
	{:else if view}
		{#if view.scheduleE.unallocatedActivity.requiresAllocation}
			<div class="mb-6 rounded-lg border border-amber-300 bg-amber-50 p-4 text-sm text-amber-950 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-100" data-testid="year-end-unallocated-warning">
				<p class="font-semibold">Tax activity needs a property before filing</p>
				<p class="mt-1">{view.scheduleE.unallocatedActivity.incomeEntryCount} income entries ({formatAccountingCurrency(view.scheduleE.unallocatedActivity.rentalIncome)}) and {view.scheduleE.unallocatedActivity.expenseCount} expenses ({formatAccountingCurrency(view.scheduleE.unallocatedActivity.totalExpenses)}) are excluded from the per-property Schedule E lines until allocated.</p>
				<p class="mt-2 font-medium">Reconciled tax activity: {formatAccountingCurrency(view.scheduleE.reconciledTotalRentalIncome)} income − {formatAccountingCurrency(view.scheduleE.reconciledTotalExpenses)} expenses = {formatAccountingCurrency(view.scheduleE.reconciledNetIncome)} net.</p>
			</div>
		{/if}
		<!-- The two headline numbers, side by side -->
		<div class="mb-6 grid gap-4 sm:grid-cols-2" data-testid="year-end-headline">
			<div class="rounded-xl border border-border bg-card p-5" data-testid="year-end-cash-flow-headline">
				<div class="flex items-center gap-2 text-sm font-medium text-muted-foreground"><Wallet class="h-4 w-4" /> Cash flow (what hit your pocket)</div>
				<p class="mt-2 font-mono text-3xl font-bold tabular-nums {totalCashFlow < 0 ? 'text-destructive' : 'text-foreground'}" data-testid="year-end-cash-flow-total">{formatAccountingCurrency(totalCashFlow)}</p>
				<p class="mt-1 text-xs text-muted-foreground">Rent in − operating expenses − debt service. Excludes non-cash depreciation.</p>
			</div>
			<div class="rounded-xl border border-border bg-card p-5" data-testid="year-end-taxable-headline">
				<div class="flex items-center gap-2 text-sm font-medium text-muted-foreground"><Receipt class="h-4 w-4" /> Taxable income (Schedule E)</div>
				<p class="mt-2 font-mono text-3xl font-bold tabular-nums {totalTaxable < 0 ? 'text-destructive' : 'text-foreground'}" data-testid="year-end-taxable-total">{formatAccountingCurrency(totalTaxable)}</p>
				<p class="mt-1 text-xs text-muted-foreground">Rent − deductible expenses − mortgage interest − depreciation. Principal is not deductible.</p>
			</div>
		</div>

		<!-- Block 1: Cash flow per property -->
		<section class="mb-6" data-testid="year-end-cash-flow-block">
			<h2 class="mb-3 flex items-center gap-2 text-lg font-semibold"><Wallet class="h-5 w-5" /> Cash flow</h2>
			<div class="overflow-x-auto rounded-lg border border-border">
				<table class="w-full text-sm tabular-nums">
					<thead class="bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
						<tr>
							<th class="px-3 py-2">Property</th>
							<th class="px-3 py-2 text-right">Income</th>
							<th class="px-3 py-2 text-right">Operating exp.</th>
							<th class="px-3 py-2 text-right">NOI</th>
							<th class="px-3 py-2 text-right">Debt service</th>
							<th class="px-3 py-2 text-right">Cash flow</th>
						</tr>
					</thead>
					<tbody>
						{#each cashRows as p (p.propertyId)}
							<tr class="border-t border-border/60">
								<td class="px-3 py-2 font-medium">{p.propertyName}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(p.income)}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(p.operatingExpenses)}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(p.noi)}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(p.debtService)}</td>
								<td class="px-3 py-2 text-right font-semibold {p.cashFlow < 0 ? 'text-destructive' : ''}">{formatAccountingCurrency(p.cashFlow)}</td>
							</tr>
						{:else}
							<tr><td colspan="6" class="px-3 py-6 text-center text-muted-foreground">No cash-flow activity for {year}.</td></tr>
						{/each}
					</tbody>
				</table>
			</div>
		</section>

		<!-- Block 2: Tax / Schedule E per property -->
		<section class="mb-6" data-testid="year-end-tax-block">
			<h2 class="mb-3 flex items-center gap-2 text-lg font-semibold"><Receipt class="h-5 w-5" /> Tax / Schedule E</h2>
			<div class="overflow-x-auto rounded-lg border border-border">
				<table class="w-full text-sm tabular-nums">
					<thead class="bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
						<tr>
							<th class="px-3 py-2">Property</th>
							<th class="px-3 py-2 text-right">Rental income</th>
							<th class="px-3 py-2 text-right">Deductible exp.</th>
							<th class="px-3 py-2 text-right">Mortgage interest</th>
							<th class="px-3 py-2 text-right">Depreciation</th>
							<th class="px-3 py-2 text-right">Taxable income</th>
						</tr>
					</thead>
					<tbody>
						{#each taxRows as p (p.propertyId)}
							<tr class="border-t border-border/60">
								<td class="px-3 py-2 font-medium">{p.propertyName}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(p.rentalIncome)}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(p.totalExpenses - p.mortgageInterest - p.depreciation)}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(p.mortgageInterest)}</td>
								<td class="px-3 py-2 text-right">
									{formatAccountingCurrency(p.depreciation)}
									{#if p.depreciationIsFirstYearEstimate}<span class="ml-1 text-xs text-amber-600" title="IRS mid-month estimate — confirm with accountant">(est.)</span>{/if}
								</td>
								<td class="px-3 py-2 text-right font-semibold {p.netIncome < 0 ? 'text-destructive' : ''}">{formatAccountingCurrency(p.netIncome)}</td>
							</tr>
						{:else}
							<tr><td colspan="6" class="px-3 py-6 text-center text-muted-foreground">No Schedule-E activity for {year}.</td></tr>
						{/each}
					</tbody>
				</table>
			</div>
		</section>

		<!-- Block 3: Property dispositions -->
		<section class="mb-6" data-testid="year-end-dispositions-block">
			<h2 class="mb-3 flex items-center gap-2 text-lg font-semibold"><Home class="h-5 w-5" /> Property sales / dispositions</h2>
			<div class="overflow-x-auto rounded-lg border border-border">
				<table class="w-full text-sm tabular-nums">
					<thead class="bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
						<tr>
							<th class="px-3 py-2">Property</th>
							<th class="px-3 py-2">Closed</th>
							<th class="px-3 py-2 text-right">Sale price</th>
							<th class="px-3 py-2 text-right">Selling costs</th>
							<th class="px-3 py-2 text-right">Sale-year dep.</th>
							<th class="px-3 py-2 text-right">Adjusted basis</th>
							<th class="px-3 py-2 text-right">Gain / loss</th>
							<th class="px-3 py-2 text-right">§1250</th>
						</tr>
					</thead>
					<tbody>
						{#each dispositionRows as d (d.id)}
							<tr class="border-t border-border/60">
								<td class="px-3 py-2 font-medium">{d.propertyName ?? `Property ${d.propertyId}`}</td>
								<td class="px-3 py-2">{new Date(d.closedOnDate).toLocaleDateString('en-US', { timeZone: 'UTC' })}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(d.salePrice)}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(d.sellingCosts)}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(d.saleYearDepreciation)}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(d.adjustedBasis)}</td>
								<td class="px-3 py-2 text-right font-semibold {d.gainLoss < 0 ? 'text-destructive' : ''}">{formatAccountingCurrency(d.gainLoss)}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(d.unrecapturedSection1250Gain)}</td>
							</tr>
						{:else}
							<tr><td colspan="8" class="px-3 py-6 text-center text-muted-foreground">No property sale or disposition recorded for {year}.</td></tr>
						{/each}
					</tbody>
				</table>
			</div>
		</section>

		<!-- Block 4: Rent roll -->
		<section class="mb-6" data-testid="year-end-rent-roll-block">
			<h2 class="mb-3 flex items-center gap-2 text-lg font-semibold"><Home class="h-5 w-5" /> Rent roll</h2>
			<div class="overflow-x-auto rounded-lg border border-border">
				<table class="w-full text-sm tabular-nums">
					<thead class="bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
						<tr>
							<th class="px-3 py-2">Property</th>
							<th class="px-3 py-2">Unit</th>
							<th class="px-3 py-2">Tenant</th>
							<th class="px-3 py-2 text-right">Rent</th>
							<th class="px-3 py-2">Status</th>
							<th class="px-3 py-2 text-right">Past due</th>
						</tr>
					</thead>
					<tbody>
						{#each rentRoll as r, i (i)}
							<tr class="border-t border-border/60">
								<td class="px-3 py-2 font-medium">{r.propertyName}</td>
								<td class="px-3 py-2">{r.unitNumber}</td>
								<td class="px-3 py-2">{r.tenantName}</td>
								<td class="px-3 py-2 text-right">{formatAccountingCurrency(r.monthlyRent)}</td>
								<td class="px-3 py-2">{r.leaseStatus}</td>
								<td class="px-3 py-2 text-right {r.pastDueBalance > 0 ? 'text-destructive' : ''}">{formatAccountingCurrency(r.pastDueBalance)}</td>
							</tr>
						{:else}
							<tr><td colspan="6" class="px-3 py-6 text-center text-muted-foreground">No current leases.</td></tr>
						{/each}
					</tbody>
				</table>
			</div>
		</section>

		<!-- §18 "see your accountant" caveats -->
		{#if view.accountantNotes.length > 0}
			<section class="rounded-lg border border-amber-300/50 bg-amber-50 p-4 dark:bg-amber-950/20" data-testid="year-end-accountant-notes">
				<h3 class="mb-2 flex items-center gap-2 text-sm font-semibold text-amber-800 dark:text-amber-300"><Info class="h-4 w-4" /> See your accountant</h3>
				<ul class="list-disc space-y-1 pl-5 text-sm text-amber-800 dark:text-amber-200">
					{#each view.accountantNotes as note}<li>{note}</li>{/each}
				</ul>
			</section>
		{/if}
	{/if}
</div>
