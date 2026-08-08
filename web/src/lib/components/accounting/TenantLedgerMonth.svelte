<script lang="ts">
	import { MoreHorizontal } from '@lucide/svelte';
	import type { TenantLedgerRow, TenantMonthSummary } from '$lib/api/endpoints/tenant-ledgers';
	import {
		accountingAmountClass,
		formatAccountingCurrency,
		formatAccountingDate
	} from '$lib/accounting/accounting-display';
	import { formatMoneyCategoryLabel, formatMoneyEntryLabel } from '$lib/accounting/money-display';
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

	function isCredit(row: TenantLedgerRow): boolean {
		return row.type === 'Credit' || row.creditAmount !== 0;
	}

	function displayLabel(row: TenantLedgerRow): string {
		return row.description || formatMoneyEntryLabel(row.type) || formatMoneyCategoryLabel(row.categoryName);
	}

	function action(row: TenantLedgerRow, next: TenantLedgerRowAction): void {
		onaction?.(row, next);
	}
</script>

<section class="overflow-hidden rounded-xl border border-border bg-card" data-testid={`tenant-ledger-month-${summary.year}-${summary.month}`}>
	<div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-2 border-b border-border bg-muted/20 px-4 py-3">
		<h3 class="font-semibold">{monthLabel}</h3>
		<div class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
			<span>Opening <strong class="font-mono font-medium tabular-nums">{formatAccountingCurrency(summary.openingBalance, summary.currency)}</strong></span>
			<span>Charges <strong class="font-mono font-medium tabular-nums">{formatAccountingCurrency(summary.chargeAmount, summary.currency)}</strong></span>
			<span>Payments <strong class="font-mono font-medium tabular-nums">{formatAccountingCurrency(summary.paymentAmount, summary.currency)}</strong></span>
			<span>Closing <strong class="font-mono font-medium tabular-nums">{formatAccountingCurrency(summary.closingBalance, summary.currency)}</strong></span>
		</div>
	</div>

	<Table.Root>
		<Table.Header>
			<Table.Row class="bg-muted/10 hover:bg-muted/10">
				<Table.Head class="w-28 px-4 py-2 text-xs">Date</Table.Head>
				<Table.Head class="min-w-52 px-4 py-2 text-xs">What happened</Table.Head>
				<Table.Head class="w-28 px-4 py-2 text-xs">Due</Table.Head>
				<Table.Head class="w-32 px-4 py-2 text-right text-xs">Charge</Table.Head>
				<Table.Head class="w-36 px-4 py-2 text-right text-xs">Payment / credit</Table.Head>
				<Table.Head class="w-32 px-4 py-2 text-right text-xs">Balance</Table.Head>
				<Table.Head class="w-12 px-2 py-2"><span class="sr-only">Actions</span></Table.Head>
			</Table.Row>
		</Table.Header>
		<Table.Body>
			{#each rows as row (row.tenantLedgerEntryId)}
				<Table.Row data-testid={`tenant-ledger-row-${row.tenantLedgerEntryId}`}>
					<Table.Cell class="whitespace-nowrap px-4 py-3 text-xs text-muted-foreground">{formatAccountingDate(row.effectiveOn)}</Table.Cell>
					<Table.Cell class="px-4 py-3">
						<div class="min-w-0">
							<p class="truncate font-medium">{displayLabel(row)}</p>
							{#if row.paymentMethod || row.categoryName || row.recurringScheduleContext}
								<p class="mt-1 truncate text-xs text-muted-foreground">
									{row.paymentMethod ?? formatMoneyCategoryLabel(row.categoryName)}{#if row.recurringScheduleContext} · {row.recurringScheduleContext}{/if}
								</p>
							{/if}
						</div>
					</Table.Cell>
					<Table.Cell class="whitespace-nowrap px-4 py-3 text-xs text-muted-foreground">{formatAccountingDate(row.dueOn)}</Table.Cell>
					<Table.Cell class={`px-4 py-3 text-right font-mono tabular-nums ${accountingAmountClass(row.chargeAmount)}`}>
						{#if isCharge(row)}{formatAccountingCurrency(row.chargeAmount, row.currency)}{:else}—{/if}
					</Table.Cell>
					<Table.Cell class="px-4 py-3 text-right font-mono tabular-nums">
						{#if isPayment(row)}<span class={accountingAmountClass(row.paymentAmount)}>{formatAccountingCurrency(row.paymentAmount, row.currency)}</span>{:else if isCredit(row)}<span class={accountingAmountClass(row.creditAmount)}>{formatAccountingCurrency(row.creditAmount, row.currency)}</span>{:else}—{/if}
					</Table.Cell>
					<Table.Cell class={`px-4 py-3 text-right font-mono font-medium tabular-nums ${accountingAmountClass(row.runningAmountOwed)}`}>
						{formatAccountingCurrency(row.runningAmountOwed, row.currency)}
					</Table.Cell>
					<Table.Cell class="px-2 py-3 text-right">
						<DropdownMenu.Root>
							<DropdownMenu.Trigger aria-label={`Actions for ${displayLabel(row)}`} class="rounded-md p-2 text-muted-foreground hover:bg-muted hover:text-foreground">
								<MoreHorizontal class="size-4" />
							</DropdownMenu.Trigger>
							<DropdownMenu.Content align="end">
								<DropdownMenu.Item onSelect={() => action(row, 'view')}>View detail</DropdownMenu.Item>
								{#if isCharge(row)}
									<DropdownMenu.Item onSelect={() => action(row, 'give-credit')}>Give credit</DropdownMenu.Item>
									<DropdownMenu.Item onSelect={() => action(row, 'related-charge')}>Add related charge</DropdownMenu.Item>
									<DropdownMenu.Item onSelect={() => action(row, 'fix-charge')}>Reverse charge</DropdownMenu.Item>
								{:else if isPayment(row)}
									<DropdownMenu.Item onSelect={() => action(row, 'fix-payment')}>Fix this payment</DropdownMenu.Item>
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
