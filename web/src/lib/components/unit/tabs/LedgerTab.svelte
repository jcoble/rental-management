<script lang="ts">
	import { page } from '$app/state';
	import type { UnitDashboard } from '$lib/types';
	import type { ScanContext } from '$lib/scan/scan-context';
	import {
		resolveUnitUrlDestination,
		UNIT_SUBNAV_ITEM_CLASS,
		UNIT_SUBNAV_LIST_CLASS,
		type UnitView,
	} from '$lib/components/unit/unit-tabs';
	import RentTab from './RentTab.svelte';
	import ExpensesTab from './ExpensesTab.svelte';

	let {
		dashboard,
		onScan,
		onOpenTab,
	}: {
		dashboard: UnitDashboard;
		onScan: (context?: Partial<ScanContext>) => void;
		onOpenTab: (tab: string, view?: UnitView) => void;
	} = $props();

	const unitId = $derived(dashboard.unit.id);
	const activeView = $derived.by(() => {
		const urlResolution = resolveUnitUrlDestination(page.url);
		if (urlResolution.hasExplicitViewOrRecord) {
			return urlResolution.destination.view === 'operating-costs' ? 'operating-costs' : 'tenant-account';
		}

		const rememberedView = page.state?.unitPathname === page.url.pathname ? page.state?.unitView : null;
		return rememberedView === 'operating-costs' ? 'operating-costs' : 'tenant-account';
	});

	function ledgerUrl(view: 'tenant-account' | 'operating-costs') {
		const url = new URL(`/units/${unitId}`, page.url.origin);
		url.searchParams.set('tab', 'money');
		url.searchParams.set('view', view);
		return `${url.pathname}${url.search}`;
	}

	function setView(view: 'tenant-account' | 'operating-costs') {
		// Nested Money clicks use the same URL/state adapter as primary tabs. This
		// keeps a user view click authoritative after arriving from a deep record URL
		// and lets the write-once synchronizer converge without a stale pushState.
		onOpenTab('money', view);
	}
</script>

<div class="space-y-4" data-testid="unit-ledger-tab">
	<div class="min-w-0 overflow-x-auto">
		<nav aria-label="Money sections" data-testid="unit-money-subnav">
			<div class={UNIT_SUBNAV_LIST_CLASS}>
				<button type="button" aria-current={activeView === 'tenant-account' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setView('tenant-account')} data-testid="unit-money-view-tenant-account">Rent &amp; payments</button>
				<button type="button" aria-current={activeView === 'operating-costs' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setView('operating-costs')} data-testid="unit-money-view-operating-costs">Property expenses</button>
			</div>
		</nav>
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
