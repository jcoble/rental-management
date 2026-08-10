<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { tenantAccounts } from '$lib/api/endpoints/tenant-accounts';
	import { formatAccountingCurrency, formatAccountingDate } from '$lib/accounting/accounting-display';
	import { normalizeTenantLedgerDescription } from '$lib/accounting/tenant-ledger-display';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	const PAGE_SIZE = 25;
	let propertyId = $state(''); let unitId = $state(''); let tenant = $state(''); let entryType = $state(''); let page = $state(1);
	const query = createQuery(() => ({ queryKey: ['portfolio-lease-ledger', propertyId, unitId, tenant, entryType, page], queryFn: () => tenantAccounts.entriesPage({
		propertyId: propertyId ? Number(propertyId) : undefined, unitId: unitId ? Number(unitId) : undefined,
		search: tenant || undefined, entryType: entryType || undefined, skip: (page - 1) * PAGE_SIZE, take: PAGE_SIZE, sort: '-effectiveOn'
	}) }));
	function month(value: string): string { return new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(`${value}T00:00:00Z`)); }
</script>
<section class="space-y-4" data-testid="portfolio-lease-ledger">
	<header><h2 class="text-lg font-semibold">Rent & payments across the portfolio</h2><p class="text-sm text-muted-foreground">Charges, payments, credits, and deposits for every unit and tenant.</p></header>
	<div class="grid gap-2 sm:grid-cols-4" data-testid="portfolio-ledger-filters">
		<Input bind:value={propertyId} inputmode="numeric" placeholder="Property ID" aria-label="Property" />
		<Input bind:value={unitId} inputmode="numeric" placeholder="Unit ID" aria-label="Unit" />
		<Input bind:value={tenant} placeholder="Tenant name" aria-label="Tenant" />
		<Input bind:value={entryType} placeholder="Type" aria-label="Type" />
	</div>
	{#if query.isLoading}<p class="text-sm text-muted-foreground">Loading portfolio ledger…</p>{:else if query.isError}<p role="alert">The portfolio ledger could not be loaded.</p>{:else}
		<div class="overflow-hidden rounded-xl border" data-testid="portfolio-ledger-months">
			{#each query.data?.items ?? [] as row, index (row.tenantLedgerEntryId)}
				{#if index === 0 || month(row.effectiveOn) !== month(query.data!.items[index - 1].effectiveOn)}<div class="flex justify-between border-y bg-muted/40 px-4 py-2 font-semibold" data-testid={`portfolio-ledger-month-${row.effectiveOn.slice(0,7)}`}><span>{month(row.effectiveOn)}</span><span class="text-sm font-normal">Charges {formatAccountingCurrency(row.monthCharges)} · Payments & credits {formatAccountingCurrency(row.monthPaymentsAndCredits)}</span></div>{/if}
				<a href={`/units/${row.unitId}?tab=rent`} class="grid gap-1 border-t px-4 py-3 hover:bg-muted/30 sm:grid-cols-[7rem_1fr_1fr_auto]" data-testid={`portfolio-ledger-row-${row.tenantLedgerEntryId}`}>
					<span>{formatAccountingDate(row.effectiveOn)}</span><span>{normalizeTenantLedgerDescription(row.description)}<small class="block text-muted-foreground">{row.primaryTenantName ?? 'Tenant'} · Unit {row.unitNumber}</small></span><span>{row.propertyName}</span><span class="font-mono">{row.direction === 'Debit' ? '' : '−'}{formatAccountingCurrency(row.amount)}</span>
				</a>
			{/each}
		</div>
		<div class="flex justify-end gap-2"><Button variant="outline" disabled={page === 1} onclick={() => page--}>Previous</Button><Button variant="outline" disabled={(query.data?.items.length ?? 0) < PAGE_SIZE} onclick={() => page++}>Next</Button></div>
	{/if}
</section>
