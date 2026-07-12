<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { untrack } from 'svelte';
	import type { UnitDashboard, PaymentReceipt } from '$lib/types';
	import { payments } from '$lib/api/endpoints/payments';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { money } from '../money';
	import { formatDateOnly } from '$lib/utils/date';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import PaymentDetail from '$lib/components/records/PaymentDetail.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { PAYMENT_METHODS } from '$lib/constants/payments';
	import { X, ScanLine, ArrowLeft, Receipt, FilePlus2 } from '@lucide/svelte';

	let { dashboard, onScan, tabQuery = 'rent', ledgerQuery }: {
		dashboard: UnitDashboard;
		onScan: () => void;
		tabQuery?: string;
		ledgerQuery?: string;
	} = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const tenantAccountId = $derived(dashboard.currentLease?.tenantAccountId ?? null);
	const leaseManagementId = $derived(dashboard.currentLease?.leaseManagementId ?? null);
	const selectedReceipt = $derived(Number(page.url.searchParams.get('payment')) || null);
	const PAGE_SIZE = 20;
	const today = () => new Date().toISOString().slice(0, 10);

	function unitUrl(payment?: number) {
		const url = new URL(`/units/${dashboard.unit.id}`, page.url.origin);
		url.searchParams.set('tab', tabQuery);
		if (ledgerQuery) url.searchParams.set('ledger', ledgerQuery);
		if (payment) url.searchParams.set('payment', String(payment));
		return `${url.pathname}${url.search}`;
	}

	function openReceipt(id: number) {
		goto(unitUrl(id), { keepFocus: true, noScroll: true });
	}
	function clearSelection() {
		goto(unitUrl(), { replaceState: true, keepFocus: true, noScroll: true });
	}

	let receiptPage = $state(1);
	let receiptItems = $state<PaymentReceipt[]>([]);
	const receiptsQuery = createQuery(() => ({
		queryKey: ['payments', portfolioId, { tenantAccountId, leaseManagementId }, receiptPage, PAGE_SIZE],
		enabled: !!tenantAccountId && !!leaseManagementId && portfolioId > 0,
		queryFn: () => payments.listPage(portfolioId, {
			tenantAccountId: tenantAccountId ?? undefined,
			leaseManagementId: leaseManagementId ?? undefined,
			skip: (receiptPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: '-receivedOn'
		})
	}));

	$effect(() => {
		void tenantAccountId;
		receiptPage = 1;
		receiptItems = [];
	});
	$effect(() => {
		const result = receiptsQuery.data;
		if (!result) return;
		if (result.skip === 0) receiptItems = result.items;
		else {
			const existing = untrack(() => receiptItems);
			const ids = new Set(existing.map((item) => item.id));
			receiptItems = [...existing, ...result.items.filter((item) => !ids.has(item.id))];
		}
	});

	const totalReceipts = $derived(receiptsQuery.data?.totalCount ?? receiptItems.length);
	const hasMore = $derived(receiptItems.length < totalReceipts);

	function invalidateMoney() {
		receiptItems = [];
		receiptPage = 1;
		queryClient.invalidateQueries({ queryKey: ['payments', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['unit-dashboard', dashboard.unit.id] });
		queryClient.invalidateQueries({ queryKey: ['unit-timeline', dashboard.unit.id] });
		queryClient.invalidateQueries({ queryKey: ['accounting'] });
	}

	type FormKind = 'receipt' | 'charge' | null;
	let formKind = $state<FormKind>(null);
	let operationKey = $state<string | null>(null);
	let errors = $state<Record<string, string>>({});
	let receiptForm = $state({ amount: '', effectiveOn: today(), description: 'Tenant payment', method: '', reference: '', payerName: '' });
	let chargeForm = $state({ amount: '', effectiveOn: today(), dueOn: today(), description: '' });

	function openForm(kind: Exclude<FormKind, null>) {
		formKind = kind;
		operationKey = null;
		errors = {};
		if (kind === 'receipt') receiptForm = { amount: '', effectiveOn: today(), description: 'Tenant payment', method: '', reference: '', payerName: '' };
		else chargeForm = { amount: '', effectiveOn: today(), dueOn: today(), description: '' };
	}
	function closeForm() {
		formKind = null;
		operationKey = null;
		errors = {};
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
</script>

<div class="space-y-4" data-testid="unit-rent-tab">
{#if selectedReceipt}
	<Button variant="outline" size="sm" class="gap-1" onclick={clearSelection} data-testid="payment-back-to-list"><ArrowLeft class="h-4 w-4" /> Back to receipts</Button>
	<PaymentDetail paymentId={selectedReceipt} expectedUnitId={dashboard.unit.id} onUnitMismatch={clearSelection} />
{:else}
	<div class="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card p-4">
		<div><p class="text-sm text-muted-foreground">Outstanding balance</p><p class="text-2xl font-bold">{money(dashboard.header.outstandingRentBalance)}</p></div>
		<div class="flex flex-wrap gap-2">
			{#if tenantAccountId}
				<Button class="gap-2" onclick={() => formKind === 'receipt' ? closeForm() : openForm('receipt')} data-testid="rent-post-payment"><Receipt class="h-4 w-4" /> Record receipt</Button>
				<Button variant="outline" class="gap-2" onclick={() => formKind === 'charge' ? closeForm() : openForm('charge')} data-testid="rent-post-charge"><FilePlus2 class="h-4 w-4" /> Add charge</Button>
			{/if}
			<Button variant="outline" class="gap-2" onclick={onScan} data-testid="rent-scan"><ScanLine class="h-4 w-4" /> Scan payment</Button>
		</div>
	</div>

	{#if formKind && tenantAccountId}
		<div class="rounded-xl border border-border bg-muted/20 p-4" data-testid="rent-create-form">
			<div class="mb-3 flex items-center justify-between"><h3 class="text-sm font-semibold">{formKind === 'receipt' ? 'Record payment receipt' : 'Add a manual charge'}</h3><Button variant="ghost" size="icon" onclick={closeForm}><X class="h-4 w-4" /></Button></div>
			{#if formKind === 'receipt'}
				<div class="grid gap-3 sm:grid-cols-2">
					<label class="text-xs font-medium text-muted-foreground">Amount<Input type="text" inputmode="decimal" mask="currency" bind:value={receiptForm.amount} />{#if errors.amount}<span class="text-destructive">{errors.amount}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Date received<DatePicker bind:value={receiptForm.effectiveOn} />{#if errors.effectiveOn}<span class="text-destructive">{errors.effectiveOn}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Method<Select.Root type="single" bind:value={receiptForm.method}><Select.Trigger class="w-full">{receiptForm.method || 'Select method'}</Select.Trigger><Select.Content>{#each PAYMENT_METHODS as method}<Select.Item value={method} label={method}>{method}</Select.Item>{/each}</Select.Content></Select.Root>{#if errors.method}<span class="text-destructive">{errors.method}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Reference<Input bind:value={receiptForm.reference} placeholder="Check or confirmation number" /></label>
					<label class="text-xs font-medium text-muted-foreground">Payer<Input bind:value={receiptForm.payerName} placeholder="Optional" /></label>
					<label class="text-xs font-medium text-muted-foreground sm:col-span-2">Description<Input bind:value={receiptForm.description} />{#if errors.description}<span class="text-destructive">{errors.description}</span>{/if}</label>
				</div>
				<div class="mt-3 flex justify-end"><Button onclick={submitReceipt} disabled={receiptMutation.isPending}>{receiptMutation.isPending ? 'Recording…' : 'Record receipt'}</Button></div>
			{:else}
				<div class="grid gap-3 sm:grid-cols-2">
					<label class="text-xs font-medium text-muted-foreground">Amount<Input type="text" inputmode="decimal" mask="currency" bind:value={chargeForm.amount} />{#if errors.amount}<span class="text-destructive">{errors.amount}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Effective date<DatePicker bind:value={chargeForm.effectiveOn} />{#if errors.effectiveOn}<span class="text-destructive">{errors.effectiveOn}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Due date<DatePicker bind:value={chargeForm.dueOn} />{#if errors.dueOn}<span class="text-destructive">{errors.dueOn}</span>{/if}</label>
					<label class="text-xs font-medium text-muted-foreground">Description<Input bind:value={chargeForm.description} placeholder="One-time fee or correction" />{#if errors.description}<span class="text-destructive">{errors.description}</span>{/if}</label>
				</div>
				<p class="mt-2 text-xs text-muted-foreground">Rent, late fees, deposits, and addenda come from their own workflows. Use this only for a true one-off charge.</p>
				<div class="mt-3 flex justify-end"><Button onclick={submitCharge} disabled={chargeMutation.isPending}>{chargeMutation.isPending ? 'Adding…' : 'Add charge'}</Button></div>
			{/if}
		</div>
	{/if}

	{#if !tenantAccountId}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">No tenant account exists for this rental yet. Receipts and charges begin when the tenancy is created.</p>
	{:else if receiptsQuery.isLoading}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">Loading receipts…</p>
	{:else if receiptItems.length === 0}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">No receipts yet. Record one manually or scan a payment.</p>
	{:else}
		<ul class="space-y-2" data-testid="rent-payments">
			{#each receiptItems as receipt (receipt.id)}
				<li class="rounded-xl border bg-card"><button type="button" class="flex w-full items-center justify-between gap-2 p-3 text-left" onclick={() => openReceipt(receipt.id)}><span><span class="font-medium">{formatDateOnly(receipt.receivedOn)}</span><span class="ml-2 text-muted-foreground">{receipt.paymentMethodSummary || receipt.description}</span></span><span class="font-semibold">{money(receipt.amount)}</span></button></li>
			{/each}
		</ul>
		{#if hasMore}<div class="flex justify-center"><Button variant="outline" size="sm" onclick={() => (receiptPage += 1)} disabled={receiptsQuery.isFetching}>{receiptsQuery.isFetching ? 'Loading…' : 'Load more'}</Button></div>{/if}
	{/if}
{/if}
</div>
