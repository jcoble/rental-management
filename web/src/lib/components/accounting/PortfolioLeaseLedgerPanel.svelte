<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { tenantAccounts } from '$lib/api/endpoints/tenant-accounts';
	import { formatAccountingCurrency, formatAccountingDate } from '$lib/accounting/accounting-display';
	import { normalizeTenantLedgerDescription } from '$lib/accounting/tenant-ledger-display';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import { properties } from '$lib/api/endpoints/properties';
	import { units } from '$lib/api/endpoints/units';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	const PAGE_SIZE = 25;
	let propertyId = $state(''); let unitId = $state(''); let tenant = $state(''); let entryType = $state(''); let page = $state(1);
	const query = createQuery(() => ({ queryKey: ['portfolio-lease-ledger', propertyId, unitId, tenant, entryType, page], queryFn: () => tenantAccounts.entriesPage({
		propertyId: propertyId ? Number(propertyId) : undefined, unitId: unitId ? Number(unitId) : undefined,
		search: tenant || undefined, entryType: entryType || undefined, skip: (page - 1) * PAGE_SIZE, take: PAGE_SIZE, sort: '-effectiveOn'
	}) }));
	const portfolioId = $derived(getCurrentPortfolioId());

	async function loadPropertyOptions(params: { search?: string; skip: number; take: number }) {
		const result = await properties.listPage(portfolioId, { ...params, sort: 'name' });
		return {
			...result,
			items: result.items.map((property) => ({ id: property.id, label: property.name, description: property.addressLine1 }))
		};
	}

	// Units follow the chosen property so the list stays short; with no property picked it shows every unit.
	async function loadUnitOptions(params: { search?: string; skip: number; take: number }) {
		const result = await units.listPage({ ...params, propertyId: propertyId ? Number(propertyId) : undefined });
		return {
			...result,
			items: result.items.map((unit) => ({ id: unit.id, label: `Unit ${unit.unitNumber}` }))
		};
	}

	function month(value: string): string { return new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(`${value}T00:00:00Z`)); }
</script>
<section class="space-y-4" data-testid="portfolio-lease-ledger">
	<header><h2 class="text-lg font-semibold">Rent & payments across the portfolio</h2><p class="text-sm text-muted-foreground">Charges, payments, credits, and deposits for every unit and tenant.</p></header>
	<div class="grid gap-2 sm:grid-cols-4" data-testid="portfolio-ledger-filters">
		<RemoteRecordSelect
			queryKey={['portfolio-ledger-properties', portfolioId]}
			label="Property"
			bind:value={propertyId}
			placeholder="All properties"
			clearLabel="All properties"
			searchPlaceholder="Search properties…"
			emptyLabel="No matching properties"
			loadPage={loadPropertyOptions}
			onValueChange={() => { unitId = ''; page = 1; }}
			testid="portfolio-ledger-property"
		/>
		<RemoteRecordSelect
			queryKey={['portfolio-ledger-units', propertyId]}
			label="Unit"
			bind:value={unitId}
			placeholder="All units"
			clearLabel="All units"
			searchPlaceholder="Search units…"
			emptyLabel="No matching units"
			loadPage={loadUnitOptions}
			onValueChange={() => { page = 1; }}
			testid="portfolio-ledger-unit"
		/>
		<label class="grid gap-1.5"><span class="text-sm font-medium">Tenant</span><Input bind:value={tenant} placeholder="Tenant name" aria-label="Tenant" /></label>
		<label class="grid gap-1.5"><span class="text-sm font-medium">Entry type</span><Input bind:value={entryType} placeholder="Rent, payment, credit…" aria-label="Entry type" /></label>
	</div>
	{#if query.isLoading}<p class="text-sm text-muted-foreground">Loading portfolio ledger…</p>{:else if query.isError}<p role="alert">The portfolio ledger could not be loaded.</p>{:else}
		<div class="overflow-hidden rounded-xl border" data-testid="portfolio-ledger-months">
			{#each query.data?.items ?? [] as row, index (row.tenantLedgerEntryId)}
				{#if index === 0 || month(row.effectiveOn) !== month(query.data!.items[index - 1].effectiveOn)}<div class="flex justify-between border-y bg-muted/40 px-4 py-2 font-semibold" data-testid={`portfolio-ledger-month-${row.effectiveOn.slice(0,7)}`}><span>{month(row.effectiveOn)}</span><span class="text-sm font-normal">Charges {formatAccountingCurrency(row.monthCharges)} · Payments & credits {formatAccountingCurrency(row.monthPaymentsAndCredits)}</span></div>{/if}
				<a href={`/units/${row.unitId}?tab=money&view=tenant-account`} class="grid gap-1 border-t px-4 py-3 hover:bg-muted/30 sm:grid-cols-[7rem_1fr_1fr_auto]" data-testid={`portfolio-ledger-row-${row.tenantLedgerEntryId}`}>
					<span>{formatAccountingDate(row.effectiveOn)}</span><span>{normalizeTenantLedgerDescription(row.description)}<small class="block text-muted-foreground">{row.primaryTenantName ?? 'Tenant'} · Unit {row.unitNumber}</small></span><span>{row.propertyName}</span><span class="font-mono">{row.direction === 'Debit' ? '' : '−'}{formatAccountingCurrency(row.amount)}</span>
				</a>
			{/each}
		</div>
		<div class="flex justify-end gap-2"><Button variant="outline" disabled={page === 1} onclick={() => page--}>Previous</Button><Button variant="outline" disabled={(query.data?.items.length ?? 0) < PAGE_SIZE} onclick={() => page++}>Next</Button></div>
	{/if}
</section>
