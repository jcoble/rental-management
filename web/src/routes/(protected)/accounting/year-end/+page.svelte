<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { accounting, downloadYearEndPacket } from '$lib/api/endpoints/accounting';
	import { showError, apiErrorMessage } from '$lib/utils/toast';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Download, Wallet, Receipt, Home, AlertCircle, Info } from '@lucide/svelte';

	// Default to the previous calendar year (the year you file for).
	const currentYear = new Date().getFullYear();
	let year = $state(currentYear - 1);
	const yearOptions = Array.from({ length: 6 }, (_, i) => currentYear - i);

	let selectedPropertyId = $state<number | 'all'>('all');

	const query = createQuery(() => ({
		queryKey: ['year-end', year],
		queryFn: () => accounting.yearEnd(year)
	}));
	const view = $derived(query.data);

	function fmt(value: number): string {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value || 0);
	}

	// Property options come from whichever properties appear in either block.
	const propertyOptions = $derived(() => {
		const map = new Map<number, string>();
		for (const p of view?.cashFlow.properties ?? []) map.set(p.propertyId, p.propertyName);
		for (const p of view?.scheduleE.properties ?? []) map.set(p.propertyId, p.propertyName);
		return [...map.entries()].map(([id, name]) => ({ id, name })).sort((a, b) => a.name.localeCompare(b.name));
	});

	const cashRows = $derived(
		(view?.cashFlow.properties ?? []).filter((p) => selectedPropertyId === 'all' || p.propertyId === selectedPropertyId)
	);
	const taxRows = $derived(
		(view?.scheduleE.properties ?? []).filter((p) => selectedPropertyId === 'all' || p.propertyId === selectedPropertyId)
	);
	const rentRoll = $derived(view?.rentRoll ?? []);

	// When a single property is selected, total just that property; else the portfolio totals.
	const totalCashFlow = $derived(
		selectedPropertyId === 'all'
			? (view?.cashFlow.totalCashFlow ?? 0)
			: cashRows.reduce((s, p) => s + p.cashFlow, 0)
	);
	const totalTaxable = $derived(
		selectedPropertyId === 'all'
			? (view?.scheduleE.netIncome ?? 0)
			: taxRows.reduce((s, p) => s + p.netIncome, 0)
	);

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
			<p class="mt-1 text-sm text-muted-foreground">Cash flow vs. taxable income, side by side — with depreciation and debt service finally in the picture.</p>
		</div>
		<div class="flex flex-wrap items-end gap-2">
			<label class="flex flex-col text-xs font-medium text-muted-foreground">
				Tax year
				<select bind:value={year} class="mt-1 rounded-md border border-input bg-background px-3 py-2 text-sm text-foreground" data-testid="year-end-year-select">
					{#each yearOptions as y}<option value={y}>{y}</option>{/each}
				</select>
			</label>
			<label class="flex flex-col text-xs font-medium text-muted-foreground">
				Property
				<select bind:value={selectedPropertyId} class="mt-1 rounded-md border border-input bg-background px-3 py-2 text-sm text-foreground" data-testid="year-end-property-select">
					<option value="all">All properties</option>
					{#each propertyOptions() as p}<option value={p.id}>{p.name}</option>{/each}
				</select>
			</label>
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
		<!-- The two headline numbers, side by side -->
		<div class="mb-6 grid gap-4 sm:grid-cols-2" data-testid="year-end-headline">
			<div class="rounded-xl border border-border bg-card p-5" data-testid="year-end-cash-flow-headline">
				<div class="flex items-center gap-2 text-sm font-medium text-muted-foreground"><Wallet class="h-4 w-4" /> Cash flow (what hit your pocket)</div>
				<p class="mt-2 font-mono text-3xl font-bold tabular-nums {totalCashFlow < 0 ? 'text-destructive' : 'text-foreground'}" data-testid="year-end-cash-flow-total">{fmt(totalCashFlow)}</p>
				<p class="mt-1 text-xs text-muted-foreground">Rent in − operating expenses − debt service. Excludes non-cash depreciation.</p>
			</div>
			<div class="rounded-xl border border-border bg-card p-5" data-testid="year-end-taxable-headline">
				<div class="flex items-center gap-2 text-sm font-medium text-muted-foreground"><Receipt class="h-4 w-4" /> Taxable income (Schedule E)</div>
				<p class="mt-2 font-mono text-3xl font-bold tabular-nums {totalTaxable < 0 ? 'text-destructive' : 'text-foreground'}" data-testid="year-end-taxable-total">{fmt(totalTaxable)}</p>
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
								<td class="px-3 py-2 text-right">{fmt(p.income)}</td>
								<td class="px-3 py-2 text-right">{fmt(p.operatingExpenses)}</td>
								<td class="px-3 py-2 text-right">{fmt(p.noi)}</td>
								<td class="px-3 py-2 text-right">{fmt(p.debtService)}</td>
								<td class="px-3 py-2 text-right font-semibold {p.cashFlow < 0 ? 'text-destructive' : ''}">{fmt(p.cashFlow)}</td>
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
								<td class="px-3 py-2 text-right">{fmt(p.rentalIncome)}</td>
								<td class="px-3 py-2 text-right">{fmt(p.totalExpenses - p.mortgageInterest - p.depreciation)}</td>
								<td class="px-3 py-2 text-right">{fmt(p.mortgageInterest)}</td>
								<td class="px-3 py-2 text-right">
									{fmt(p.depreciation)}
									{#if p.depreciationIsFirstYearEstimate}<span class="ml-1 text-xs text-amber-600" title="IRS mid-month estimate — confirm with accountant">(est.)</span>{/if}
								</td>
								<td class="px-3 py-2 text-right font-semibold {p.netIncome < 0 ? 'text-destructive' : ''}">{fmt(p.netIncome)}</td>
							</tr>
						{:else}
							<tr><td colspan="6" class="px-3 py-6 text-center text-muted-foreground">No Schedule-E activity for {year}.</td></tr>
						{/each}
					</tbody>
				</table>
			</div>
		</section>

		<!-- Block 3: Rent roll -->
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
								<td class="px-3 py-2 text-right">{fmt(r.monthlyRent)}</td>
								<td class="px-3 py-2">{r.leaseStatus}</td>
								<td class="px-3 py-2 text-right {r.pastDueBalance > 0 ? 'text-destructive' : ''}">{fmt(r.pastDueBalance)}</td>
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
