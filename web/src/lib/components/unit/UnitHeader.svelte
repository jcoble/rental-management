<script lang="ts">
	import type { UnitDashboard } from '$lib/types';
	import { money } from './money';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { formatDateOnly } from '$lib/utils/date';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { Pencil, ScanLine } from '@lucide/svelte';

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

	const rentLabel = $derived(
		header.rentState === 'NoLease'
			? 'No current lease'
			: header.outstandingRentBalance > 0
				? `${money(header.outstandingRentBalance)} still owed`
				: 'Paid up to date'
	);
</script>

<header class="rounded-2xl bg-card p-4 sm:p-5" data-testid="unit-header">
	<div class="flex flex-wrap items-start justify-between gap-3">
		<div class="min-w-0">
			<nav class="text-xs text-muted-foreground" aria-label="Breadcrumb">
				<a href="/properties/{unit.propertyId}" class="hover:underline">{dashboard.propertyName}</a>
				<span class="px-1">/</span>
				<a href="/units" class="hover:underline">Command Center</a>
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

	<div class="mt-4 grid gap-3 border-t pt-4 sm:grid-cols-2 lg:grid-cols-4" data-testid="unit-health-chips">
		<div data-testid="chip-tenant">
			<p class="text-xs text-muted-foreground">Resident</p>
			<p class="mt-0.5 text-sm font-medium">{header.currentTenantName ?? 'No current resident'}</p>
		</div>
		<div data-testid="chip-rent">
			<p class="text-xs text-muted-foreground">Rent</p>
			<p class="mt-0.5 text-sm font-medium">{rentLabel}</p>
		</div>
		<div data-testid="chip-lease-ends">
			<p class="text-xs text-muted-foreground">Lease ends</p>
			<p class="mt-0.5 text-sm font-medium">{dashboard.currentLease?.endDate ? formatDateOnly(dashboard.currentLease.endDate) : 'No end date'}</p>
		</div>
		<div data-testid="chip-repairs">
			<p class="text-xs text-muted-foreground">Repairs</p>
			<p class="mt-0.5 text-sm font-medium">{header.openWorkOrderCount === 0 ? 'No open repairs' : `${header.openWorkOrderCount} open repair${header.openWorkOrderCount === 1 ? '' : 's'}`}</p>
		</div>
		<div data-testid="chip-possession" class="sm:col-span-2 lg:col-span-4">
			<p class="text-xs text-muted-foreground">Current stage</p>
			<p class="mt-0.5 text-sm font-medium">{formatStatusLabel(dashboard.occupancyPossession.status)} · {formatStatusLabel(dashboard.maintenanceTurnover.status)}</p>
		</div>
	</div>
</header>
