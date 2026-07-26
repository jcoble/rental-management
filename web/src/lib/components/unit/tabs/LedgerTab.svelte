<script lang="ts">
	import { page } from '$app/state';
	import { pushState } from '$app/navigation';
	import type { UnitDashboard } from '$lib/types';
	import type { ScanContext } from '$lib/scan/scan-context';
	import RentTab from './RentTab.svelte';
	import ExpensesTab from './ExpensesTab.svelte';

	let {
		dashboard,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onScan: (context?: Partial<ScanContext>) => void;
	} = $props();

	const unitId = $derived(dashboard.unit.id);
	const activeView = $derived(
		(page.state.unitView ?? page.url.searchParams.get('view')) === 'operating-costs'
			? 'operating-costs'
			: 'tenant-account'
	);

	function ledgerUrl(view: 'tenant-account' | 'operating-costs') {
		const url = new URL(`/units/${unitId}`, page.url.origin);
		url.searchParams.set('tab', 'money');
		url.searchParams.set('view', view);
		return `${url.pathname}${url.search}`;
	}

	function setView(view: 'tenant-account' | 'operating-costs') {
		if (view === activeView) return;
		pushState(ledgerUrl(view), {
			...page.state,
			unitTab: 'money',
			unitView: view,
			unitPaymentId: null,
			unitExpenseId: null,
		});
	}
</script>

<div class="space-y-4" data-testid="unit-ledger-tab">
	<div class="min-w-0 overflow-x-auto">
		<div class="m3-tabs-list min-w-max" role="tablist" aria-label="Money views" data-testid="unit-money-tabs">
			<button type="button" role="tab" aria-selected={activeView === 'tenant-account'} data-state={activeView === 'tenant-account' ? 'active' : 'inactive'} class="m3-tabs-trigger" onclick={() => setView('tenant-account')}>Rent &amp; payments</button>
			<button type="button" role="tab" aria-selected={activeView === 'operating-costs'} data-state={activeView === 'operating-costs' ? 'active' : 'inactive'} class="m3-tabs-trigger" onclick={() => setView('operating-costs')}>Property expenses</button>
		</div>
	</div>

	<div data-testid="unit-money-surface">
		{#if activeView === 'tenant-account'}
		<section tabindex="-1" class="outline-none" data-testid="ledger-rent-panel">
			<RentTab
				{dashboard}
				tabQuery="money"
				ledgerQuery="rent"
				onScan={() => onScan({ type: 'Payment', propertyId: dashboard.unit.propertyId, unitId: dashboard.unit.id, leaseManagementId: dashboard.leaseManagementId ?? undefined, tenantAccountId: dashboard.tenantAccountId ?? undefined, returnTo: ledgerUrl('tenant-account') })}
			/>
		</section>
		{:else}
		<section tabindex="-1" class="outline-none" data-testid="ledger-expenses-panel">
			<ExpensesTab {dashboard} tabQuery="money" ledgerQuery="expenses" {onScan} />
		</section>
		{/if}
	</div>
</div>
