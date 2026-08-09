<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowRight, ChevronDown, ScanLine } from '@lucide/svelte';
	import type { UnitDashboard } from '$lib/types';
	import {
		tenantAccounts,
		type TenantAccountDetail
	} from '$lib/api/endpoints/tenant-accounts';
	import { securityDeposits } from '$lib/api/endpoints/securityDeposits';
	import {
		tenantLedgers,
		type RecurringTenantChargeRow,
		type TenantLedgerParams,
		type TenantLedgerPeriodSummary,
		type TenantLedgerRow,
		type TenantLedgerSummaryMonths,
		type TenantMonthSummary
	} from '$lib/api/endpoints/tenant-ledgers';
	import { tenantMoney } from '$lib/api/endpoints/tenant-money';
	import { tenantLedgerPeriodRange } from '$lib/components/unit/money';
	import {
		formatAccountingCurrency,
		formatAccountingDate
	} from '$lib/accounting/accounting-display';
	import { oldestOpenChargeDisplay } from '$lib/accounting/tenant-ledger-oldest-charge';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { ACCOUNTING_HELP } from '$lib/accounting/accounting-help';
	import JournalDetailDrawer from './JournalDetailDrawer.svelte';
	import TenantLedgerMonth, { type TenantLedgerRowAction } from './TenantLedgerMonth.svelte';
	import RecordPaymentSheet from './RecordPaymentSheet.svelte';
	import OneTimeChargeSheet, { type OneTimeChargeSeed } from './OneTimeChargeSheet.svelte';
	import TenantCreditSheet from './TenantCreditSheet.svelte';
	import RecurringChargeSheet from './RecurringChargeSheet.svelte';

	type TenantLedgerFilter = 'all' | 'open' | 'payments' | 'credits';
	type SheetKind = 'payment' | 'charge' | 'credit' | 'recurring' | null;
	type FixChoice = 'reduce' | 'increase' | 'remove' | null;

	const PERIODS: TenantLedgerSummaryMonths[] = [3, 6, 9, 12];
	const FILTERS: Array<{ value: TenantLedgerFilter; label: string }> = [
		{ value: 'all', label: 'All' },
		{ value: 'open', label: 'Open charges' },
		{ value: 'payments', label: 'Payments' },
		{ value: 'credits', label: 'Credits & corrections' }
	];

	interface TenantAccountSummary extends TenantAccountDetail {
		receivableBalance: number;
		unappliedCredit: number;
		pastDueAmount: number;
		nextDueOn: string | null;
		nextDueAmount: number;
	}

	let {
		dashboard,
		onScan,
		onopenpayment
	}: {
		dashboard: UnitDashboard;
		onScan: () => void;
		onopenpayment?: (tenantLedgerEntryId: number) => void;
	} = $props();

	const queryClient = useQueryClient();
	const tenantAccountId = $derived(dashboard.tenantAccountId ?? null);
	let periodMonths = $state<TenantLedgerSummaryMonths>(12);
	let filter = $state<TenantLedgerFilter>('all');
	let sheet = $state<SheetKind>(null);
	let creditTarget = $state<TenantLedgerRow | null>(null);
	let chargeSeed = $state<OneTimeChargeSeed | null>(null);
	let recurringTarget = $state<RecurringTenantChargeRow | null>(null);
	let fixTarget = $state<TenantLedgerRow | null>(null);
	let fixChoice = $state<FixChoice>(null);
	let journalPublicId = $state<string | null>(null);

	const periodRange = $derived(tenantLedgerPeriodRange(periodMonths));
	const ledgerParams = $derived.by(() => {
		const params: TenantLedgerParams = {
			take: 200,
			sort: '-effectiveOn',
			effectiveFrom: periodRange.from,
			effectiveTo: periodRange.to
		};
		if (filter === 'open') params.openOnly = true;
		if (filter === 'payments') params.entryType = 'PaymentReceipt';
		if (filter === 'credits') params.entryType = 'Credit';
		return params;
	});

	const accountQuery = createQuery(() => ({
		queryKey: ['tenant-account-summary', tenantAccountId],
		enabled: tenantAccountId != null,
		queryFn: () => tenantAccounts.get(tenantAccountId as number) as Promise<TenantAccountSummary>
	}));
	const ledgerQuery = createQuery(() => ({
		queryKey: [
			'tenant-ledger',
			tenantAccountId,
			periodMonths,
			filter,
			periodRange.from,
			periodRange.to
		],
		enabled: tenantAccountId != null,
		queryFn: () => tenantLedgers.list(tenantAccountId as number, ledgerParams)
	}));
	const monthSummaryQuery = createQuery(() => ({
		queryKey: ['tenant-ledger-month-summary', tenantAccountId, periodRange.from, periodRange.to],
		enabled: tenantAccountId != null,
		queryFn: () => tenantLedgers.monthSummary(tenantAccountId as number, periodRange)
	}));
	const ledgerSummaryQuery = createQuery(() => ({
		queryKey: ['tenant-ledger-summary', tenantAccountId, periodMonths],
		enabled: tenantAccountId != null,
		queryFn: () => tenantLedgers.ledgerSummary(tenantAccountId as number, periodMonths)
	}));
	const depositQuery = createQuery(() => ({
		queryKey: ['tenant-security-deposit', tenantAccountId],
		enabled: tenantAccountId != null,
		queryFn: () => securityDeposits.get(tenantAccountId as number)
	}));
	const recurringQuery = createQuery(() => ({
		queryKey: ['tenant-recurring-charges', tenantAccountId],
		enabled: tenantAccountId != null,
		queryFn: () => tenantLedgers.recurringCharges(tenantAccountId as number, { take: 50, sort: 'nextRunDate' })
	}));

	const accountSummary = $derived(accountQuery.data);
	const ledgerSummary = $derived(ledgerSummaryQuery.data as TenantLedgerPeriodSummary | null | undefined);
	const currency = $derived(
		accountSummary?.currency ?? ledgerSummary?.currency ?? ledgerQuery.data?.items[0]?.currency ?? 'USD'
	);
	const rows = $derived(ledgerQuery.data?.items ?? []);
	const monthSummaries = $derived(monthSummaryQuery.data ?? []);
	const activeSchedules = $derived((recurringQuery.data?.items ?? []).filter((schedule) => schedule.isActive));
	const hasHistory = $derived(rows.length > 0 || monthSummaries.length > 0);
	const nextDueOn = $derived(accountSummary?.nextDueOn ?? null);
	const nextDueAmount = $derived(accountSummary?.nextDueAmount ?? null);
	const balanceDue = $derived(accountSummary?.receivableBalance ?? ledgerSummary?.endingBalance ?? null);
	const pastDue = $derived(accountSummary?.pastDueAmount ?? dashboard.tenantAccountCondition.pastDueAmount);
	const unappliedCredit = $derived(accountSummary?.unappliedCredit ?? null);
	const deposit = $derived(depositQuery.data);
	const balancesMatch = $derived(
		balanceDue != null && pastDue != null && Math.abs(balanceDue - pastDue) < 0.005
	);
	const oldestOpenCharge = $derived(
		oldestOpenChargeDisplay({
			businessDate: accountSummary?.businessDate,
			oldestOpenChargeDueOn: accountSummary?.oldestOpenChargeDueOn ?? null,
			oldestOpenChargeAmount: accountSummary?.oldestOpenChargeAmount ?? null
		})
	);

	const monthGroups = $derived.by(() => {
		const rowsByMonth = new Map<string, TenantLedgerRow[]>();
		for (const row of rows) {
			const monthKey = row.effectiveOn.slice(0, 7);
			const monthRows = rowsByMonth.get(monthKey);
			if (monthRows) monthRows.push(row);
			else rowsByMonth.set(monthKey, [row]);
		}
		return [...monthSummaries]
			.sort((left, right) => right.year - left.year || right.month - left.month)
			.map((summary) => ({
				summary,
				rows: rowsByMonth.get(`${summary.year}-${String(summary.month).padStart(2, '0')}`) ?? []
			}));
	});

	function errorStatus(error: unknown): number | undefined {
		if (!error || typeof error !== 'object' || !('status' in error)) return undefined;
		const status = (error as { status?: unknown }).status;
		return typeof status === 'number' ? status : undefined;
	}

	const coreReadError = $derived(ledgerQuery.error ?? monthSummaryQuery.error);
	const summaryReadError = $derived(accountQuery.error ?? ledgerSummaryQuery.error);
	const unavailable = $derived([401, 403, 404].includes(errorStatus(coreReadError) ?? 0));
	const loading = $derived(ledgerQuery.isLoading || monthSummaryQuery.isLoading);

	function invalidateMoney(): void {
		queryClient.invalidateQueries({ queryKey: ['tenant-account-summary', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['tenant-ledger', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['tenant-ledger-month-summary', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['tenant-ledger-summary', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['tenant-ledger-open-charges', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['tenant-ledger-credit-targets', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['tenant-recurring-charges', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['tenant-security-deposit', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['unit-dashboard', dashboard.unit.id] });
		queryClient.invalidateQueries({ queryKey: ['unit-timeline', dashboard.unit.id] });
		queryClient.invalidateQueries({ queryKey: ['accounting'] });
	}

	function closeSheet(): void {
		sheet = null;
		creditTarget = null;
		chargeSeed = null;
		recurringTarget = null;
	}

	function openNewCharge(): void {
		chargeSeed = null;
		sheet = 'charge';
	}

	function openRecurring(schedule: RecurringTenantChargeRow | null): void {
		recurringTarget = schedule;
		sheet = 'recurring';
	}

	function openFixCharge(row: TenantLedgerRow): void {
		fixTarget = row;
		fixChoice = null;
	}

	function closeFixCharge(): void {
		if (!reverseMutation.isPending) {
			fixTarget = null;
			fixChoice = null;
		}
	}

	function handleRowAction(row: TenantLedgerRow, action: TenantLedgerRowAction): void {
		if (action === 'view') {
			if (row.type === 'PaymentReceipt') onopenpayment?.(row.tenantLedgerEntryId);
			else if (row.journalEntryPublicId) journalPublicId = row.journalEntryPublicId;
			return;
		}
		if (action === 'give-credit') {
			creditTarget = row;
			sheet = 'credit';
			return;
		}
		if (action === 'related-charge') {
			chargeSeed = {
				description: `Additional charge related to ${row.description}`,
				effectiveOn: row.effectiveOn,
				dueOn: row.dueOn ?? row.effectiveOn,
				chargeType: 'Other'
			};
			sheet = 'charge';
			return;
		}
		if (action === 'fix-charge') {
			openFixCharge(row);
			return;
		}
		if (action === 'fix-payment') onopenpayment?.(row.tenantLedgerEntryId);
	}

	function applyFix(): void {
		if (!fixTarget || !fixChoice) return;
		const target = fixTarget;
		if (fixChoice === 'reduce') {
			fixTarget = null;
			fixChoice = null;
			creditTarget = target;
			sheet = 'credit';
			return;
		}
		if (fixChoice === 'increase') {
			fixTarget = null;
			fixChoice = null;
			chargeSeed = {
				description: `Additional charge for ${target.description}`,
				effectiveOn: target.effectiveOn,
				dueOn: target.dueOn ?? target.effectiveOn,
				chargeType: 'Other'
			};
			sheet = 'charge';
			return;
		}
		reverseMutation.mutate(target);
	}

	const reverseMutation = createMutation(() => ({
		mutationFn: (target: TenantLedgerRow) =>
			tenantMoney.reverseCharge(tenantAccountId as number, target.tenantLedgerEntryId, crypto.randomUUID(), {
				effectiveOn: new Date().toISOString().slice(0, 10),
				reason: `Reverse charge: ${target.description}`
			}),
		onSuccess: () => {
				showSuccess('Charge reversed.');
				closeFixCharge();
				invalidateMoney();
			},
		onError: (error) => showError(apiErrorMessage(error, 'The charge was not reversed.'))
	}));

	function retryReads(): void {
		void accountQuery.refetch();
		void ledgerQuery.refetch();
		void monthSummaryQuery.refetch();
		void ledgerSummaryQuery.refetch();
	}
</script>

<div class="space-y-4" data-testid="tenant-ledger-panel">
	<div class="flex items-center justify-end gap-1.5 text-sm font-medium text-muted-foreground">
		<span>About this ledger</span>
		<HelpPopover
			title={ACCOUNTING_HELP.tenantLedger.title}
			summary={ACCOUNTING_HELP.tenantLedger.summary}
			learnMoreUrl={ACCOUNTING_HELP.tenantLedger.href}
			testid="tenant-ledger-help"
		/>
	</div>
	<div class="grid gap-2 overflow-x-auto sm:grid-cols-2 lg:grid-cols-5">
		<div class="min-w-36 rounded-xl border border-border bg-card px-4 py-3">
			<p class="text-xs text-muted-foreground">Balance due</p>
			<p class="mt-1 font-mono text-xl font-semibold tabular-nums">{formatAccountingCurrency(balanceDue, currency)}</p>
		</div>
		{#if balancesMatch}
			<div class="min-w-44 rounded-xl border border-border bg-card px-4 py-3" data-testid="tenant-ledger-oldest-charge">
				<p class="text-xs text-muted-foreground">Oldest open charge</p>
				{#if oldestOpenCharge}
					<p class="mt-1 font-medium">{oldestOpenCharge.ageDays ? `${oldestOpenCharge.ageDays} days late` : `Due ${formatAccountingDate(oldestOpenCharge.dueOn)}`}</p>
					<p class="mt-0.5 font-mono text-xs tabular-nums text-muted-foreground">{formatAccountingCurrency(oldestOpenCharge.openAmount, currency)}</p>
				{:else}
					<p class="mt-1 font-medium">No open charges</p>
				{/if}
			</div>
		{:else}
			<div class="min-w-36 rounded-xl border border-border bg-card px-4 py-3" data-testid="tenant-ledger-past-due">
				<p class="text-xs text-muted-foreground">Past due</p>
				<p class="mt-1 font-mono text-xl font-semibold tabular-nums">{formatAccountingCurrency(pastDue, currency)}</p>
			</div>
		{/if}
		<div class="min-w-48 rounded-xl border border-border bg-card px-4 py-3">
			<p class="text-xs text-muted-foreground">Next due</p>
			<p class="mt-1 font-medium">{formatAccountingDate(nextDueOn)} <span class="font-mono tabular-nums">· {formatAccountingCurrency(nextDueAmount, currency)}</span></p>
		</div>
		<div class="min-w-40 rounded-xl border border-border bg-card px-4 py-3">
			<p class="text-xs text-muted-foreground">Unapplied credit</p>
			<p class="mt-1 font-mono text-xl font-semibold tabular-nums">{formatAccountingCurrency(unappliedCredit, currency)}</p>
		</div>
		<div class="min-w-44 rounded-xl border border-border bg-card px-4 py-3">
			<p class="text-xs text-muted-foreground">Deposit held</p>
			{#if deposit?.securityDepositAccountId}
				<a class="mt-1 inline-flex items-center gap-1 font-mono font-semibold tabular-nums text-primary hover:underline" href={`/deposits/${deposit.securityDepositAccountId}`}>
					{formatAccountingCurrency(deposit.heldBalance, deposit.currency || currency)} <ArrowRight class="size-4" />
				</a>
			{:else}
				<p class="mt-1 font-mono text-xl font-semibold tabular-nums">—</p>
			{/if}
		</div>
	</div>

	<div class="flex flex-wrap items-center gap-2" data-testid="tenant-ledger-actions">
		<Button size="sm" onclick={() => sheet = 'payment'} disabled={tenantAccountId == null}>Record payment</Button>
		<Button size="sm" variant="outline" onclick={openNewCharge} disabled={tenantAccountId == null}>Add charge</Button>
		<Button size="sm" variant="outline" onclick={() => { creditTarget = null; sheet = 'credit'; }} disabled={tenantAccountId == null}>Give credit</Button>
		<DropdownMenu.Root>
			<DropdownMenu.Trigger class="inline-flex h-9 items-center gap-1 rounded-md border border-input bg-background px-3 text-sm font-medium shadow-sm hover:bg-muted disabled:pointer-events-none disabled:opacity-50" disabled={tenantAccountId == null}>
				Recurring charge <ChevronDown class="size-4" />
			</DropdownMenu.Trigger>
			<DropdownMenu.Content align="start" class="w-72">
				{#if recurringQuery.isLoading}
					<div class="px-2 py-2 text-sm text-muted-foreground">Loading recurring charges…</div>
				{:else if activeSchedules.length === 0}
					<div class="px-2 py-2 text-sm text-muted-foreground">No active recurring charges.</div>
				{:else}
					{#each activeSchedules as schedule (schedule.id)}
						<DropdownMenu.Item onSelect={() => openRecurring(schedule)}>
							<span class="flex min-w-0 flex-col"><span class="truncate">{schedule.displayName}</span><span class="text-xs text-muted-foreground">{formatAccountingCurrency(schedule.amount, schedule.currency)} · next {formatAccountingDate(schedule.nextRunDate)}</span></span>
						</DropdownMenu.Item>
					{/each}
				{/if}
				<DropdownMenu.Separator />
				<DropdownMenu.Item onSelect={() => openRecurring(null)}>+ New recurring charge</DropdownMenu.Item>
			</DropdownMenu.Content>
		</DropdownMenu.Root>
		<Button size="sm" variant="outline" onclick={onScan}><ScanLine class="mr-1.5 size-4" />Scan payment</Button>
	</div>

	<div class="flex flex-col gap-3 rounded-xl border border-border bg-card p-3 sm:flex-row sm:items-center sm:justify-between">
		<div class="flex items-center gap-1" aria-label="Ledger period">
			{#each PERIODS as option}
				<button type="button" class={`rounded-md px-3 py-1.5 text-sm ${periodMonths === option ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-muted hover:text-foreground'}`} aria-pressed={periodMonths === option} onclick={() => periodMonths = option}>{option} mo</button>
			{/each}
		</div>
		<div class="flex gap-1 overflow-x-auto" aria-label="Ledger filter">
			{#each FILTERS as option}
				<button type="button" class={`whitespace-nowrap rounded-md px-3 py-1.5 text-sm ${filter === option.value ? 'bg-muted font-medium text-foreground' : 'text-muted-foreground hover:bg-muted hover:text-foreground'}`} aria-pressed={filter === option.value} onclick={() => filter = option.value}>{option.label}</button>
			{/each}
		</div>
	</div>

	{#if summaryReadError}
		<div class="inline-flex w-fit items-center rounded-full border border-amber-500/40 bg-amber-500/10 px-3 py-1 text-xs text-foreground" role="status" data-testid="tenant-ledger-summary-error">
			Some summary totals are unavailable. Rent and payment history is still shown below.
		</div>
	{/if}

	{#if loading}
		<LoadingState label="Loading rent and payment history" variant="section" testid="tenant-ledger-loading" />
	{:else if coreReadError}
		<section class="rounded-xl border border-border bg-card px-4 py-8 text-center" role="alert" data-testid="tenant-ledger-error">
			<p class="font-medium">{unavailable ? 'This money view is not available.' : 'Rent and payment history is unavailable.'}</p>
			{#if !unavailable}<Button class="mt-4" variant="outline" size="sm" onclick={retryReads}>Try again</Button>{/if}
		</section>
	{:else if !hasHistory}
		<section class="rounded-xl border border-dashed border-border bg-card px-4 py-12 text-center text-sm text-muted-foreground" data-testid="tenant-ledger-empty">
			No charges or payments yet. Record the first payment or charge above.
		</section>
	{:else}
		<div class="space-y-4" data-testid="tenant-ledger-months">
			{#each monthGroups as group ( `${group.summary.year}-${group.summary.month}` )}
				<TenantLedgerMonth summary={group.summary} rows={group.rows} onaction={handleRowAction} />
			{/each}
		</div>
	{/if}
</div>

<RecordPaymentSheet
	open={sheet === 'payment'}
	tenantAccountId={tenantAccountId ?? 0}
	currency={currency}
	defaultPayerName={dashboard.currentTenant?.name ?? dashboard.currentTenants?.[0]?.name ?? ''}
	onclose={closeSheet}
	onsaved={invalidateMoney}
/>
<OneTimeChargeSheet
	open={sheet === 'charge'}
	tenantAccountId={tenantAccountId ?? 0}
	seed={chargeSeed}
	tenantName={dashboard.currentTenant?.name ?? dashboard.currentTenants?.map((tenant) => tenant.name).join(', ') ?? ''}
	propertyName={dashboard.propertyName}
	unitLabel={`Unit ${dashboard.unit.unitNumber}`}
	currentBalance={balanceDue}
	currency={currency}
	onclose={closeSheet}
	onsaved={invalidateMoney}
/>
<TenantCreditSheet
	open={sheet === 'credit'}
	tenantAccountId={tenantAccountId ?? 0}
	currency={currency}
	ledgerRows={rows}
	initialTarget={creditTarget}
	onclose={closeSheet}
	onsaved={invalidateMoney}
/>
	<RecurringChargeSheet
	open={sheet === 'recurring'}
	tenantAccountId={tenantAccountId ?? 0}
	leaseAgreementId={dashboard.currentLease?.id ?? null}
	propertyId={dashboard.unit.propertyId}
	unitId={dashboard.unit.id}
	schedule={recurringTarget}
	onclose={closeSheet}
	onsaved={invalidateMoney}
/>

<Dialog.Root open={fixTarget != null} onOpenChange={(next) => { if (!next) closeFixCharge(); }}>
	<Dialog.Content class="max-w-lg" data-testid="fix-charge-dialog">
		<Dialog.Header>
			<Dialog.Title>Fix this charge</Dialog.Title>
			<Dialog.Description>
				{#if fixTarget}{formatAccountingDate(fixTarget.effectiveOn)} · {fixTarget.description} · {formatAccountingCurrency(fixTarget.chargeAmount, fixTarget.currency)}{/if}
			</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-2">
			<button type="button" class={`w-full rounded-lg border px-3 py-3 text-left ${fixChoice === 'reduce' ? 'border-primary bg-primary/5' : 'border-border hover:bg-muted/50'}`} aria-pressed={fixChoice === 'reduce'} onclick={() => fixChoice = 'reduce'}>
					<span class="block font-medium">Issue a credit</span><span class="block text-sm text-muted-foreground">Posts a credit to reduce this charge.</span>
			</button>
			<button type="button" class={`w-full rounded-lg border px-3 py-3 text-left ${fixChoice === 'increase' ? 'border-primary bg-primary/5' : 'border-border hover:bg-muted/50'}`} aria-pressed={fixChoice === 'increase'} onclick={() => fixChoice = 'increase'}>
					<span class="block font-medium">Add the missing amount</span><span class="block text-sm text-muted-foreground">Posts a related charge for the amount that was missed.</span>
			</button>
			<button type="button" class={`w-full rounded-lg border px-3 py-3 text-left ${fixChoice === 'remove' ? 'border-primary bg-primary/5' : 'border-border hover:bg-muted/50'}`} aria-pressed={fixChoice === 'remove'} onclick={() => fixChoice = 'remove'}>
					<span class="block font-medium">Reverse the posted charge</span><span class="block text-sm text-muted-foreground">Removes this charge with a linked reversal entry.</span>
			</button>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={closeFixCharge} disabled={reverseMutation.isPending}>Cancel</Button>
			<Button onclick={applyFix} disabled={!fixChoice || reverseMutation.isPending}>Continue</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<JournalDetailDrawer bind:journalPublicId />
