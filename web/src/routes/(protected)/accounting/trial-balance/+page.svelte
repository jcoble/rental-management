<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { createQuery } from '@tanstack/svelte-query';
	import { Printer } from '@lucide/svelte';

	import {
		accountingBooks,
		type TrialBalanceResponse
	} from '$lib/api/endpoints/accounting-books';
	import AccountingDetailMode from '$lib/components/accounting/AccountingDetailMode.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Table from '$lib/components/ui/table';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import {
		accountingAmountClass,
		formatAccountingCurrency
	} from '$lib/accounting/accounting-display';
	import { apiErrorMessage } from '$lib/utils/toast';
	import {
		buildAsOfQueryString,
		getTodayIsoDate,
		isIsoDate,
		readAsOfState
	} from '$lib/accounting/statement-state';

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

	const trialBalanceQuery = createQuery(() => ({
		queryKey: ['accounting-trial-balance', to],
		enabled: authState.isAuthenticated && asOfValid,
		queryFn: () => accountingBooks.trialBalance({ to })
	}));

	function errorStatus(error: unknown): number | undefined {
		if (!error || typeof error !== 'object' || !('status' in error)) return undefined;
		const status = (error as { status?: unknown }).status;
		return typeof status === 'number' ? status : undefined;
	}

	const trialBalance = $derived(trialBalanceQuery.data);
	const unauthorized = $derived(
		!authState.isLoading &&
			(!authState.isAuthenticated || [401, 403, 404].includes(errorStatus(trialBalanceQuery.error) ?? 0))
	);
	const loading = $derived(authState.isLoading || (authState.isAuthenticated && trialBalanceQuery.isLoading));
	const currency = $derived(firstCurrency(trialBalance));
	const status = $derived.by<StatementStatus | undefined>(() => {
		const balanced = trialBalance?.isBalanced;
		if (balanced == null) return undefined;
		return {
			balanced,
			message: balanced ? '✓ Balanced' : '⚠ Out of balance — contact support'
		};
	});

	function firstCurrency(response: TrialBalanceResponse | undefined): string {
		return response?.rows[0]?.currency ?? 'USD';
	}

	function setAsOfDate(value: string): void {
		to = value || getTodayIsoDate();
	}

	function printStatement(): void {
		window.print();
	}
</script>

<svelte:head>
	<title>Trial balance - Rental Command</title>
</svelte:head>

<AccountingDetailMode class="mb-4 print:contents" testid="trial-balance-detail-mode">
	<div class="box-border h-full overflow-y-auto p-6 pb-20 print:overflow-visible print:p-0" data-testid="trial-balance-page">
		<PageHeader
			class="mb-6 print:mb-4"
			band
			art={12}
			tone="violet"
			eyebrow="Reports"
			title="Trial balance"
			description="Check the ending balance of every account as of a single date."
			data-testid="trial-balance-header"
		>
			{#snippet actions()}
				<div class="flex flex-wrap items-end gap-3 print:hidden">
					<label class="grid w-44 gap-1 text-sm" for="trial-balance-as-of">
						<span class="text-xs font-medium text-muted-foreground">As of</span>
						<DatePicker
							id="trial-balance-as-of"
							testid="trial-balance-as-of-input"
							bind:value={to}
							onchange={setAsOfDate}
						/>
					</label>
					<Button variant="outline" class="gap-2" onclick={printStatement} data-testid="trial-balance-print">
						<Printer class="size-4" aria-hidden="true" />
						Print
					</Button>
				</div>
			{/snippet}
		</PageHeader>

		{#if !asOfValid}
			<div class="mb-4 rounded-xl border border-destructive/40 bg-destructive/5 p-4 text-sm text-destructive" role="alert" data-testid="trial-balance-date-error">
				Choose a valid as-of date.
			</div>
		{:else if unauthorized}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="trial-balance-unauthorized">
				<p class="font-medium text-destructive">You do not have access to this statement.</p>
				<p class="mt-1 text-sm text-muted-foreground">Ask a workspace administrator to grant accounting access.</p>
			</div>
		{:else if trialBalanceQuery.isError}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="trial-balance-error">
				<p class="font-medium text-destructive">Could not load the trial balance.</p>
				<p class="mt-1 text-sm text-muted-foreground">{apiErrorMessage(trialBalanceQuery.error, 'The trial balance is temporarily unavailable.')}</p>
				<Button class="mt-4" variant="outline" onclick={() => trialBalanceQuery.refetch()} data-testid="trial-balance-retry">Try again</Button>
			</div>
		{:else}
			<div class="overflow-hidden rounded-xl border border-border bg-card" data-testid="trial-balance-table">
				{#if loading}
					<div class="space-y-4 p-4" role="status" aria-label="Loading trial balance">
						<div class="h-5 w-40 animate-pulse rounded bg-muted"></div>
						{#each Array(5) as _, index}
							<div class="grid grid-cols-[7rem_minmax(12rem,1fr)_10rem_10rem] gap-4" data-testid={`trial-balance-loading-row-${index}`}>
								<div class="h-4 animate-pulse rounded bg-muted"></div>
								<div class="h-4 animate-pulse rounded bg-muted"></div>
								<div class="h-4 animate-pulse rounded bg-muted"></div>
								<div class="h-4 animate-pulse rounded bg-muted"></div>
							</div>
						{/each}
					</div>
				{:else if trialBalance && trialBalance.rows.length === 0}
					<div class="p-8 text-center text-sm text-muted-foreground" data-testid="trial-balance-empty">
						No accounting activity for this date yet.
					</div>
				{:else if trialBalance}
					<div class="overflow-x-auto">
						<Table.Root class="min-w-[680px]">
							<Table.Header>
								<Table.Row class="bg-muted/30 hover:bg-muted/30">
									<Table.Head class="w-28 px-4">Code</Table.Head>
									<Table.Head class="px-4">Account</Table.Head>
									<Table.Head class="w-44 px-4 text-right">Debit balance</Table.Head>
									<Table.Head class="w-44 px-4 text-right">Credit balance</Table.Head>
								</Table.Row>
							</Table.Header>
							<Table.Body>
								{#each trialBalance.rows as row (row.accountId)}
									<Table.Row>
										<Table.Cell class="px-4 font-mono text-xs text-muted-foreground">{row.accountCode}</Table.Cell>
										<Table.Cell class="px-4">{row.accountName}</Table.Cell>
										<Table.Cell class={`px-4 text-right font-mono tabular-nums ${accountingAmountClass(row.debitBalance)}`}>
											{formatAccountingCurrency(row.debitBalance, currency)}
										</Table.Cell>
										<Table.Cell class={`px-4 text-right font-mono tabular-nums ${accountingAmountClass(row.creditBalance)}`}>
											{formatAccountingCurrency(row.creditBalance, currency)}
										</Table.Cell>
									</Table.Row>
								{/each}
							</Table.Body>
							<Table.Footer>
								<Table.Row>
									<Table.Cell colspan={2} class="px-4 font-semibold">Totals</Table.Cell>
									<Table.Cell class="px-4 text-right font-mono font-semibold tabular-nums">
										{formatAccountingCurrency(trialBalance.totalDebits, currency)}
									</Table.Cell>
									<Table.Cell class="px-4 text-right font-mono font-semibold tabular-nums">
										{formatAccountingCurrency(trialBalance.totalCredits, currency)}
									</Table.Cell>
								</Table.Row>
							</Table.Footer>
						</Table.Root>
					</div>
				{/if}
			</div>
			{#if status && trialBalance && trialBalance.rows.length > 0}
				<div
					class={`mt-3 rounded-xl border px-4 py-3 text-sm ${status.balanced ? 'border-success/40 bg-success/5 text-foreground' : 'border-warning/40 bg-warning/5 text-warning'}`}
					role="status"
					data-testid="trial-balance-status"
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
		:global([data-testid='trial-balance-detail-mode'] > span),
		:global([data-testid='trial-balance-detail-mode'] > button) {
			display: none;
		}
	}
</style>
