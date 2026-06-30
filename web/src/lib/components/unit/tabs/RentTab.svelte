<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { untrack } from 'svelte';
	import type { UnitDashboard, Payment } from '$lib/types';
	import { payments as paymentsApi } from '$lib/api/endpoints/payments';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { paymentSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { money } from '../money';
	import { formatDateOnly } from '$lib/utils/date';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import PaymentDetail from '$lib/components/records/PaymentDetail.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { PAYMENT_METHODS } from '$lib/constants/payments';
	import { Plus, X, ScanLine, ArrowLeft } from '@lucide/svelte';

	let {
		dashboard,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onScan: () => void;
	} = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const leaseId = $derived(dashboard.currentLease?.id);

	// A selected payment folds its full detail inline (?payment=<id> on the unit URL); otherwise the list shows.
	const selectedPayment = $derived(Number(page.url.searchParams.get('payment')) || null);

	// Selecting a row is a real navigation step (no replaceState) so Back returns to the list.
	function openPayment(id: number) {
		goto('/units/' + dashboard.unit.id + '?tab=rent&payment=' + id, { keepFocus: true, noScroll: true });
	}

	// Clearing the selection drops ?payment= (replaceState — peer of the list, not a new history step).
	function clearSelection() {
		goto('/units/' + dashboard.unit.id + '?tab=rent', { replaceState: true, keepFocus: true, noScroll: true });
	}

	const PAYMENT_TYPES = ['Rent', 'SecurityDeposit', 'LateFee', 'Utility', 'Other'];
	// 'Partial' is intentionally omitted here: this quick form has no "Amount paid" field, so a Partial
	// can never satisfy the payment schema's 0 < amountPaid < amount rule and would fail silently. Record
	// partial payments from the Money page's New Payment dialog, which has the "Amount paid" input.
	const PAYMENT_STATUSES = ['Scheduled', 'Paid', 'Late', 'Waived', 'Failed', 'Refunded'];
	const PAYMENT_PAGE_SIZE = 20;
	const today = () => new Date().toISOString().slice(0, 10);

	// Payments for the unit's current lease (the dominant case). The filter is DB-side (?leaseId=).
	let paymentPage = $state(1);
	let paymentItems = $state<Payment[]>([]);
	const paymentsQuery = createQuery(() => ({
		queryKey: ['payments', portfolioId, { leaseId }, paymentPage, PAYMENT_PAGE_SIZE],
		enabled: !!leaseId && portfolioId > 0,
		queryFn: () => paymentsApi.listPage(portfolioId, {
			leaseId,
			skip: (paymentPage - 1) * PAYMENT_PAGE_SIZE,
			take: PAYMENT_PAGE_SIZE,
			sort: '-dueDate',
		}),
	}));

	$effect(() => {
		void leaseId;
		paymentPage = 1;
		paymentItems = [];
	});

	$effect(() => {
		const page = paymentsQuery.data;
		if (!page) return;
		if (page.skip === 0) {
			paymentItems = page.items;
			return;
		}
		const currentItems = untrack(() => paymentItems);
		const seen = new Set(currentItems.map((p) => p.id));
		paymentItems = [...currentItems, ...page.items.filter((p) => !seen.has(p.id))];
	});

	const list = $derived(paymentItems);
	const totalPayments = $derived(paymentsQuery.data?.totalCount ?? paymentItems.length);
	const hasMorePayments = $derived(paymentItems.length < totalPayments);

	function invalidate({ resetPayments = true }: { resetPayments?: boolean } = {}) {
		if (resetPayments) {
			paymentItems = [];
			paymentPage = 1;
		}
		queryClient.invalidateQueries({ queryKey: ['payments', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['unit-dashboard', dashboard.unit.id] });
		queryClient.invalidateQueries({ queryKey: ['unit-timeline', dashboard.unit.id] });
	}

	function loadMorePayments() {
		if (paymentsQuery.isFetching || !hasMorePayments) return;
		paymentPage += 1;
	}

	// ── Inline "post payment" create form (reuses the app's paymentSchema + form conventions) ──
	const emptyCreate = () => ({
		amount: '',
		dueDate: today(),
		paymentType: 'Rent',
		status: 'Paid',
		method: '',
		externalReference: '',
		notes: '',
	});
	let showCreate = $state(false);
	let createForm = $state(emptyCreate());
	let createErrors = $state<Record<string, string>>({});

	function clearCreateError(field: string) {
		const next = clearFieldError(createErrors, field);
		if (next !== createErrors) createErrors = next;
	}

	$effect(() => {
		if (createForm.amount) clearCreateError('amount');
	});
	$effect(() => {
		if (createForm.dueDate) clearCreateError('dueDate');
	});

	function openCreate() {
		createForm = emptyCreate();
		createErrors = {};
		showCreate = true;
	}
	function closeCreate() {
		showCreate = false;
		createErrors = {};
	}

	const createMut = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => paymentsApi.create(data),
		onSuccess: (payment: Payment) => {
			showSuccess('Payment posted.');
			closeCreate();
			paymentPage = 1;
			paymentItems = [payment, ...paymentItems.filter((item) => item.id !== payment.id)];
			invalidate({ resetPayments: false });
		},
		onError: (e) => showError(apiErrorMessage(e)),
	}));

	function submitCreate() {
		if (!leaseId) return;
		const result = parseForm(paymentSchema, { ...createForm, leaseId: String(leaseId) });
		if (result.errors) {
			createErrors = result.errors;
			return;
		}
		createErrors = {};
		// result.data already carries leaseId (validated by the schema); add the portfolio scope.
		const data: Record<string, unknown> = { portfolioId, ...result.data };
		// A payment marked Paid with no explicit paid date defaults to its due date.
		if (createForm.status === 'Paid' && !data.paidDate) data.paidDate = createForm.dueDate;
		createMut.mutate(data);
	}
</script>

<div class="space-y-4" data-testid="unit-rent-tab">
{#if selectedPayment}
	<!-- Folded payment detail: the same <PaymentDetail> the generic /accounting/payments/[id] page mounts. -->
	<Button variant="outline" size="sm" class="gap-1" onclick={clearSelection} data-testid="payment-back-to-list">
		<ArrowLeft class="h-4 w-4" /> Back to payments
	</Button>
	<PaymentDetail
		paymentId={selectedPayment}
		onDeleted={clearSelection}
		expectedUnitId={dashboard.unit.id}
		onUnitMismatch={clearSelection}
	/>
{:else}
	<div class="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card p-4">
		<div>
			<p class="text-sm text-muted-foreground">Outstanding balance</p>
			<p class="text-2xl font-bold">{money(dashboard.header.outstandingRentBalance)}</p>
		</div>
		<div class="flex flex-wrap gap-2">
			{#if leaseId}
				<Button class="gap-2" onclick={() => (showCreate ? closeCreate() : openCreate())} data-testid="rent-post-payment">
					{#if showCreate}<X class="h-4 w-4" /> Cancel{:else}<Plus class="h-4 w-4" /> Post payment{/if}
				</Button>
			{/if}
			<Button variant="outline" class="gap-2" onclick={() => onScan()} data-testid="rent-scan">
				<ScanLine class="h-4 w-4" /> Scan receipt
			</Button>
		</div>
	</div>

	<!-- Inline post-payment form (revealed below the header, not a drawer/modal). -->
	{#if showCreate && leaseId}
		<div class="rounded-xl border border-border bg-muted/20 p-4" data-testid="rent-create-form">
			<h3 class="mb-3 text-sm font-semibold">Post a payment</h3>
			<div class="grid gap-3 sm:grid-cols-2">
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="rent-amount">Amount</label>
					<Input id="rent-amount" data-testid="rent-amount-input" type="text" inputmode="decimal" mask="currency" bind:value={createForm.amount} placeholder="0.00" />
					{#if createErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="rent-amount-error">{createErrors.amount}</p>{/if}
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="rent-due">Date</label>
					<DatePicker id="rent-due" testid="rent-due-input" bind:value={createForm.dueDate} />
					{#if createErrors.dueDate}<p class="mt-1 text-xs text-destructive" data-testid="rent-due-error">{createErrors.dueDate}</p>{/if}
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="rent-type">Type</label>
					<Select.Root type="single" bind:value={createForm.paymentType}>
						<Select.Trigger id="rent-type" class="w-full" data-testid="rent-type-input">{createForm.paymentType}</Select.Trigger>
						<Select.Content>
							{#each PAYMENT_TYPES as t}<Select.Item value={t} label={t}>{t}</Select.Item>{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="rent-status">Status</label>
					<Select.Root type="single" bind:value={createForm.status}>
						<Select.Trigger id="rent-status" class="w-full" data-testid="rent-status-input">{createForm.status}</Select.Trigger>
						<Select.Content>
							{#each PAYMENT_STATUSES as s}<Select.Item value={s} label={s}>{s}</Select.Item>{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="rent-method">Method</label>
					<Select.Root type="single" bind:value={createForm.method}>
						<Select.Trigger id="rent-method" class="w-full" data-testid="rent-method-input">
							{createForm.method || 'Select method'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="No method">No method</Select.Item>
							{#each PAYMENT_METHODS as m}<Select.Item value={m} label={m}>{m}</Select.Item>{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="rent-reference">Reference</label>
					<Input id="rent-reference" data-testid="rent-reference-input" bind:value={createForm.externalReference} placeholder="Check #, transaction id, memo" />
				</div>
				<div class="sm:col-span-2">
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="rent-notes">Notes</label>
					<textarea
						id="rent-notes"
						data-testid="rent-notes-input"
						bind:value={createForm.notes}
						rows="2"
						class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
						placeholder="Optional payment note"
					></textarea>
				</div>
			</div>
			<div class="mt-3 flex justify-end gap-2">
				<Button variant="outline" size="sm" onclick={closeCreate}>Cancel</Button>
				<Button size="sm" disabled={createMut.isPending} onclick={submitCreate} data-testid="rent-create-submit">
					{createMut.isPending ? 'Posting…' : 'Post payment'}
				</Button>
			</div>
		</div>
	{/if}

	{#if !leaseId}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">
			No lease on this unit yet — rent activity appears once a lease exists.
		</p>
	{:else if paymentsQuery.isLoading}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">Loading payments…</p>
	{:else if list.length === 0}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">No payments yet. Post a payment or scan a rent check.</p>
	{:else}
		<!-- Payment list: each row selects (folds in its full detail) rather than expanding. -->
		<ul class="space-y-2" data-testid="rent-payments">
			{#each list as p (p.id)}
				<li class="rounded-xl border bg-card" data-testid="rent-payment-{p.id}">
					<button type="button" class="flex w-full items-center justify-between gap-2 p-3 text-left" onclick={() => openPayment(p.id)} data-testid="rent-payment-open-{p.id}">
						<span class="flex min-w-0 items-center gap-2 text-sm">
							<span class="font-medium">{formatDateOnly(p.dueDate)}</span>
							<span class="shrink-0 text-muted-foreground">· {p.paymentType}</span>
						</span>
						<span class="flex shrink-0 items-center gap-2"><StatusBadge status={p.status} /><span class="font-semibold">{money(p.amount)}</span></span>
					</button>
				</li>
			{/each}
		</ul>
		{#if hasMorePayments}
			<div class="flex justify-center">
				<Button variant="outline" size="sm" onclick={loadMorePayments} disabled={paymentsQuery.isFetching} data-testid="rent-load-more">
					{paymentsQuery.isFetching ? 'Loading…' : 'Load more'}
				</Button>
			</div>
		{/if}
	{/if}
{/if}
</div>
