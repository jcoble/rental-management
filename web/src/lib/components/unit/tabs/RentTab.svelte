<script lang="ts">
	import { page } from '$app/state';
	import { pushState, replaceState } from '$app/navigation';
	import type { UnitDashboard } from '$lib/types';
	import AccountingDetailMode from '$lib/components/accounting/AccountingDetailMode.svelte';
	import TenantLedgerPanel from '$lib/components/accounting/TenantLedgerPanel.svelte';
	import PaymentDetail from '$lib/components/records/PaymentDetail.svelte';
	import { Button } from '$lib/components/ui/button';
	import { ArrowLeft } from '@lucide/svelte';

	let {
		dashboard,
		onScan,
		tabQuery = 'rent',
		ledgerQuery
	}: {
		dashboard: UnitDashboard;
		onScan: () => void;
		tabQuery?: string;
		ledgerQuery?: string;
	} = $props();

	const tenantAccountId = $derived(dashboard.tenantAccountId ?? null);
	const selectedReceipt = $derived.by(() => {
		const explicitPayment = page.url.searchParams.get('payment');
		if (explicitPayment !== null) return Number(explicitPayment) || null;
		return page.state?.unitPathname === page.url.pathname ? page.state?.unitPaymentId ?? null : null;
	});

	function unitUrl(payment?: number): string {
		const url = new URL(`/units/${dashboard.unit.id}`, page.url.origin);
		url.searchParams.set('tab', tabQuery);
		if (ledgerQuery) {
			url.searchParams.set('view', ledgerQuery === 'expenses' ? 'operating-costs' : 'tenant-account');
		}
		if (payment) url.searchParams.set('payment', String(payment));
		return `${url.pathname}${url.search}`;
	}

	function openReceipt(id: number): void {
		pushState(unitUrl(id), {
			...(page.state ?? {}),
			unitTab: 'money',
			unitView: 'tenant-account',
			unitPaymentId: id,
			unitExpenseId: null
		});
	}

	function clearSelection(): void {
		replaceState(unitUrl(), {
			...(page.state ?? {}),
			unitTab: 'money',
			unitView: 'tenant-account',
			unitPaymentId: null
		});
	}
</script>

<!-- LoadingState is rendered by TenantLedgerPanel. -->
<AccountingDetailMode class="space-y-4" testid="unit-accounting-detail-mode">
	<div class="space-y-4" data-testid="unit-rent-tab">
		{#if selectedReceipt && tenantAccountId}
			<Button variant="outline" size="sm" class="gap-1" onclick={clearSelection} data-testid="payment-back-to-list">
				<ArrowLeft class="size-4" /> Back to payments
			</Button>
			<PaymentDetail
				tenantAccountId={tenantAccountId}
				tenantLedgerEntryId={selectedReceipt}
				expectedUnitId={dashboard.unit.id}
				onUnitMismatch={clearSelection}
			/>
		{:else}
			<TenantLedgerPanel {dashboard} {onScan} onopenpayment={openReceipt} />
		{/if}
	</div>
</AccountingDetailMode>
