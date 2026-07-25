<script lang="ts">
	import type { UnitDashboard } from '$lib/types';
	import { money } from './money';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Pencil, ScanLine, CircleAlert } from '@lucide/svelte';

	let {
		dashboard,
		onEdit,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onEdit: () => void;
		/** Routes into the receipt scan flow. Domain-specific work happens inline in tabs. */
		onScan: () => void;
	} = $props();

	const header = $derived(dashboard.header);
	const unit = $derived(dashboard.unit);

	// Rent chip tone by state. NoLease reads neutral (nothing owed yet).
	const rentTone = $derived(
		header.rentState === 'Overdue'
			? 'm3-tone-chip border m3-tone--error'
			: header.rentState === 'Due'
				? 'm3-tone-chip border m3-tone--warning'
				: header.rentState === 'Current'
					? 'm3-tone-chip border m3-tone--success'
					: 'bg-muted text-muted-foreground border-border'
	);

	const rentLabel = $derived(
		header.rentState === 'NoLease'
			? 'No lease'
			: header.outstandingRentBalance > 0
				? `${header.rentState} · ${money(header.outstandingRentBalance)}`
				: 'Rent current'
	);
</script>

<header class="rc-hero m3-surface-art m3-surface-art--band rounded-xl border p-4 sm:p-5" data-testid="unit-header">
	<div class="flex flex-wrap items-start justify-between gap-3">
		<div class="min-w-0">
			<nav class="text-xs text-muted-foreground" aria-label="Breadcrumb">
				<a href="/properties/{unit.propertyId}" class="hover:underline">{dashboard.propertyName}</a>
				<span class="px-1">/</span>
				<a href="/units" class="hover:underline">Units</a>
			</nav>
			<h1 class="mt-0.5 flex items-center gap-2 text-2xl font-bold" data-testid="unit-title">
				Unit {unit.unitNumber}
				<StatusBadge status={unit.status} />
			</h1>
			<p class="mt-0.5 text-sm text-muted-foreground">
				{unit.bedrooms} bd · {unit.bathrooms} ba{unit.squareFeet ? ` · ${unit.squareFeet} sqft` : ''} · Market {money(unit.marketRent)}
			</p>
		</div>

		<!-- Primary receipt action; other domain work happens inline in the relevant tab. -->
		<div class="flex shrink-0 items-center gap-2">
			<Button variant="outline" onclick={() => onEdit()} class="gap-2" data-testid="unit-detail-edit-button">
				<Pencil class="h-4 w-4" />
				Edit
			</Button>
			<Button onclick={() => onScan()} class="gap-2" data-testid="unit-scan-button">
				<ScanLine class="h-4 w-4" />
				Scan receipt
			</Button>
		</div>
	</div>

	<!-- Health chips. -->
	<div class="mt-4 flex flex-wrap gap-2" data-testid="unit-health-chips">
		<span class="inline-flex items-center gap-1 rounded-full border px-2.5 py-1 text-xs font-medium {rentTone}" data-testid="chip-rent">
			{#if header.rentState === 'Overdue'}<CircleAlert class="h-3 w-3" />{/if}
			{rentLabel}
		</span>
		<span class="inline-flex items-center gap-1 rounded-full border border-border bg-muted/40 px-2.5 py-1 text-xs font-medium text-muted-foreground" data-testid="chip-repairs">
			{header.openWorkOrderCount} open repair{header.openWorkOrderCount === 1 ? '' : 's'}
		</span>
		{#if header.leaseEndsInDays != null}
			<span class="inline-flex items-center gap-1 rounded-full border border-border bg-muted/40 px-2.5 py-1 text-xs font-medium text-muted-foreground" data-testid="chip-lease-ends">
				Lease ends in {header.leaseEndsInDays}d
			</span>
		{/if}
		<span class="inline-flex items-center gap-1 rounded-full border border-border bg-muted/40 px-2.5 py-1 text-xs font-medium text-muted-foreground" data-testid="chip-docs">
			{header.docsNeedingReviewCount} doc{header.docsNeedingReviewCount === 1 ? '' : 's'}
		</span>
		{#if header.currentTenantName}
			<span class="inline-flex items-center gap-1 rounded-full border border-border bg-muted/40 px-2.5 py-1 text-xs font-medium text-muted-foreground" data-testid="chip-tenant">
				{header.currentTenantName}
			</span>
		{/if}
		<span class="inline-flex items-center gap-1 rounded-full border border-border bg-muted/40 px-2.5 py-1 text-xs font-medium text-muted-foreground" data-testid="chip-possession">
			{dashboard.occupancyPossession.status}
		</span>
		<span class="inline-flex items-center gap-1 rounded-full border border-border bg-muted/40 px-2.5 py-1 text-xs font-medium text-muted-foreground" data-testid="chip-maintenance-condition">
			{dashboard.maintenanceTurnover.status}
		</span>
	</div>
</header>
