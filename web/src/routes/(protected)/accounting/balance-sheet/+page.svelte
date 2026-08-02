<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { createQuery } from '@tanstack/svelte-query';
	import { Printer } from '@lucide/svelte';

	import {
		accountingBooks,
		type FinancialStatementResponse
	} from '$lib/api/endpoints/accounting-books';
	import AccountingDetailMode from '$lib/components/accounting/AccountingDetailMode.svelte';
	import FinancialStatementTable from '$lib/components/accounting/FinancialStatementTable.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { Button } from '$lib/components/ui/button';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { apiErrorMessage } from '$lib/utils/toast';
	import {
		buildAsOfQueryString,
		getTodayIsoDate,
		isIsoDate,
		readAsOfState
	} from '$lib/accounting/statement-state';

	type StatementTableSection = {
		label: string;
		rows: { label: string; amount: number | null; indent?: boolean }[];
		total: number | null;
	};

	type StatementTableTotal = {
		label: string;
		amount: number | null;
	};

	type StatementStatus = {
		balanced: boolean;
		message: string;
	};

	const authState = getAuthState();
	const initialAsOf = readAsOfState(page.url.searchParams);
	let to = $state(initialAsOf.to);

	const asOfValid = $derived(isIsoDate(to));

	$effect(() => {
		if (!asOfValid) return;
		const query = buildAsOfQueryString({ to }, page.url.searchParams);
		const current = page.url.searchParams.toString();
		if (query !== current) {
			void goto(query ? `${page.url.pathname}?${query}` : page.url.pathname, {
				replaceState: true,
				noScroll: true,
				keepFocus: true
			});
		}
	});

	const balanceSheetQuery = createQuery(() => ({
		queryKey: ['accounting-balance-sheet', to],
		enabled: authState.isAuthenticated && asOfValid,
		queryFn: () => accountingBooks.balanceSheet({ to })
	}));

	function errorStatus(error: unknown): number | undefined {
		if (!error || typeof error !== 'object' || !('status' in error)) return undefined;
		const status = (error as { status?: unknown }).status;
		return typeof status === 'number' ? status : undefined;
	}

	const statement = $derived(balanceSheetQuery.data);
	const unauthorized = $derived(
		!authState.isLoading &&
			(!authState.isAuthenticated || [401, 403, 404].includes(errorStatus(balanceSheetQuery.error) ?? 0))
	);
	const loading = $derived(authState.isLoading || (authState.isAuthenticated && balanceSheetQuery.isLoading));
	const sections = $derived(toBalanceSheetSections(statement));
	const currency = $derived(firstCurrency(statement));
	const grandTotal = $derived.by<StatementTableTotal | undefined>(() =>
		statement
			? { label: 'Total liabilities and equity', amount: statement.totals.liabilitiesAndEquity }
			: undefined
	);
	const status = $derived.by<StatementStatus | undefined>(() => {
		const balanced = statement?.totals.isBalanced;
		if (balanced == null) return undefined;
		return {
			balanced,
			message: balanced ? '✓ Balanced' : '⚠ Out of balance — contact support'
		};
	});

	function toBalanceSheetSections(response: FinancialStatementResponse | undefined): StatementTableSection[] {
		if (!response) return [];

		const sectionsByLabel = new Map(response.sections.map((section) => [section.label.toLowerCase(), section]));
		const sections: StatementTableSection[] = ['Assets', 'Liabilities', 'Equity'].map((label) => {
			const section = sectionsByLabel.get(label.toLowerCase());
			return section
				? {
						label,
						rows: section.rows.map((row) => ({ label: row.accountName, amount: row.amount, indent: true })),
						total: section.subtotal
					}
				: { label, rows: [], total: null };
		});
		const currentEarningsRow = {
			label: 'Current earnings (not yet closed)',
			amount: response.totals.currentEarnings,
			indent: true
		};
		const equity = sections[2];
		if (equity) equity.rows = [...equity.rows, currentEarningsRow];

		return sections;
	}

	function firstCurrency(response: FinancialStatementResponse | undefined): string {
		for (const section of response?.sections ?? []) {
			const currency = section.rows[0]?.currency;
			if (currency) return currency;
		}
		return 'USD';
	}

	function hasStatementRows(response: FinancialStatementResponse): boolean {
		return response.sections.some((section) => section.rows.length > 0);
	}

	function setAsOfDate(value: string): void {
		to = value || getTodayIsoDate();
	}

	function printStatement(): void {
		window.print();
	}
</script>

<svelte:head>
	<title>Balance sheet - Rental Command</title>
</svelte:head>

<AccountingDetailMode class="mb-4 print:contents" testid="balance-sheet-detail-mode">
	<div class="box-border h-full overflow-y-auto p-6 pb-20 print:overflow-visible print:p-0" data-testid="balance-sheet-page">
		<PageHeader
			class="mb-6 print:mb-4"
			band
			art={11}
			tone="sky"
			eyebrow="Reports"
			title="Balance sheet"
			description="See what the workspace owns, owes, and has built up as of a single date."
			data-testid="balance-sheet-header"
		>
			{#snippet actions()}
				<div class="flex flex-wrap items-end gap-3 print:hidden">
					<label class="grid w-44 gap-1 text-sm" for="balance-sheet-as-of">
						<span class="text-xs font-medium text-muted-foreground">As of</span>
						<DatePicker
							id="balance-sheet-as-of"
							testid="balance-sheet-as-of-input"
							bind:value={to}
							onchange={setAsOfDate}
						/>
					</label>
					<Button variant="outline" class="gap-2" onclick={printStatement} data-testid="balance-sheet-print">
						<Printer class="size-4" aria-hidden="true" />
						Print
					</Button>
				</div>
			{/snippet}
		</PageHeader>

		{#if !asOfValid}
			<div class="mb-4 rounded-xl border border-destructive/40 bg-destructive/5 p-4 text-sm text-destructive" role="alert" data-testid="balance-sheet-date-error">
				Choose a valid as-of date.
			</div>
		{:else if unauthorized}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="balance-sheet-unauthorized">
				<p class="font-medium text-destructive">You do not have access to this statement.</p>
				<p class="mt-1 text-sm text-muted-foreground">Ask a workspace administrator to grant accounting access.</p>
			</div>
		{:else if balanceSheetQuery.isError}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="balance-sheet-error">
				<p class="font-medium text-destructive">Could not load the balance sheet.</p>
				<p class="mt-1 text-sm text-muted-foreground">{apiErrorMessage(balanceSheetQuery.error, 'The balance sheet is temporarily unavailable.')}</p>
				<Button class="mt-4" variant="outline" onclick={() => balanceSheetQuery.refetch()} data-testid="balance-sheet-retry">Try again</Button>
			</div>
		{:else if loading}
			<FinancialStatementTable sections={[]} loading authorized={!unauthorized} currency="USD" />
		{:else if statement && !hasStatementRows(statement)}
			<div class="rounded-xl border border-border bg-card p-8 text-center text-sm text-muted-foreground" data-testid="balance-sheet-empty">
				No accounting activity for this date yet.
			</div>
		{:else}
			<FinancialStatementTable {sections} {grandTotal} authorized={!unauthorized} currency={currency} />
			{#if status}
				<div
					class={`mt-3 rounded-xl border px-4 py-3 text-sm ${status.balanced ? 'border-success/40 bg-success/5 text-foreground' : 'border-warning/40 bg-warning/5 text-warning'}`}
					role="status"
					data-testid="balance-sheet-status"
					data-balanced={status.balanced}
				>
					{status.message}
				</div>
			{/if}
		{/if}
	</div>
</AccountingDetailMode>

<style>
	@media print {
		:global([data-testid='balance-sheet-detail-mode'] > span),
		:global([data-testid='balance-sheet-detail-mode'] > button) {
			display: none;
		}
	}
</style>
