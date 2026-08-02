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
		buildDateRangeQueryString,
		isIsoDate,
		readDateRangeState
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

	const authState = getAuthState();
	const initialRange = readDateRangeState(page.url.searchParams);
	let from = $state(initialRange.from);
	let to = $state(initialRange.to);

	const dateRangeValid = $derived(isIsoDate(from) && isIsoDate(to) && from <= to);

	$effect(() => {
		if (!dateRangeValid) return;
		const query = buildDateRangeQueryString({ from, to }, page.url.searchParams);
		const current = page.url.searchParams.toString();
		if (query !== current) {
			void goto(query ? `${page.url.pathname}?${query}` : page.url.pathname, {
				replaceState: true,
				noScroll: true,
				keepFocus: true
			});
		}
	});

	const statementQuery = createQuery(() => ({
		queryKey: ['accounting-income-statement', from, to],
		enabled: authState.isAuthenticated && dateRangeValid,
		queryFn: () => accountingBooks.incomeStatement({ from, to })
	}));

	function errorStatus(error: unknown): number | undefined {
		if (!error || typeof error !== 'object' || !('status' in error)) return undefined;
		const status = (error as { status?: unknown }).status;
		return typeof status === 'number' ? status : undefined;
	}

	const statement = $derived(statementQuery.data);
	const unauthorized = $derived(
		!authState.isLoading &&
			(!authState.isAuthenticated || [401, 403, 404].includes(errorStatus(statementQuery.error) ?? 0))
	);
	const loading = $derived(authState.isLoading || (authState.isAuthenticated && statementQuery.isLoading));
	const sections = $derived(toTableSections(statement));
	const currency = $derived(firstCurrency(statement));
	const grandTotal = $derived.by<StatementTableTotal | undefined>(() =>
		statement
			? { label: 'Net income', amount: statement.totals.netIncome }
			: undefined
	);

	function toTableSections(response: FinancialStatementResponse | undefined): StatementTableSection[] {
		return (response?.sections ?? []).map((section) => ({
			label: section.label,
			rows: section.rows.map((row) => ({ label: row.accountName, amount: row.amount, indent: true })),
			total: section.subtotal
		}));
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

	function printStatement(): void {
		window.print();
	}
</script>

<svelte:head>
	<title>Profit &amp; loss - Rental Command</title>
</svelte:head>

<AccountingDetailMode class="mb-4 print:contents" testid="profit-and-loss-detail-mode">
	<div class="box-border h-full overflow-y-auto p-6 pb-20 print:overflow-visible print:p-0" data-testid="profit-and-loss-page">
		<PageHeader
			class="mb-6 print:mb-4"
			band
			art={10}
			tone="mint"
			eyebrow="Reports"
			title="Profit &amp; loss"
			description="See the income and expenses recorded for a selected period."
			data-testid="profit-and-loss-header"
		>
			{#snippet actions()}
				<div class="flex flex-wrap items-end gap-3 print:hidden">
					<label class="grid w-44 gap-1 text-sm" for="profit-and-loss-from">
						<span class="text-xs font-medium text-muted-foreground">From</span>
						<DatePicker id="profit-and-loss-from" testid="profit-and-loss-from-input" bind:value={from} />
					</label>
					<label class="grid w-44 gap-1 text-sm" for="profit-and-loss-to">
						<span class="text-xs font-medium text-muted-foreground">To</span>
						<DatePicker id="profit-and-loss-to" testid="profit-and-loss-to-input" bind:value={to} />
					</label>
					<Button variant="outline" class="gap-2" onclick={printStatement} data-testid="profit-and-loss-print">
						<Printer class="size-4" aria-hidden="true" />
						Print
					</Button>
				</div>
			{/snippet}
		</PageHeader>

		{#if !dateRangeValid}
			<div class="mb-4 rounded-xl border border-destructive/40 bg-destructive/5 p-4 text-sm text-destructive" role="alert" data-testid="profit-and-loss-date-error">
				From must be on or before To.
			</div>
		{:else if unauthorized}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="profit-and-loss-unauthorized">
				<p class="font-medium text-destructive">You do not have access to this statement.</p>
				<p class="mt-1 text-sm text-muted-foreground">Ask a workspace administrator to grant accounting access.</p>
			</div>
		{:else if statementQuery.isError}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="profit-and-loss-error">
				<p class="font-medium text-destructive">Could not load the profit &amp; loss statement.</p>
				<p class="mt-1 text-sm text-muted-foreground">{apiErrorMessage(statementQuery.error, 'The statement is temporarily unavailable.')}</p>
				<Button class="mt-4" variant="outline" onclick={() => statementQuery.refetch()} data-testid="profit-and-loss-retry">Try again</Button>
			</div>
		{:else if loading}
			<FinancialStatementTable sections={[]} loading currency="USD" />
		{:else if statement && !hasStatementRows(statement)}
			<div class="rounded-xl border border-border bg-card p-8 text-center text-sm text-muted-foreground" data-testid="profit-and-loss-empty">
				No accounting activity for these filters yet.
			</div>
		{:else}
			<FinancialStatementTable
				{sections}
				{grandTotal}
				currency={currency}
			/>
		{/if}
	</div>
</AccountingDetailMode>

<style>
	@media print {
		:global([data-testid='profit-and-loss-detail-mode'] > span),
		:global([data-testid='profit-and-loss-detail-mode'] > button) {
			display: none;
		}
	}
</style>
