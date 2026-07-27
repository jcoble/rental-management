<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { pushState, replaceState } from '$app/navigation';
	import { page } from '$app/state';
	import type { UnitDashboard } from '$lib/types';
	import { tenantAccounts } from '$lib/api/endpoints/tenant-accounts';
	import type { TenantLedgerEntry } from '$lib/api/endpoints/tenant-accounts';
	import { payments } from '$lib/api/endpoints/payments';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { canReverseTenantLedgerEntry, money, tenantLedgerReversalReason, unitMoneyIdentity } from '../money';
	import { formatDateOnly } from '$lib/utils/date';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import PaymentDetail from '$lib/components/records/PaymentDetail.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { PAYMENT_METHODS } from '$lib/constants/payments';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { X, ScanLine, ArrowLeft, Receipt, FilePlus2, Undo2 } from '@lucide/svelte';

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
	const selectedReceipt = $derived(
		page.state.unitPaymentId ?? (Number(page.url.searchParams.get('payment')) || null)
	);
	const PAGE_SIZE = 20;
	const today = () => new Date().toISOString().slice(0, 10);

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

	let receiptPage = $state(1);
	let activityPage = $state(1);
	let chargePage = $state(1);
	let depositPage = $state(1);
	const activityQuery = createQuery(() => ({
		queryKey: ['tenant-account-entries', tenantAccountId, 'activity', activityPage, PAGE_SIZE],
		enabled: !!tenantAccountId,
		queryFn: () => tenantAccounts.accountEntriesPage(tenantAccountId as number, {
			skip: (activityPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: '-postedAtUtc'
		})
	}));
	const chargesQuery = createQuery(() => ({
		queryKey: ['tenant-account-charges', tenantAccountId, chargePage, PAGE_SIZE],
		enabled: !!tenantAccountId,
		queryFn: () => tenantAccounts.chargesPage(tenantAccountId as number, {
			skip: (chargePage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: '-effectiveOn'
		})
	}));
	const depositsQuery = createQuery(() => ({
		queryKey: ['tenant-account-deposits', tenantAccountId, depositPage, PAGE_SIZE],
		enabled: !!tenantAccountId,
		queryFn: () => tenantAccounts.depositsPage({
			tenantAccountId: tenantAccountId as number,
			skip: (depositPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: '-createdAtUtc'
		})
	}));
	const receiptsQuery = createQuery(() => ({
		queryKey: ['tenant-account-entries', tenantAccountId, 'receipts', receiptPage, PAGE_SIZE],
		enabled: !!tenantAccountId,
		queryFn: () => tenantAccounts.accountEntriesPage(tenantAccountId as number, {
			entryType: 'PaymentReceipt',
			skip: (receiptPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: '-effectiveOn'
		})
	}));

	$effect(() => {
		void tenantAccountId;
		receiptPage = 1;
		activityPage = 1;
		chargePage = 1;
		depositPage = 1;
	});

	const receiptItems = $derived(receiptsQuery.data?.items ?? []);

	function invalidateMoney() {
		receiptPage = 1;
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
	let receiptForm = $state({ amount: '', effectiveOn: today(), description: 'Tenant payment', method: '', reference: '', payerName: '' });
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
		if (kind === 'receipt') receiptForm = { amount: '', effectiveOn: today(), description: 'Tenant payment', method: '', reference: '', payerName: '' };
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
				allocateOldestCharges: true
			});
		},
		onSuccess: () => { showSuccess('Receipt recorded.'); closeForm(); invalidateMoney(); },
		onError: (error) => showError(apiErrorMessage(error))
	}));

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
			{#if tenantAccountId}
				<Button class="gap-2" onclick={() => formKind === 'receipt' ? closeForm() : openForm('receipt')} data-testid="rent-post-payment"><Receipt class="h-4 w-4" /> Record payment</Button>
				<Button variant="outline" class="gap-2" onclick={() => formKind === 'charge' ? closeForm() : openForm('charge')} data-testid="rent-post-charge"><FilePlus2 class="h-4 w-4" /> Add charge</Button>
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
		<section class="space-y-2" data-testid="account-activity-section">
			<h3 class="text-sm font-semibold">Account activity</h3>
			{#if activityQuery.isLoading}
				<LoadingState label="Loading account activity" testid="account-activity-loading" />
			{:else if activityQuery.isError}
				<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="account-activity-error">
					<p class="text-sm font-medium text-destructive">Account activity could not be loaded.</p>
					<Button class="mt-3" variant="outline" size="sm" onclick={() => activityQuery.refetch()}>Try again</Button>
				</div>
			{:else if (activityQuery.data?.items.length ?? 0) === 0}
				<p class="rounded-xl bg-card p-4 text-sm text-muted-foreground">No account activity yet.</p>
			{:else}
				<ul class="divide-y overflow-hidden rounded-xl bg-card">
					{#each activityQuery.data?.items ?? [] as entry (entry.tenantLedgerEntryId)}
						<li class="text-sm">
							{#if entry.entryType === 'PaymentReceipt'}
								<button type="button" class="flex w-full items-center justify-between gap-3 p-3 text-left hover:bg-muted/40" onclick={() => openReceipt(entry.tenantLedgerEntryId)}>
									<span><span class="font-medium">{formatStatusLabel(entry.entryType)}</span><span class="ml-2 text-muted-foreground">{entry.description}</span></span>
									<span class="font-semibold">{money(entry.amount)}</span>
								</button>
							{:else}
								<div class="flex items-center justify-between gap-3 p-3">
									<span><span class="font-medium">{formatStatusLabel(entry.entryType)}</span><span class="ml-2 text-muted-foreground">{entry.description}</span></span>
									<span class="flex items-center gap-2">
										<span class="font-semibold">{money(entry.amount)}</span>
										{#if canReverseTenantLedgerEntry(entry)}
											<Button variant="outline" size="sm" class="gap-1" onclick={() => openReversal(entry)} aria-label={`Reverse ${formatStatusLabel(entry.entryType)} entry ${entry.tenantLedgerEntryId}`} data-testid={`rent-reverse-ledger-entry-${entry.tenantLedgerEntryId}`}><Undo2 class="h-4 w-4" /> Reverse</Button>
										{/if}
									</span>
								</div>
							{/if}
						</li>
					{/each}
				</ul>
				{#if activityQuery.data && activityQuery.data.totalCount > 0}<div class="flex items-center justify-between"><Button variant="outline" size="sm" onclick={() => (activityPage -= 1)} disabled={activityPage === 1 || activityQuery.isFetching}>Previous</Button><span class="text-sm text-muted-foreground">Page {activityPage} of {Math.ceil(activityQuery.data.totalCount / PAGE_SIZE)} · {activityQuery.data.totalCount} entries</span><Button variant="outline" size="sm" onclick={() => (activityPage += 1)} disabled={activityPage * PAGE_SIZE >= activityQuery.data.totalCount || activityQuery.isFetching}>Next</Button></div>{/if}
			{/if}
		</section>

		<section class="space-y-2 border-t pt-4" data-testid="charge-allocation-section">
			<div>
				<h3 class="text-sm font-semibold">Rent and charges</h3>
				<p class="text-xs text-muted-foreground">See what has been paid and what is still owed.</p>
			</div>
			{#if chargesQuery.isLoading}
				<LoadingState label="Loading charges" testid="charges-loading" />
			{:else if chargesQuery.isError}
				<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="charges-error">
					<p class="text-sm font-medium text-destructive">Charges could not be loaded.</p>
					<Button class="mt-3" variant="outline" size="sm" onclick={() => chargesQuery.refetch()}>Try again</Button>
				</div>
			{:else if (chargesQuery.data?.items.length ?? 0) === 0}
				<p class="rounded-xl bg-card p-4 text-sm text-muted-foreground">No rent or other charges yet.</p>
			{:else}
				<ul class="divide-y overflow-hidden rounded-xl bg-card">
					{#each chargesQuery.data?.items ?? [] as charge (charge.tenantLedgerEntryId)}
						<li class="p-3 text-sm">
							<div class="flex items-center justify-between gap-3">
								<span class="font-medium">{charge.description}</span>
								<span class="font-semibold">{charge.openAmount <= 0 ? 'Paid in full' : `${money(charge.openAmount)} still owed`}</span>
							</div>
							<p class="text-xs text-muted-foreground">{charge.netAllocations >= charge.originalAmount ? `${money(charge.originalAmount)} paid` : `${money(charge.netAllocations)} paid of ${money(charge.originalAmount)}`}</p>
						</li>
					{/each}
				</ul>
				{#if chargesQuery.data && chargesQuery.data.totalCount > 0}<div class="flex items-center justify-between"><Button variant="outline" size="sm" onclick={() => (chargePage -= 1)} disabled={chargePage === 1 || chargesQuery.isFetching}>Previous</Button><span class="text-sm text-muted-foreground">Page {chargePage} of {Math.ceil(chargesQuery.data.totalCount / PAGE_SIZE)} · {chargesQuery.data.totalCount} charges</span><Button variant="outline" size="sm" onclick={() => (chargePage += 1)} disabled={chargePage * PAGE_SIZE >= chargesQuery.data.totalCount || chargesQuery.isFetching}>Next</Button></div>{/if}
			{/if}
		</section>

		<section class="space-y-2 border-t pt-4" data-testid="deposits-section">
			<h3 class="text-sm font-semibold">Deposits</h3>
			{#if depositsQuery.isLoading}
				<LoadingState label="Loading deposits" testid="deposits-loading" />
			{:else if depositsQuery.isError}
				<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="deposits-error">
					<p class="text-sm font-medium text-destructive">Deposits could not be loaded.</p>
					<Button class="mt-3" variant="outline" size="sm" onclick={() => depositsQuery.refetch()}>Try again</Button>
				</div>
			{:else if (depositsQuery.data?.items.length ?? 0) === 0}
				<p class="rounded-xl bg-card p-4 text-sm text-muted-foreground">No security deposit is recorded for this rental.</p>
			{:else}
				<ul class="divide-y overflow-hidden rounded-xl bg-card">
					{#each depositsQuery.data?.items ?? [] as deposit (deposit.securityDepositAccountId)}
						<li class="flex items-center justify-between gap-3 p-3 text-sm">
							<span><span class="font-medium">{deposit.status}</span><span class="ml-2 text-muted-foreground">Security deposit</span></span>
							<span class="font-semibold">{money(deposit.heldBalance)} held</span>
						</li>
					{/each}
				</ul>
				{#if depositsQuery.data && depositsQuery.data.totalCount > 0}<div class="flex items-center justify-between"><Button variant="outline" size="sm" onclick={() => (depositPage -= 1)} disabled={depositPage === 1 || depositsQuery.isFetching}>Previous</Button><span class="text-sm text-muted-foreground">Page {depositPage} of {Math.ceil(depositsQuery.data.totalCount / PAGE_SIZE)} · {depositsQuery.data.totalCount} deposit records</span><Button variant="outline" size="sm" onclick={() => (depositPage += 1)} disabled={depositPage * PAGE_SIZE >= depositsQuery.data.totalCount || depositsQuery.isFetching}>Next</Button></div>{/if}
			{/if}
		</section>

		<section class="space-y-2 border-t pt-4" data-testid="rent-payments">
			<h3 class="text-sm font-semibold">Payments received</h3>
			{#if receiptsQuery.isLoading}
				<LoadingState label="Loading payment receipts" testid="rent-receipts-loading" />
			{:else if receiptsQuery.isError}
				<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="rent-receipts-error">
					<p class="text-sm font-medium text-destructive">Could not load payment receipts.</p>
					<Button class="mt-3" variant="outline" size="sm" onclick={() => receiptsQuery.refetch()}>Try again</Button>
				</div>
			{:else if receiptItems.length === 0}
				<p class="rounded-xl bg-card p-4 text-sm text-muted-foreground">No payments received yet. Record one manually or scan a payment.</p>
			{:else}
				<ul class="divide-y overflow-hidden rounded-xl bg-card">
					{#each receiptItems as receipt (receipt.tenantLedgerEntryId)}
						<li><button type="button" class="flex w-full items-center justify-between gap-2 p-3 text-left hover:bg-muted/40" onclick={() => openReceipt(receipt.tenantLedgerEntryId)}><span><span class="font-medium">{formatDateOnly(receipt.effectiveOn)}</span><span class="ml-2 text-muted-foreground">{receipt.description}</span></span><span class="font-semibold">{money(receipt.amount)}</span></button></li>
					{/each}
				</ul>
				{#if receiptsQuery.data && receiptsQuery.data.totalCount > 0}<div class="flex items-center justify-between"><Button variant="outline" size="sm" onclick={() => (receiptPage -= 1)} disabled={receiptPage === 1 || receiptsQuery.isFetching}>Previous</Button><span class="text-sm text-muted-foreground">Page {receiptPage} of {Math.ceil(receiptsQuery.data.totalCount / PAGE_SIZE)} · {receiptsQuery.data.totalCount} payments</span><Button variant="outline" size="sm" onclick={() => (receiptPage += 1)} disabled={receiptPage * PAGE_SIZE >= receiptsQuery.data.totalCount || receiptsQuery.isFetching}>Next</Button></div>{/if}
			{/if}
		</section>
	{/if}
{/if}
</div>
