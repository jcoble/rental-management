<script lang="ts">
	import * as Table from '$lib/components/ui/table';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { ACCOUNTING_HELP } from '$lib/accounting/accounting-help';
	import {
		accountingAmountClass,
		formatAccountingCurrency,
		formatStatementStatusLabel,
		formatStatementTotalLabel
	} from '$lib/accounting/accounting-display';

	export type FinancialStatementRow = {
		label: string;
		amount: number | null;
		indent?: boolean;
	};

	export type FinancialStatementSection = {
		label: string;
		rows: FinancialStatementRow[];
		total: number | null;
	};

	export type FinancialStatementTotal = {
		label: string;
		amount: number | null;
	};

	export type FinancialStatementStatus = {
		balanced: boolean;
		message: string;
	};

	let {
		sections,
		grandTotal,
		status,
		loading = false,
		authorized = true,
		currency = 'USD'
	}: {
		sections: FinancialStatementSection[];
		grandTotal?: FinancialStatementTotal;
		status?: FinancialStatementStatus;
		loading?: boolean;
		authorized?: boolean;
		currency?: string;
	} = $props();

</script>

{#if authorized}
	<div class="overflow-hidden rounded-xl border border-border bg-card" data-testid="financial-statement-table">
		<div class="flex items-center justify-end gap-1.5 border-b border-border px-4 py-2 text-xs font-medium text-muted-foreground print:hidden">
			<span>About this statement</span>
			<HelpPopover
				title={ACCOUNTING_HELP.financialStatements.title}
				summary={ACCOUNTING_HELP.financialStatements.summary}
				learnMoreUrl={ACCOUNTING_HELP.financialStatements.href}
				testid="financial-statement-help"
			/>
		</div>
		{#if loading}
			<div class="space-y-4 p-4" role="status" aria-label="Loading financial statement">
				<div class="h-5 w-40 animate-pulse rounded bg-muted"></div>
				{#each Array(5) as _, index}
					<div class="flex items-center justify-between gap-4" data-testid={`financial-statement-loading-row-${index}`}>
						<div class="h-4 {index % 2 === 0 ? 'w-2/5' : 'w-1/3'} animate-pulse rounded bg-muted"></div>
						<div class="h-4 w-24 animate-pulse rounded bg-muted"></div>
					</div>
				{/each}
			</div>
		{:else}
			<Table.Root>
				<Table.Body>
					{#each sections as section (section.label)}
						<Table.Row class="border-b-0 bg-muted/30 hover:bg-muted/30">
							<Table.Cell colspan={2} class="px-4 pb-2 pt-4 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
								{section.label}
							</Table.Cell>
						</Table.Row>
						{#each section.rows as row (row.label)}
							<Table.Row>
								<Table.Cell class={row.indent ? 'pl-8' : 'pl-4'}>{row.label}</Table.Cell>
								<Table.Cell class={`w-40 px-4 text-right font-mono tabular-nums ${accountingAmountClass(row.amount)}`}>
									{formatAccountingCurrency(row.amount, currency)}
								</Table.Cell>
							</Table.Row>
						{/each}
						<Table.Row class="font-medium">
							<Table.Cell class="pl-4">{formatStatementTotalLabel(section.label)}</Table.Cell>
							<Table.Cell class={`px-4 text-right font-mono tabular-nums ${accountingAmountClass(section.total)}`}>
								{formatAccountingCurrency(section.total, currency)}
							</Table.Cell>
						</Table.Row>
					{/each}
				</Table.Body>
				{#if grandTotal}
					<Table.Footer>
						<Table.Row>
							<Table.Cell class="px-4 font-semibold">{grandTotal.label}</Table.Cell>
							<Table.Cell class={`px-4 text-right font-mono font-semibold tabular-nums ${accountingAmountClass(grandTotal.amount)}`}>
								{formatAccountingCurrency(grandTotal.amount, currency)}
							</Table.Cell>
						</Table.Row>
					</Table.Footer>
				{/if}
			</Table.Root>

			{#if status}
				<div
					class={`flex items-start gap-2 border-t border-border px-4 py-3 text-sm ${status.balanced ? 'text-foreground' : 'text-destructive'}`}
					role="status"
					data-balanced={status.balanced}
				>
					<span class="font-semibold">{formatStatementStatusLabel(status.balanced)}</span>
					<span>{status.message}</span>
				</div>
			{/if}
		{/if}
	</div>
{/if}
