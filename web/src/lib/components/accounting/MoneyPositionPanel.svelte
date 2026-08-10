<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import RangeDatePicker from '$lib/components/shared/RangeDatePicker.svelte';
	import HelpTooltip from '$lib/components/ui/HelpTooltip.svelte';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { Button } from '$lib/components/ui/button';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { moneyPosition, type MoneyPositionResponse } from '$lib/api/endpoints/money-position';
	import {
		formatAccountingCurrency,
		formatAccountingDate
	} from '$lib/accounting/accounting-display';
	import { ACCOUNTING_HELP } from '$lib/accounting/accounting-help';
	import { getAccountingDetailMode } from './AccountingDetailMode.svelte';

	type Period = 'this-month' | 'last-month' | 'year-to-date' | 'custom';

	let {
		class: className,
		testid = 'money-position-panel'
	}: {
		class?: string;
		testid?: string;
	} = $props();

	const authState = getAuthState();
	const portfolioId = $derived(getCurrentPortfolioId());
	const detailMode = getAccountingDetailMode();
	const advanced = $derived((detailMode?.mode ?? 'simple') === 'advanced');
	const today = new Date();
	const todayIso = toIsoDate(today);
	const monthStartIso = toIsoDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1)));

	let period = $state<Period>('this-month');
	let customFrom = $state(monthStartIso);
	let customTo = $state(todayIso);

	function toIsoDate(value: Date): string {
		return value.toISOString().slice(0, 10);
	}

	function rangeForPeriod(value: Period, from: string, to: string): { from?: string; to?: string } {
		const now = new Date();
		if (value === 'custom') return { from: from || undefined, to: to || undefined };
		if (value === 'last-month') {
			return {
				from: toIsoDate(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - 1, 1))),
				to: toIsoDate(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 0)))
			};
		}
		if (value === 'year-to-date') {
			return {
				from: toIsoDate(new Date(Date.UTC(now.getUTCFullYear(), 0, 1))),
				to: toIsoDate(now)
			};
		}
		return {
			from: toIsoDate(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1))),
			to: toIsoDate(now)
		};
	}

	const periodRange = $derived.by(() => rangeForPeriod(period, customFrom, customTo));
	const positionQuery = createQuery(() => ({
		queryKey: ['accounting-money-position', portfolioId, period, periodRange.from, periodRange.to],
		enabled: authState.isAuthenticated,
		queryFn: () => moneyPosition.get(periodRange)
	}));

	const position = $derived(positionQuery.data as MoneyPositionResponse | undefined);
	const warning = $derived(
		position != null && position.tenantDepositsHeld > position.totalCashOnHand
	);

	function selectPeriod(value: string | undefined): void {
		if (value === 'this-month' || value === 'last-month' || value === 'year-to-date' || value === 'custom') {
			period = value;
		}
	}

	function amount(value: number | null | undefined, currency = 'USD'): string {
		return formatAccountingCurrency(value, currency);
	}

	function decreaseAmount(value: number | null | undefined, currency = 'USD'): string {
		const formatted = amount(value, currency);
		return value != null && value > 0 ? `-${formatted}` : formatted;
	}
</script>

<section class={['space-y-4', className]} data-testid={testid}>
	{#if positionQuery.isLoading}
		<div class="grid gap-4 lg:grid-cols-[1.25fr_1fr]">
			<div class="rounded-xl border border-border bg-card p-5" aria-label="Loading cash position">
				<div class="h-4 w-20 animate-pulse rounded bg-muted"></div>
				<div class="mt-4 h-10 w-52 animate-pulse rounded bg-muted"></div>
				<div class="mt-2 h-4 w-36 animate-pulse rounded bg-muted"></div>
				<div class="mt-8 space-y-3">
					<div class="h-4 w-full animate-pulse rounded bg-muted"></div>
					<div class="h-4 w-4/5 animate-pulse rounded bg-muted"></div>
					<div class="h-5 w-full animate-pulse rounded bg-muted"></div>
				</div>
			</div>
			<div class="rounded-xl border border-border bg-card p-5" aria-label="Loading financial position">
				<div class="h-4 w-36 animate-pulse rounded bg-muted"></div>
				<div class="mt-5 space-y-5">
					{#each Array(4) as _}
						<div class="flex items-center justify-between gap-4">
							<div class="h-4 w-32 animate-pulse rounded bg-muted"></div>
							<div class="h-4 w-24 animate-pulse rounded bg-muted"></div>
						</div>
					{/each}
				</div>
			</div>
		</div>
	{:else if positionQuery.isError}
		<div class="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-destructive/40 bg-destructive/5 p-5" role="alert" data-testid="money-position-error">
			<div>
				<h2 class="font-semibold">Cash position unavailable</h2>
				<p class="mt-1 text-sm text-muted-foreground">The server could not load this portfolio's money position.</p>
			</div>
			<Button variant="outline" size="sm" onclick={() => positionQuery.refetch()}>Try again</Button>
		</div>
	{:else if position}
		{#if warning}
			<div class="rounded-lg border border-amber-500/50 bg-amber-500/10 px-4 py-3 text-sm text-amber-950 dark:text-amber-100" role="alert" data-testid="money-position-deposit-warning">
				<strong>Tenant deposits held ({amount(position.tenantDepositsHeld)}) exceed cash available ({amount(position.totalCashOnHand)}).</strong>
			</div>
		{/if}

		<div class="grid gap-4 lg:grid-cols-[1.25fr_1fr]">
			<Card.Root class="gap-0 py-0" data-testid="money-position-cash-card">
				<Card.Header class="px-5 pb-2 pt-5">
					<Card.Title class="flex items-center gap-1.5 text-base">
						Cash
						<HelpPopover
							title={ACCOUNTING_HELP.cashPosition.title}
							summary={ACCOUNTING_HELP.cashPosition.summary}
							learnMoreUrl={ACCOUNTING_HELP.cashPosition.href}
							testid="money-position-cash-help"
						/>
					</Card.Title>
				</Card.Header>
				<Card.Content class="space-y-5 px-5 pb-5 pt-0">
					<div>
						<p class="text-sm text-muted-foreground">Cash available</p>
						<p class="mt-1 font-mono text-4xl font-semibold tabular-nums" data-testid="money-position-total-cash">
							{amount(position.totalCashOnHand)}
						</p>
						<p class="mt-1 text-sm text-muted-foreground">As of {formatAccountingDate(position.asOfUtc)}</p>
					</div>

					<div class="space-y-3 border-t border-border pt-4 text-sm">
						<div class="flex items-center justify-between gap-4">
							<span class="text-muted-foreground">Tenant deposits held</span>
							<span class="font-mono tabular-nums text-destructive">{decreaseAmount(position.tenantDepositsHeld)}</span>
						</div>
						<div class="flex items-center justify-between gap-4 border-t border-border pt-3 font-semibold">
							<span>Cash after tenant deposits</span>
							<span class="font-mono tabular-nums" data-testid="money-position-after-deposits">{amount(position.cashAfterTenantDeposits)}</span>
						</div>
					</div>
				</Card.Content>
			</Card.Root>

			<Card.Root class="gap-0 py-0" data-testid="money-position-facts-card">
				<Card.Header class="px-5 pb-2 pt-5">
					<Card.Title class="text-base">Financial position</Card.Title>
				</Card.Header>
				<Card.Content class="space-y-4 px-5 pb-5 pt-0 text-sm">
					<div class="flex items-center justify-between gap-4">
						<span class="text-muted-foreground">Who's behind</span>
						<span class="font-mono tabular-nums">{amount(position.rentStillOwed)}</span>
					</div>
					<div class="flex items-center justify-between gap-4">
						<span class="text-muted-foreground">Loan balance</span>
						<span class="font-mono tabular-nums">{amount(position.loanBalance)}</span>
					</div>
					{#if advanced}
						<div class="flex items-center justify-between gap-4">
							<span class="flex items-center gap-1 text-muted-foreground">Book equity <HelpTooltip text="Assets minus liabilities on the books. Not market value." label="What book equity means" /></span>
							<span class="font-mono tabular-nums">{amount(position.bookEquity)}</span>
						</div>
					{/if}
				</Card.Content>
			</Card.Root>
		</div>

		<div class="rounded-xl border border-border bg-card p-5" data-testid="money-position-period-card">
			<div class="flex flex-wrap items-center justify-between gap-3">
				<div>
					<h2 class="font-semibold">This period</h2>
					<p class="mt-1 text-sm text-muted-foreground">Cash movement and profit for the selected dates.</p>
				</div>
				<Select.Root type="single" value={period} onValueChange={(value) => selectPeriod(typeof value === 'string' ? value : value?.[0])}>
					<Select.Trigger class="w-44" aria-label="Money position period" data-testid="money-position-period">
						{period === 'this-month' ? 'This month' : period === 'last-month' ? 'Last month' : period === 'year-to-date' ? 'Year to date' : 'Custom'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="this-month" label="This month">This month</Select.Item>
						<Select.Item value="last-month" label="Last month">Last month</Select.Item>
						<Select.Item value="year-to-date" label="Year to date">Year to date</Select.Item>
						<Select.Item value="custom" label="Custom">Custom</Select.Item>
					</Select.Content>
				</Select.Root>
			</div>

			{#if period === 'custom'}
				<div class="mt-4 max-w-md">
					<label class="grid gap-1.5 text-sm font-medium" for="money-position-date-range">
						Custom dates
						<RangeDatePicker id="money-position-date-range" bind:start={customFrom} bind:end={customTo} testid="money-position-date-range" placeholder="Choose custom dates" />
					</label>
				</div>
			{/if}

			<div class="mt-5 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
				<div>
					<p class="text-sm text-muted-foreground">Cash received</p>
					<p class="mt-1 font-mono text-xl font-semibold tabular-nums">{amount(position.cashReceived)}</p>
				</div>
				<div>
					<p class="text-sm text-muted-foreground">Cash paid</p>
					<p class="mt-1 font-mono text-xl font-semibold tabular-nums">{decreaseAmount(position.cashPaid)}</p>
				</div>
				<div>
					<p class="text-sm text-muted-foreground">Kept</p>
					<p class="mt-1 font-mono text-xl font-semibold tabular-nums">{amount(position.netCashMovement)}</p>
				</div>
				<div>
					<p class="text-sm text-muted-foreground">Profit after expenses</p>
					<p class="mt-1 font-mono text-xl font-semibold tabular-nums">{amount(position.profitOrLoss)}</p>
				</div>
			</div>
		</div>
	{:else}
		<p class="rounded-xl border border-border bg-card p-5 text-sm text-muted-foreground">No cash position is available yet.</p>
	{/if}
</section>
