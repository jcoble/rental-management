<script lang="ts">
	import type {
		PortalTenantAccountHistory,
		PortalTenantAccountHistoryItem,
	} from '$lib/api/endpoints/portal';
	import { formatAccountingCurrency, formatAccountingDate, accountingAmountClass } from '$lib/accounting/accounting-display';
	import { tenantLedgerLabel } from '$lib/portal/tenant-ledger';
	import { Button } from '$lib/components/ui/button';
	import TenantPaymentAllocationDetails from '$lib/components/accounting/TenantPaymentAllocationDetails.svelte';

	let {
		history,
		onlinePaymentsUnavailable,
		payPending,
		payingId,
		onpay,
	}: {
		history: PortalTenantAccountHistory;
		onlinePaymentsUnavailable: boolean;
		payPending: boolean;
		payingId: number | null;
		onpay: (entry: PortalTenantAccountHistoryItem) => void;
	} = $props();

	function monthKey(effectiveOn: string): string {
		return effectiveOn.slice(0, 7);
	}

	function monthLabel(effectiveOn: string): string {
		const [year, month] = monthKey(effectiveOn).split('-').map(Number);
		return new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' })
			.format(new Date(Date.UTC(year, month - 1, 1)));
	}
</script>

<div class="space-y-4 pt-4" data-testid="portal-account-history-list">
	{#each history.items as entry, index (entry.tenantLedgerEntryId)}
		{#if index === 0 || monthKey(history.items[index - 1].effectiveOn) !== monthKey(entry.effectiveOn)}
			<div class="rounded-t-xl border border-border bg-muted/20 px-4 py-3" data-testid={`portal-history-month-${monthKey(entry.effectiveOn)}`}>
				<h3 class="font-semibold">{monthLabel(entry.effectiveOn)}</h3>
			</div>
		{/if}
		<div
			id={`portal-ledger-entry-${entry.tenantLedgerEntryId}`}
			class="grid gap-2 border-b border-border py-4 md:grid-cols-[8rem_minmax(0,1fr)_9rem_9rem_7rem] md:items-center md:gap-4 {entry.isFocused ? 'bg-primary/5 outline outline-2 outline-primary/40' : ''}"
			data-focused={entry.isFocused}
			data-testid="portal-history-row"
		>
			<time class="font-medium" datetime={entry.effectiveOn}>{formatAccountingDate(entry.effectiveOn)}</time>
			<!-- The server's entry.displayType stays private; tenantLedgerLabel owns visible vocabulary. -->
			<div class="min-w-0">
				<p class="font-medium">{tenantLedgerLabel(entry)}</p>
				{#if entry.entryType === 'PaymentReceipt'}
					<TenantPaymentAllocationDetails
						allocations={entry.allocations}
						currency={history.currency}
						testid={`portal-payment-allocations-${entry.tenantLedgerEntryId}`}
					/>
				{/if}
			</div>
			<div class="flex justify-between gap-4 md:block md:text-right">
				<span class="text-sm text-muted-foreground md:hidden">Amount</span>
				<span class="font-medium tabular-nums {accountingAmountClass(entry.signedAmount)}">{formatAccountingCurrency(entry.signedAmount, history.currency)}</span>
			</div>
			<div class="flex justify-between gap-4 md:block md:text-right">
				<span class="text-sm text-muted-foreground md:hidden">Balance</span>
				<span class="tabular-nums {accountingAmountClass(entry.runningBalance)}">{formatAccountingCurrency(entry.runningBalance, history.currency)}</span>
			</div>
			<div class="md:text-right">
				{#if entry.payable}
					<Button size="sm" disabled={onlinePaymentsUnavailable || (payPending && payingId === entry.tenantLedgerEntryId)} onclick={() => onpay(entry)} data-testid="portal-payment-pay-now">
						{payPending && payingId === entry.tenantLedgerEntryId ? 'Opening…' : 'Pay now'}
					</Button>
				{/if}
			</div>
		</div>
		{#if index === history.items.length - 1 || monthKey(history.items[index + 1].effectiveOn) !== monthKey(entry.effectiveOn)}
			<div class="flex items-center justify-between rounded-b-xl border border-t-0 border-border bg-muted/10 px-4 py-3 text-sm font-medium" data-testid="portal-history-month-total">
				<span>Month-end balance</span>
				<span class="tabular-nums {accountingAmountClass(entry.runningBalance)}">{formatAccountingCurrency(entry.runningBalance, history.currency)}</span>
			</div>
		{/if}
	{:else}
		<p class="border-b border-border py-8 text-center text-sm text-muted-foreground">No account activity in this period.</p>
	{/each}
</div>
