<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { pushState, replaceState } from '$app/navigation';
	import { page } from '$app/state';
	import type { UnitDashboard } from '$lib/types';
	import { tenantAccounts } from '$lib/api/endpoints/tenant-accounts';
	import type { TenantLedgerEntry, TenantLedgerRow, TenantMonthSummary } from '$lib/api/endpoints/tenant-accounts';
	import { CAPABILITY } from '$lib/auth/experience-policy';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { payments } from '$lib/api/endpoints/payments';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { canReverseTenantLedgerEntry, money, tenantLedgerReversalReason, unitMoneyIdentity } from '../money';
	import { formatDateOnly } from '$lib/utils/date';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import PaymentDetail from '$lib/components/records/PaymentDetail.svelte';
	import MonthGroup from '$lib/components/ledger/MonthGroup.svelte';
	import LedgerRowSheet from '$lib/components/ledger/LedgerRowSheet.svelte';
	import LedgerTypeBadge from '$lib/components/ledger/LedgerTypeBadge.svelte';
	import LedgerAmount from '$lib/components/ledger/LedgerAmount.svelte';
	import RecurringChargeDialog from './RecurringChargeDialog.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { PAYMENT_METHODS } from '$lib/constants/payments';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { X, ScanLine, ArrowLeft, Receipt, FilePlus2, Repeat2, Undo2 } from '@lucide/svelte';

	let { dashboard, onScan, tabQuery = 'rent', ledgerQuery }: {
		dashboard: UnitDashboard;
		onScan: () => void;
		tabQuery?: string;
		ledgerQuery?: string;
	} = $props();

	const queryClient = useQueryClient();
	const moneyIdentity = $derived(unitMoneyIdentity({
		tenantAccountId: dashboard.tenantAccountId,
		leaseManagementId: dashboard.leaseManagementId
	}));
	const tenantAccountId = $derived(moneyIdentity.tenantAccountId);
	const canManagePayments = $derived(hasCapability(CAPABILITY.moneyPaymentsManage));
	const selectedReceipt = $derived(
		page.state.unitPaymentId ?? (Number(page.url.searchParams.get('payment')) || null)
	);
	const LEDGER_TAKE = 200;
	const today = () => new Date().toISOString().slice(0, 10);
	type PeriodMonths = 3 | 6 | 9 | 12;
	let periodMonths = $state<PeriodMonths>(3);
	let recurringOpen = $state(false);
	let selectedLedgerRow = $state<TenantLedgerRow | null>(null);

	function periodRange(months: PeriodMonths) {
		const now = new Date();
		const from = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - months + 1, 1));
		const to = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 0));
		return { from: from.toISOString().slice(0, 10), to: to.toISOString().slice(0, 10) };
	}
	const range = $derived(periodRange(periodMonths));
	function belongsToMonth(row: TenantLedgerRow, month: TenantMonthSummary) {
		return Number(row.effectiveOn.slice(0, 4)) === month.year && Number(row.effectiveOn.slice(5, 7)) === month.month;
	}

	function unitUrl(payment?: number) {
		const url = new URL(`/units/${dashboard.unit.id}`, page.url.origin);
		url.searchParams.set('tab', tabQuery);
		if (ledgerQuery) url.searchParams.set('view', ledgerQuery === 'expenses' ? 'operating-costs' : 'tenant-account');
		if (payment) url.searchParams.set('payment', String(payment));
		return `${url.pathname}${url.search}`;
	}

	function openReceipt(id: number) {
		pushState(unitUrl(id), {
			...page.state,
			unitTab: 'money',
			unitView: 'tenant-account',
			unitPaymentId: id,
			unitExpenseId: null,
		});
	}
	function clearSelection() {
		replaceState(unitUrl(), {
			...page.state,
			unitTab: 'money',
			unitView: 'tenant-account',
			unitPaymentId: null,
		});
	}

	const tenantLedgerQuery = createQuery(() => ({
		queryKey: ['tenant-ledger', tenantAccountId, periodMonths, range.from, range.to, LEDGER_TAKE],
		enabled: !!tenantAccountId,
		queryFn: () => tenantAccounts.ledger(tenantAccountId as number, {
			skip: 0,
			take: LEDGER_TAKE,
			effectiveFrom: range.from,
			effectiveTo: range.to,
			sort: 'effectiveOn,postedAtUtc,tenantLedgerEntryId'
		})
	}));
	const monthSummaryQuery = createQuery(() => ({
		queryKey: ['tenant-month-summary', tenantAccountId, periodMonths, range.from, range.to],
		enabled: !!tenantAccountId,
		queryFn: () => tenantAccounts.monthSummary(tenantAccountId as number, range)
	}));

	$effect(() => {
		void tenantAccountId;
		selectedLedgerRow = null;
		recurringOpen = false;
	});

	function invalidateMoney() {
		queryClient.invalidateQueries({ queryKey: ['tenant-ledger', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['tenant-month-summary', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['tenant-account-entries', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['tenant-account-charges', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['tenant-account-deposits', tenantAccountId] });
		queryClient.invalidateQueries({ queryKey: ['unit-dashboard', dashboard.unit.id] });
		queryClient.invalidateQueries({ queryKey: ['unit-timeline', dashboard.unit.id] });
		queryClient.invalidateQueries({ queryKey: ['accounting'] });
	}

	type FormKind = 'receipt' | 'charge' | 'reversal' | null;
	let formKind = $state<FormKind>(null);
	let operationKey = $state<string | null>(null);
	let errors = $state<Record<string, string>>({});
	let receiptForm = $state({ amount: '', effectiveOn: today(), description: 'Tenant payment', method: '', reference: '', payerName: '', targetChargeEntryId: '' });
	let chargeForm = $state({ amount: '', effectiveOn: today(), dueOn: today(), description: '' });
	let reversalTarget = $state<TenantLedgerEntry | null>(null);
	let reversalForm = $state({ effectiveOn: today(), reason: '' });

	function clearCreateError(field: string) {
		const next = clearFieldError(errors, field);
		if (next !== errors) errors = next;
	}

	$effect(() => {
		if (formKind === 'receipt' && Number(receiptForm.amount) > 0) clearCreateError('amount');
	});
	$effect(() => {
		if (formKind === 'receipt' && receiptForm.effectiveOn) clearCreateError('effectiveOn');
	});
	$effect(() => {
		if (formKind === 'receipt' && receiptForm.description.trim()) clearCreateError('description');
	});
	$effect(() => {
		if (formKind === 'receipt' && receiptForm.method) clearCreateError('method');
	});
	$effect(() => {
		if (formKind === 'receipt' && receiptForm.targetChargeEntryId) clearCreateError('targetChargeEntryId');
	});
	$effect(() => {
		if (formKind === 'charge' && Number(chargeForm.amount) > 0) clearCreateError('amount');
	});
	$effect(() => {
		if (formKind === 'charge' && chargeForm.effectiveOn) clearCreateError('effectiveOn');
	});
	$effect(() => {
		if (formKind === 'charge' && chargeForm.dueOn) clearCreateError('dueOn');
	});
	$effect(() => {
		if (formKind === 'charge' && chargeForm.description.trim()) clearCreateError('description');
	});
	$effect(() => {
		if (formKind === 'reversal' && reversalForm.effectiveOn) clearCreateError('effectiveOn');
	});
	$effect(() => {
		if (formKind === 'reversal' && reversalForm.reason.trim()) clearCreateError('reason');
	});

	function openForm(kind: Exclude<FormKind, null>) {
		formKind = kind;
		operationKey = null;
		errors = {};
		if (kind === 'receipt') receiptForm = { amount: '', effectiveOn: today(), description: 'Tenant payment', method: '', reference: '', payerName: '', targetChargeEntryId: '' };
		else if (kind === 'charge') chargeForm = { amount: '', effectiveOn: today(), dueOn: today(), description: '' };
	}
	function openReversal(entry: TenantLedgerEntry) {
		formKind = 'reversal';
		operationKey = null;
		errors = {};
		reversalTarget = entry;
		reversalForm = {
			effectiveOn: today(),
			reason: tenantLedgerReversalReason(entry)
		};
	}
	function closeForm() {
		formKind = null;
		operationKey = null;
		errors = {};
		reversalTarget = null;
	}

	const receiptMutation = createMutation(() => ({
		mutationFn: () => {
			if (!tenantAccountId) throw new Error('This rental does not have a tenant account.');
			operationKey ??= crypto.randomUUID();
			return payments.recordReceipt(tenantAccountId, operationKey, {
				amount: Number(receiptForm.amount),
				effectiveOn: receiptForm.effectiveOn,
				description: receiptForm.description.trim(),
				paymentMethodSummary: receiptForm.method,
				externalReference: receiptForm.reference.trim() || undefined,
				payerName: receiptForm.payerName.trim() || undefined,
				targetChargeEntryId: receiptTargetChargeEntryId()
			});
		},
		onSuccess: () => { showSuccess('Receipt recorded.'); closeForm(); invalidateMoney(); },
		onError: (error) => showError(apiErrorMessage(error))
	}));

	function receiptTargetChargeEntryId(): number | null {
		if (receiptForm.targetChargeEntryId === 'unapplied') return null;
		return Number(receiptForm.targetChargeEntryId);
	}

	const chargeMutation = createMutation(() => ({
		mutationFn: () => {
			if (!tenantAccountId) throw new Error('This rental does not have a tenant account.');
			operationKey ??= crypto.randomUUID();
			return payments.postCharge(tenantAccountId, operationKey, {
				amount: Number(chargeForm.amount),
				effectiveOn: chargeForm.effectiveOn,
				dueOn: chargeForm.dueOn,
				description: chargeForm.description.trim()
			});
		},
		onSuccess: () => { showSuccess('Manual charge added.'); closeForm(); invalidateMoney(); },
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const reversalMutation = createMutation(() => ({
		mutationFn: () => {
			if (!tenantAccountId) throw new Error('This rental does not have a tenant account.');
			if (!reversalTarget) throw new Error('Choose a ledger entry to reverse.');
			operationKey ??= crypto.randomUUID();
			return tenantAccounts.reverseEntry(tenantAccountId, operationKey, {
				reversesEntryId: reversalTarget.tenantLedgerEntryId,
				effectiveOn: reversalForm.effectiveOn,
				reason: reversalForm.reason.trim()
			});
		},
		onSuccess: () => { showSuccess('Ledger entry reversed.'); closeForm(); invalidateMoney(); },
		onError: (error) => showError(apiErrorMessage(error))
	}));

	function submitReceipt() {
		errors = {};
		if (!(Number(receiptForm.amount) > 0)) errors.amount = 'Enter an amount greater than zero.';
		if (!receiptForm.effectiveOn) errors.effectiveOn = 'Pick the date received.';
		if (!receiptForm.description.trim()) errors.description = 'Describe this receipt.';
		if (!receiptForm.method) errors.method = 'Choose a payment method.';
		if (!receiptForm.targetChargeEntryId) errors.targetChargeEntryId = 'Choose one charge or leave the receipt unapplied.';
		if (Object.keys(errors).length === 0) receiptMutation.mutate();
	}
	function submitCharge() {
		errors = {};
		if (!(Number(chargeForm.amount) > 0)) errors.amount = 'Enter an amount greater than zero.';
		if (!chargeForm.effectiveOn) errors.effectiveOn = 'Pick the effective date.';
		if (!chargeForm.dueOn) errors.dueOn = 'Pick the due date.';
		if (!chargeForm.description.trim()) errors.description = 'Describe this charge.';
		if (Object.keys(errors).length === 0) chargeMutation.mutate();
	}
	function submitReversal() {
		errors = {};
		if (!reversalTarget) errors.entry = 'Choose a ledger entry to reverse.';
		if (!reversalForm.effectiveOn) errors.effectiveOn = 'Pick the reversal date.';
		if (!reversalForm.reason.trim()) errors.reason = 'Describe why this entry is being reversed.';
		if (Object.keys(errors).length === 0) reversalMutation.mutate();
	}
</script>

<div class="space-y-4" data-testid="unit-rent-tab">
{#if selectedReceipt && tenantAccountId}
	<Button variant="outline" size="sm" class="gap-1" onclick={clearSelection} data-testid="payment-back-to-list"><ArrowLeft class="h-4 w-4" /> Back to payments</Button>
	<PaymentDetail tenantAccountId={tenantAccountId} tenantLedgerEntryId={selectedReceipt} expectedUnitId={dashboard.unit.id} onUnitMismatch={clearSelection} />
{:else}
	<div class="flex flex-wrap items-center justify-between gap-3 rounded-xl bg-card p-4">
		<div><p class="text-sm text-muted-foreground">Outstanding balance</p><p class="text-2xl font-bold">{money(dashboard.header.outstandingRentBalance)}</p></div>
		<div class="flex flex-wrap gap-2">
			{#if tenantAccountId && canManagePayments}
				<Button class="gap-2" onclick={() => formKind === 'receipt' ? closeForm() : openForm('receipt')} data-testid="rent-post-payment"><Receipt class="h-4 w-4" /> Record payment</Button>
				<Button variant="outline" class="gap-2" onclick={() => formKind === 'charge' ? closeForm() : openForm('charge')} data-testid="rent-post-charge"><FilePlus2 class="h-4 w-4" /> Post charge</Button>
				<Button variant="outline" class="gap-2" onclick={() => (recurringOpen = true)} data-testid="rent-recurring-charge"><Repeat2 class="h-4 w-4" /> Recurring charge</Button>
			{/if}
			<Button variant="outline" class="gap-2" onclick={onScan} data-testid="rent-scan"><ScanLine class="h-4 w-4" /> Scan payment</Button>
		</div>
	</div>

	{#if formKind && tenantAccountId}
		<div class="rounded-xl bg-card p-4" data-testid="rent-create-form">
			<div class="mb-3 flex items-center justify-between"><h3 class="text-sm font-semibold">{formKind === 'receipt' ? 'Record a payment' : formKind === 'charge' ? 'Add a manual charge' : 'Reverse ledger entry'}</h3><Button variant="ghost" size="icon" onclick={closeForm}><X class="h-4 w-4" /></Button></div>
			{#if formKind === 'receipt'}
				<div class="grid gap-3 sm:grid-cols-2">
					<label class="text-xs font-medium text-muted-foreground">Amount<Input type="text" inputmode="decimal" mask="currency" bind:value={receiptForm.amount} />{#if errors.amount}<span class="text-destructive">{errors.amount}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Date received<DatePicker bind:value={receiptForm.effectiveOn} />{#if errors.effectiveOn}<span class="text-destructive">{errors.effectiveOn}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Method<Select.Root type="single" bind:value={receiptForm.method}><Select.Trigger class="w-full">{receiptForm.method || 'Select method'}</Select.Trigger><Select.Content>{#each PAYMENT_METHODS as method}<Select.Item value={method} label={method}>{method}</Select.Item>{/each}</Select.Content></Select.Root>{#if errors.method}<span class="text-destructive">{errors.method}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Reference<Input bind:value={receiptForm.reference} placeholder="Check or confirmation number" /></label>
					<label class="text-xs font-medium text-muted-foreground">Payer<Input bind:value={receiptForm.payerName} placeholder="Optional" /></label>
					<label class="text-xs font-medium text-muted-foreground sm:col-span-2">Description<Input bind:value={receiptForm.description} />{#if errors.description}<span class="text-destructive">{errors.description}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground sm:col-span-2">
						Apply payment to
						<Select.Root type="single" bind:value={receiptForm.targetChargeEntryId}>
							<Select.Trigger class="w-full">{receiptForm.targetChargeEntryId === 'unapplied' ? 'Leave unapplied/advance receipt' : receiptForm.targetChargeEntryId ? `Charge #${receiptForm.targetChargeEntryId}` : 'Choose a charge or leave unapplied'}</Select.Trigger>
							<Select.Content>
								<Select.Item value="unapplied" label="Leave unapplied/advance receipt">Leave unapplied/advance receipt</Select.Item>
								{#each tenantLedgerQuery.data?.items ?? [] as charge (charge.tenantLedgerEntryId)}
									{#if charge.chargeAmount > 0 && charge.openAmount > 0}<Select.Item value={String(charge.tenantLedgerEntryId)} label={`${charge.description} - ${money(charge.openAmount)} still owed`}>{charge.description} - {money(charge.openAmount)} still owed</Select.Item>{/if}
								{/each}
							</Select.Content>
						</Select.Root>
						{#if errors.targetChargeEntryId}<span class="text-destructive">{errors.targetChargeEntryId}</span>{/if}
					</label>
				</div>
				<div class="mt-3 flex justify-end"><Button onclick={submitReceipt} disabled={receiptMutation.isPending}>{receiptMutation.isPending ? 'Recording…' : 'Record payment'}</Button></div>
			{:else if formKind === 'charge'}
				<div class="grid gap-3 sm:grid-cols-2">
					<label class="text-xs font-medium text-muted-foreground">Amount<Input type="text" inputmode="decimal" mask="currency" bind:value={chargeForm.amount} />{#if errors.amount}<span class="text-destructive">{errors.amount}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Effective date<DatePicker bind:value={chargeForm.effectiveOn} />{#if errors.effectiveOn}<span class="text-destructive">{errors.effectiveOn}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Due date<DatePicker bind:value={chargeForm.dueOn} />{#if errors.dueOn}<span class="text-destructive">{errors.dueOn}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Description<Input bind:value={chargeForm.description} placeholder="One-time fee or correction" />{#if errors.description}<span class="text-destructive">{errors.description}</span>{/if}</label>
				</div>
				<p class="mt-2 text-xs text-muted-foreground">Rent, late fees, deposits, and addenda come from their own workflows. Use this only for a true one-off charge.</p>
				<div class="mt-3 flex justify-end"><Button onclick={submitCharge} disabled={chargeMutation.isPending}>{chargeMutation.isPending ? 'Adding…' : 'Add charge'}</Button></div>
			{:else}
				<div class="space-y-3" data-testid="rent-reversal-form">
					{#if reversalTarget}
						<div class="rounded-lg border bg-background p-3 text-sm">
							<div class="flex items-center justify-between gap-3">
								<span><span class="font-medium">{formatStatusLabel(reversalTarget.entryType)}</span><span class="ml-2 text-muted-foreground">{reversalTarget.description}</span></span>
								<span class="font-semibold">{money(reversalTarget.amount)}</span>
							</div>
						</div>
					{/if}
					{#if errors.entry}<p class="text-xs text-destructive" role="alert">{errors.entry}</p>{/if}
					<div class="grid gap-3 sm:grid-cols-2">
						<label class="text-xs font-medium text-muted-foreground">Reversal date<DatePicker bind:value={reversalForm.effectiveOn} />{#if errors.effectiveOn}<span class="text-destructive">{errors.effectiveOn}</span>{/if}</label>
						<label class="text-xs font-medium text-muted-foreground">Reason<Input bind:value={reversalForm.reason} maxlength={500} />{#if errors.reason}<span class="text-destructive">{errors.reason}</span>{/if}</label>
					</div>
				</div>
				<div class="mt-3 flex justify-end"><Button onclick={submitReversal} disabled={reversalMutation.isPending}>{reversalMutation.isPending ? 'Reversing…' : 'Reverse entry'}</Button></div>
			{/if}
		</div>
	{/if}

	{#if !tenantAccountId}
		<p class="rounded-xl bg-card p-6 text-center text-sm text-muted-foreground">No tenant account exists for this rental yet. Payments and charges begin when the tenancy is created.</p>
	{:else}
		<section class="space-y-4" data-testid="unit-money-ledger">
			<div class="flex flex-wrap items-center justify-between gap-3">
				<div><h3 class="text-sm font-semibold">Tenant ledger</h3><p class="text-xs text-muted-foreground">Every charge, payment, and credit with the amount owed after each entry.</p></div>
				<div class="inline-flex rounded-lg bg-muted p-1" role="group" aria-label="Ledger period" data-testid="unit-money-period-switch">
					{#each [3, 6, 9, 12] as months}
						<button type="button" class="rounded-md px-3 py-1.5 text-sm font-medium transition-colors {periodMonths === months ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}" aria-pressed={periodMonths === months} onclick={() => (periodMonths = months as PeriodMonths)} data-testid={`unit-money-period-${months}`}>{months} mo</button>
					{/each}
				</div>
			</div>
			{#if tenantLedgerQuery.isLoading || monthSummaryQuery.isLoading}
				<LoadingState label="Loading tenant ledger" testid="unit-money-ledger-loading" />
			{:else if tenantLedgerQuery.isError || monthSummaryQuery.isError}
				<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="unit-money-ledger-error"><p class="text-sm font-medium text-destructive">The tenant ledger could not be loaded.</p><Button class="mt-3" variant="outline" size="sm" onclick={() => { tenantLedgerQuery.refetch(); monthSummaryQuery.refetch(); }}>Try again</Button></div>
			{:else if (monthSummaryQuery.data?.length ?? 0) === 0}
				<p class="rounded-xl bg-card p-4 text-sm text-muted-foreground">No account activity in this period.</p>
			{:else}
				<div class="space-y-4">
					{#each monthSummaryQuery.data ?? [] as month (`${month.year}-${month.month}`)}
						<MonthGroup {month}>
							<div class="hidden grid-cols-[7rem_minmax(0,1fr)_8rem_9rem_9rem] gap-3 border-b px-4 py-2 text-xs font-medium uppercase tracking-wide text-muted-foreground md:grid"><span>Date</span><span>What happened</span><span class="text-right">Charges</span><span class="text-right">Payments / credits</span><span class="text-right">Amount owed</span></div>
							{#each tenantLedgerQuery.data?.items ?? [] as row (row.tenantLedgerEntryId)}
								{#if belongsToMonth(row, month)}
									<button type="button" class="grid w-full gap-2 border-b px-4 py-3 text-left transition-colors hover:bg-muted/40 md:grid-cols-[7rem_minmax(0,1fr)_8rem_9rem_9rem] md:items-center md:gap-3" onclick={() => (selectedLedgerRow = row)} data-testid={`unit-money-ledger-row-${row.tenantLedgerEntryId}`}>
										<div><time class="text-sm font-medium" datetime={row.effectiveOn}>{formatDateOnly(row.effectiveOn)}</time><p class="text-xs text-muted-foreground">Entered {formatDateOnly(row.postedAtUtc)}</p></div>
										<div class="min-w-0"><div class="flex flex-wrap items-center gap-2"><LedgerTypeBadge type={row.type} /><span class="font-medium">{row.description}</span></div>{#if row.recurringScheduleContext || row.sourceDocumentContext}<p class="mt-1 text-xs text-muted-foreground">{row.recurringScheduleContext ?? row.sourceDocumentContext}</p>{/if}{#if row.reversesEntryId || row.replacedByEntryId}<p class="mt-1 text-xs text-muted-foreground">{row.reversesEntryId ? `Reverses entry #${row.reversesEntryId}` : `Reversed by entry #${row.replacedByEntryId}`}</p>{/if}</div>
										<div class="flex justify-between text-sm md:block md:text-right"><span class="text-muted-foreground md:hidden">Charges</span>{#if row.chargeAmount}<LedgerAmount amount={row.chargeAmount} currency={row.currency} tone="charge" />{:else}<span class="text-muted-foreground">—</span>{/if}</div>
										<div class="flex justify-between text-sm md:block md:text-right"><span class="text-muted-foreground md:hidden">Payments / credits</span>{#if row.paymentAmount}<LedgerAmount amount={row.paymentAmount} currency={row.currency} tone="payment" />{:else if row.creditAmount}<LedgerAmount amount={row.creditAmount} currency={row.currency} tone="credit" />{:else}<span class="text-muted-foreground">—</span>{/if}</div>
										<div class="flex items-center justify-between gap-2 text-sm md:justify-end"><span class="text-muted-foreground md:hidden">Amount owed</span><LedgerAmount amount={row.runningAmountOwed} currency={row.currency} />{#if canManagePayments && canReverseTenantLedgerEntry({ entryType: row.type, reversesEntryId: row.reversesEntryId, hasReversal: row.replacedByEntryId != null })}<Button variant="ghost" size="icon" onclick={(event) => { event.stopPropagation(); openReversal({ tenantAccountId, leaseManagementId: dashboard.leaseManagementId ?? 0, tenantLedgerEntryId: row.tenantLedgerEntryId, publicId: row.publicId, entryType: row.type, direction: row.chargeAmount > 0 ? 'Debit' : 'Credit', amount: row.chargeAmount || row.paymentAmount || row.creditAmount, currency: row.currency, effectiveOn: row.effectiveOn, dueOn: row.dueOn, postedAtUtc: row.postedAtUtc, description: row.description, businessKey: row.sourcePublicId ?? row.publicId, reversesEntryId: row.reversesEntryId, hasReversal: row.replacedByEntryId != null }); }} aria-label={`Reverse ${row.description}`} data-testid={`rent-reverse-ledger-entry-${row.tenantLedgerEntryId}`}><Undo2 class="h-4 w-4" /></Button>{/if}</div>
									</button>
								{/if}
							{/each}
						</MonthGroup>
					{/each}
				</div>
				{#if tenantLedgerQuery.data && tenantLedgerQuery.data.totalCount > tenantLedgerQuery.data.items.length}<p class="text-sm text-destructive" role="alert">This period contains more than {tenantLedgerQuery.data.items.length} entries. Choose a shorter period to see every row.</p>{/if}
			{/if}
		</section>
	{/if}
	{#if recurringOpen && tenantAccountId && dashboard.leaseManagementId && canManagePayments}<RecurringChargeDialog open tenantAccountId={tenantAccountId} leaseAgreementId={dashboard.currentLease?.id} leaseManagementId={dashboard.leaseManagementId} propertyId={dashboard.unit.propertyId} unitId={dashboard.unit.id} onClose={() => (recurringOpen = false)} />{/if}
	{#if selectedLedgerRow && tenantAccountId}<LedgerRowSheet tenantRow={selectedLedgerRow} tenantAccountId={tenantAccountId} onClose={() => (selectedLedgerRow = null)} />{/if}
{/if}
</div>
