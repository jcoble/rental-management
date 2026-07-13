<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import type { UnitDashboard } from '$lib/types';
	import type { ScanContext } from '$lib/scan/scan-context';
	import * as Tabs from '$lib/components/ui/tabs';
	import { money } from '../money';
	import RentTab from './RentTab.svelte';
	import ExpensesTab from './ExpensesTab.svelte';
	import { Banknote, ReceiptText } from '@lucide/svelte';

	type LedgerView = 'rent' | 'expenses';

	let {
		dashboard,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onScan: (context?: Partial<ScanContext>) => void;
	} = $props();

	const unitId = $derived(dashboard.unit.id);
	const lease = $derived(dashboard.currentLease);

	function resolveLedgerView(tab: string | null, ledger: string | null): LedgerView {
		if (tab === 'expenses' || ledger === 'expenses') return 'expenses';
		return 'rent';
	}

	let activeLedgerView = $state(resolveLedgerView(page.url.searchParams.get('tab'), page.url.searchParams.get('ledger')));

	$effect(() => {
		const next = resolveLedgerView(page.url.searchParams.get('tab'), page.url.searchParams.get('ledger'));
		if (next !== activeLedgerView) activeLedgerView = next;
	});

	function ledgerUrl(view: LedgerView) {
		const url = new URL(`/units/${unitId}`, page.url.origin);
		url.searchParams.set('tab', 'ledger');
		url.searchParams.set('ledger', view);
		return `${url.pathname}${url.search}`;
	}

	function setLedgerView(value: string) {
		const next: LedgerView = value === 'expenses' ? 'expenses' : 'rent';
		activeLedgerView = next;
		const url = new URL(page.url);
		url.searchParams.set('tab', 'ledger');
		url.searchParams.set('ledger', next);
		if (next === 'rent') {
			url.searchParams.delete('expense');
		} else {
			url.searchParams.delete('payment');
		}
		goto(url, { replaceState: true, keepFocus: true, noScroll: true });
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
						{lease ? `${lease.leaseNumber} is the active tenant account` : 'No active tenant account'}
					</p>
				</div>
			</div>
		</section>
	</div>

	<Tabs.Root value={activeLedgerView} onValueChange={setLedgerView}>
		<Tabs.List class="w-full sm:w-auto" data-testid="unit-ledger-tabs">
			<Tabs.Trigger value="rent" data-testid="ledger-tab-rent">Tenant account</Tabs.Trigger>
			<Tabs.Trigger value="expenses" data-testid="ledger-tab-expenses">Operating costs</Tabs.Trigger>
		</Tabs.List>
		<Tabs.Content value="rent" class="mt-4" data-testid="ledger-rent-panel">
			<RentTab
				{dashboard}
				tabQuery="ledger"
				ledgerQuery="rent"
				onScan={() => onScan({ type: 'Payment', propertyId: dashboard.unit.propertyId, unitId: dashboard.unit.id, leaseManagementId: dashboard.currentLease?.leaseManagementId ?? undefined, tenantAccountId: dashboard.currentLease?.tenantAccountId ?? undefined, returnTo: ledgerUrl('rent') })}
			/>
		</Tabs.Content>
		<Tabs.Content value="expenses" class="mt-4" data-testid="ledger-expenses-panel">
			<ExpensesTab {dashboard} tabQuery="ledger" ledgerQuery="expenses" {onScan} />
		</Tabs.Content>
	</Tabs.Root>
</div>
