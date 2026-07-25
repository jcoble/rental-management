<script lang="ts">
	import { page } from '$app/state';
	import { tick } from 'svelte';
	import type { UnitDashboard } from '$lib/types';
	import type { ScanContext } from '$lib/scan/scan-context';
	import { money } from '../money';
	import RentTab from './RentTab.svelte';
	import ExpensesTab from './ExpensesTab.svelte';
	import { Banknote, ReceiptText } from '@lucide/svelte';

	let {
		dashboard,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onScan: (context?: Partial<ScanContext>) => void;
	} = $props();

	const unitId = $derived(dashboard.unit.id);
	const lease = $derived(dashboard.currentLease);
	const tenantAccountId = $derived(dashboard.tenantAccountId ?? null);

	let tenantAccountSection = $state<HTMLElement>();
	let operatingCostsSection = $state<HTMLElement>();
	let handledLanding = $state('');

	$effect(() => {
		if (page.url.searchParams.get('tab') !== 'money') return;
		const view = page.url.searchParams.get('view') === 'operating-costs' ? 'operating-costs' : 'tenant-account';
		const section = view === 'operating-costs' ? operatingCostsSection : tenantAccountSection;
		if (!section || handledLanding === view) return;
		handledLanding = view;
		void landOnSection(section);
	});

	async function landOnSection(section: HTMLElement) {
		await tick();
		section.scrollIntoView({ behavior: 'auto', block: 'start' });
		section.focus({ preventScroll: true });
	}

	function ledgerUrl(view: 'tenant-account' | 'operating-costs') {
		const url = new URL(`/units/${unitId}`, page.url.origin);
		url.searchParams.set('tab', 'money');
		url.searchParams.set('view', view);
		return `${url.pathname}${url.search}`;
	}
</script>

<div class="space-y-4" data-testid="unit-ledger-tab">
	<div class="grid gap-3 lg:grid-cols-2">
		<section class="rounded-lg border bg-card p-4" data-testid="unit-ledger-rent-summary">
			<div class="flex items-start gap-3">
				<div class="rounded-md bg-primary/10 p-2 text-primary">
					<Banknote class="h-5 w-5" />
				</div>
				<div class="min-w-0">
					<h3 class="text-sm font-semibold">Tenant account</h3>
					<p class="mt-1 text-sm text-muted-foreground">
						Rent charges, payments, deposits, and balances tied to the current lease.
					</p>
					<p class="mt-3 text-2xl font-bold">{money(dashboard.header.outstandingRentBalance)}</p>
					<p class="text-xs text-muted-foreground">Outstanding balance</p>
				</div>
			</div>
		</section>

		<section class="rounded-lg border bg-card p-4" data-testid="unit-ledger-expense-summary">
			<div class="flex items-start gap-3">
				<div class="rounded-md bg-muted p-2 text-muted-foreground">
					<ReceiptText class="h-5 w-5" />
				</div>
				<div class="min-w-0">
					<h3 class="text-sm font-semibold">Operating costs</h3>
					<p class="mt-1 text-sm text-muted-foreground">
						Repairs, supplies, bills, and receipts attached to this unit or its work orders.
					</p>
					<p class="mt-3 text-sm font-medium">
						{tenantAccountId ? (lease ? `${lease.leaseNumber} governs tenant account #${tenantAccountId}` : `Tenant account #${tenantAccountId} is open without a governing Agreement`) : 'No active tenant account'}
					</p>
				</div>
			</div>
		</section>
	</div>

	<div class="space-y-8" data-testid="unit-money-surface">
		<section bind:this={tenantAccountSection} tabindex="-1" class="scroll-mt-4 outline-none" data-testid="ledger-rent-panel">
			<RentTab
				{dashboard}
				tabQuery="money"
				ledgerQuery="rent"
				onScan={() => onScan({ type: 'Payment', propertyId: dashboard.unit.propertyId, unitId: dashboard.unit.id, leaseManagementId: dashboard.leaseManagementId ?? undefined, tenantAccountId: dashboard.tenantAccountId ?? undefined, returnTo: ledgerUrl('tenant-account') })}
			/>
		</section>
		<section bind:this={operatingCostsSection} tabindex="-1" class="scroll-mt-4 border-t pt-8 outline-none" data-testid="ledger-expenses-panel">
			<ExpensesTab {dashboard} tabQuery="money" ledgerQuery="expenses" {onScan} />
		</section>
	</div>
</div>
