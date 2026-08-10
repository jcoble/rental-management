<script lang="ts">
	import { MoreHorizontal } from '@lucide/svelte';
	import type { TenantLedgerRow, TenantMonthSummary } from '$lib/api/endpoints/tenant-ledgers';
	import {
		accountingAmountClass,
		formatAccountingCurrency,
		formatAccountingDate
	} from '$lib/accounting/accounting-display';
	import { formatMoneyCategoryLabel, formatMoneyEntryLabel } from '$lib/accounting/money-display';
	import { tenantMonthSummaryReconciles } from '$lib/accounting/tenant-ledger-summary';
	import { getAccountingDetailMode } from './AccountingDetailMode.svelte';
	import TenantPaymentAllocationDetails from './TenantPaymentAllocationDetails.svelte';
	import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
	import * as Table from '$lib/components/ui/table';

	export type TenantLedgerRowAction =
		| 'view'
		| 'give-credit'
		| 'related-charge'
		| 'fix-charge'
		| 'fix-payment';

	let {
		summary,
		rows = [],
		onaction
	}: {
		summary: TenantMonthSummary;
		rows?: TenantLedgerRow[];
		onaction?: (row: TenantLedgerRow, action: TenantLedgerRowAction) => void;
	} = $props();

	const detailMode = getAccountingDetailMode();
	const advanced = $derived((detailMode?.mode ?? 'simple') === 'advanced');
	const summaryReconciles = $derived(tenantMonthSummaryReconciles(summary));

	const monthLabel = $derived(
		new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(
			new Date(Date.UTC(summary.year, summary.month - 1, 1))
		)
	);

	function isPayment(row: TenantLedgerRow): boolean {
		return row.type === 'PaymentReceipt' || row.paymentAmount !== 0;
	}

	function isCharge(row: TenantLedgerRow): boolean {
		return row.type !== 'DepositCharge' && row.chargeAmount !== 0;
	}

	function canReverseCharge(row: TenantLedgerRow): boolean {
		return isCharge(row)
			&& ['RentCharge', 'AddendumCharge', 'LateFeeCharge', 'ManualCharge', 'OpeningBalance'].includes(row.type)
			&& row.reversesEntryId == null
			&& row.replacedByEntryId == null;
	}

	function isCredit(row: TenantLedgerRow): boolean {
		return row.type === 'Credit' || row.creditAmount !== 0;
	}

	function displayLabel(row: TenantLedgerRow): string {
		return row.description || formatMoneyEntryLabel(row.type) || formatMoneyCategoryLabel(row.categoryName);
	}

	function action(row: TenantLedgerRow, next: TenantLedgerRowAction): void {
		onaction?.(row, next);
	}

	function isOverdue(row: TenantLedgerRow): boolean {
		return isCharge(row) && row.openAmount > 0 && Boolean(row.dueOn) && row.dueOn! < new Date().toISOString().slice(0, 10);
	}

	function openRow(row: TenantLedgerRow): void {
		action(row, 'view');
	}
</script>

<section class="overflow-hidden rounded-xl border border-border bg-card" data-testid={`tenant-ledger-month-${summary.year}-${summary.month}`}>
	<div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-2 border-b border-border bg-muted/20 px-4 py-3">
		<h3 class="font-semibold">{monthLabel}</h3>
		<div class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
			<span>Opening <strong class="font-mono font-medium tabular-nums">{formatAccountingCurrency(summary.openingBalance, summary.currency)}</strong></span>
			<span>Charges <strong class="font-mono font-medium tabular-nums">{formatAccountingCurrency(summary.chargeAmount, summary.currency)}</strong></span>
			<span>Payments <strong class="font-mono font-medium tabular-nums">{formatAccountingCurrency(summary.paymentAmount, summary.currency)}</strong></span>
			<span data-testid="tenant-ledger-summary-credits">Credits <strong class="font-mono font-medium tabular-nums">{formatAccountingCurrency(summary.creditAmount, summary.currency)}</strong></span>
			<span>Closing <strong class="font-mono font-medium tabular-nums">{formatAccountingCurrency(summary.closingBalance, summary.currency)}</strong></span>
			{#if !summaryReconciles}
				<span
					class="text-destructive"
					role="status"
					data-testid="tenant-ledger-summary-reconciliation-warning"
				>
					Needs review: month totals do not match.
				</span>
			{/if}
		</div>
	</div>

	<Table.Root>
		<Table.Header>
			<Table.Row class="bg-muted/10 hover:bg-muted/10">
				<Table.Head class="w-28 px-4 py-2 text-xs">Date</Table.Head>
				<Table.Head class="w-28 px-4 py-2 text-xs">Due date</Table.Head>
				<Table.Head class="min-w-52 px-4 py-2 text-xs">What happened</Table.Head>
				<Table.Head class="w-32 px-4 py-2 text-right text-xs">Charge</Table.Head>
				<Table.Head class="w-36 px-4 py-2 text-right text-xs">Payment / credit</Table.Head>
				<Table.Head class="w-32 px-4 py-2 text-right text-xs">Balance</Table.Head>
				<Table.Head class="w-12 px-2 py-2"><span class="sr-only">Actions</span></Table.Head>
			</Table.Row>
		</Table.Header>
		<Table.Body>
			{#each rows as row (row.tenantLedgerEntryId)}
				<Table.Row
					data-testid={`tenant-ledger-row-${row.tenantLedgerEntryId}`}
					class="cursor-pointer hover:bg-muted/40"
					role="button"
					tabindex={0}
					aria-label={`Open ${displayLabel(row)}`}
					onclick={() => openRow(row)}
					onkeydown={(event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); openRow(row); } }}
				>
					<Table.Cell class="whitespace-nowrap px-4 py-3 text-xs text-muted-foreground">{formatAccountingDate(row.effectiveOn)}</Table.Cell>
					<Table.Cell class="whitespace-nowrap px-4 py-3 text-xs text-muted-foreground">{formatAccountingDate(row.dueOn)}</Table.Cell>
					<Table.Cell class="px-4 py-3">
						<div class="min-w-0">
							<div class="flex min-w-0 flex-wrap items-center gap-2">
								<p class="truncate font-medium">{displayLabel(row)}</p>
								{#if isCharge(row) && row.openAmount > 0}
									<span class={`rounded-full border px-1.5 py-0.5 text-[11px] font-medium ${isOverdue(row) ? 'border-destructive/40 bg-destructive/10 text-destructive' : 'border-primary/40 bg-primary/10 text-primary'}`} data-testid={`tenant-ledger-status-${row.tenantLedgerEntryId}`}>
										{isOverdue(row) ? 'Overdue' : 'Open'}
									</span>
								{/if}
							</div>
							{#if row.paymentMethod || row.categoryName || row.recurringScheduleContext}
								<p class="mt-1 truncate text-xs text-muted-foreground">
									{row.paymentMethod ?? formatMoneyCategoryLabel(row.categoryName)}{#if row.recurringScheduleContext} · {row.recurringScheduleContext}{/if}
								</p>
							{/if}
							{#if advanced}
								<div class="mt-2 flex flex-wrap gap-x-3 gap-y-1 text-[11px] text-muted-foreground" data-testid={`tenant-ledger-advanced-facts-${row.tenantLedgerEntryId}`}>
									<span class="font-medium text-foreground">Bookkeeping</span>
									{#if row.accountLabel}<span>{row.accountLabel}</span>{/if}
									{#if row.categoryName}<span>{row.categoryName}</span>{/if}
									{#if row.sourceType}<span>Source: {row.sourceType}</span>{/if}
									{#if row.journalEntryPublicId}<span>Journal: {row.journalEntryPublicId}</span>{/if}
								</div>
							{/if}
							{#if isPayment(row)}
								<TenantPaymentAllocationDetails
									allocations={row.allocations}
									currency={row.currency}
									testid={`tenant-ledger-payment-allocations-${row.tenantLedgerEntryId}`}
								/>
							{/if}
						</div>
					</Table.Cell>
					<Table.Cell class={`px-4 py-3 text-right font-mono tabular-nums ${accountingAmountClass(row.chargeAmount)}`}>
						{#if isCharge(row)}{formatAccountingCurrency(row.chargeAmount, row.currency)}{:else}—{/if}
					</Table.Cell>
					<Table.Cell class="px-4 py-3 text-right font-mono tabular-nums">
						{#if isPayment(row)}<span class={accountingAmountClass(row.paymentAmount)}>{formatAccountingCurrency(row.paymentAmount, row.currency)}</span>{:else if isCredit(row)}<span class={accountingAmountClass(row.creditAmount)}>{formatAccountingCurrency(row.creditAmount, row.currency)}</span>{:else}—{/if}
					</Table.Cell>
					<Table.Cell class={`px-4 py-3 text-right font-mono font-medium tabular-nums ${accountingAmountClass(row.runningAmountOwed)}`}>
						{formatAccountingCurrency(row.runningAmountOwed, row.currency)}
					</Table.Cell>
					<Table.Cell class="px-2 py-3 text-right" onclick={(event) => event.stopPropagation()} onkeydown={(event) => event.stopPropagation()}>
						<DropdownMenu.Root>
							<DropdownMenu.Trigger aria-label={`Actions for ${displayLabel(row)}`} class="rounded-md p-2 text-muted-foreground hover:bg-muted hover:text-foreground" data-testid={`tenant-ledger-actions-trigger-${row.tenantLedgerEntryId}`}>
								<MoreHorizontal class="size-4" />
							</DropdownMenu.Trigger>
							<DropdownMenu.Content align="end">
								<DropdownMenu.Item onSelect={() => action(row, 'view')} data-testid={`tenant-ledger-action-view-${row.tenantLedgerEntryId}`}>View detail</DropdownMenu.Item>
								{#if isCharge(row)}
									<DropdownMenu.Item onSelect={() => action(row, 'give-credit')} data-testid={`tenant-ledger-action-give-credit-${row.tenantLedgerEntryId}`}>Give credit</DropdownMenu.Item>
									<DropdownMenu.Item onSelect={() => action(row, 'related-charge')} data-testid={`tenant-ledger-action-related-charge-${row.tenantLedgerEntryId}`}>Add related charge</DropdownMenu.Item>
									{#if canReverseCharge(row)}
										<DropdownMenu.Item onSelect={() => action(row, 'fix-charge')} data-testid={`tenant-ledger-action-fix-charge-${row.tenantLedgerEntryId}`}>Reverse charge</DropdownMenu.Item>
									{/if}
								{:else if isPayment(row)}
									<DropdownMenu.Item onSelect={() => action(row, 'fix-payment')} data-testid={`tenant-ledger-action-fix-payment-${row.tenantLedgerEntryId}`}>Review payment allocation</DropdownMenu.Item>
								{/if}
							</DropdownMenu.Content>
						</DropdownMenu.Root>
					</Table.Cell>
				</Table.Row>
			{/each}
			{#if rows.length === 0}
				<Table.Row>
					<Table.Cell colspan={7} class="px-4 py-6 text-center text-sm text-muted-foreground">No entries in this month.</Table.Cell>
				</Table.Row>
			{/if}
		</Table.Body>
	</Table.Root>
</section>
