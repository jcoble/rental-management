<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { payments } from '$lib/api/endpoints/payments';
	import { expenses } from '$lib/api/endpoints/expenses';
	import { accounting } from '$lib/api/endpoints/accounting';
	import { leases } from '$lib/api/endpoints/leases';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { properties } from '$lib/api/endpoints/properties';
	import type { Payment, Expense, AccountingSummary } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { paymentSchema, expenseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import Dialog from '$lib/components/ui/Dialog.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { Pencil, Trash2, Plus } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;
	const PAYMENT_STATUSES = ['Scheduled', 'Paid', 'Partial', 'Late', 'Waived'];
	const PAYMENT_TYPES = ['Rent', 'SecurityDeposit', 'LateFee', 'Utility', 'Other'];

	let paymentSearch = $state('');
	let paymentSkip = $state(0);
	const debouncedPaymentSearch = debounced(() => paymentSearch, 300);
	$effect(() => {
		debouncedPaymentSearch.value;
		paymentSkip = 0;
	});

	const paymentsQuery = createQuery(() => ({
		queryKey: ['payments', portfolioId, debouncedPaymentSearch.value, paymentSkip],
		queryFn: () => payments.list(portfolioId, { search: debouncedPaymentSearch.value, skip: paymentSkip, take: PAGE_SIZE }),
	}));
	const expensesQuery = createQuery(() => ({
		queryKey: ['expenses', portfolioId],
		queryFn: () => expenses.list(portfolioId, { take: 100 }),
	}));
	// Single accounting rollup: payment collection (collected/outstanding/overdue) + expense totals.
	const accountingSummaryQuery = createQuery(() => ({ queryKey: ['accounting-summary', portfolioId], queryFn: () => accounting.summary() }));
	const leasesQuery = createQuery(() => ({ queryKey: ['leases', portfolioId], queryFn: () => leases.list(portfolioId, { take: 200 }) }));
	const vendorsQuery = createQuery(() => ({ queryKey: ['vendors', portfolioId], queryFn: () => vendors.list(portfolioId, { take: 200 }) }));
	const propertiesQuery = createQuery(() => ({ queryKey: ['properties', portfolioId], queryFn: () => properties.list(portfolioId, { take: 200 }) }));

	// --- Payment form/dialog ---
	const emptyPayment = { leaseId: '', amount: '', dueDate: '', type: 'Rent', status: 'Scheduled' };
	let showPaymentForm = $state(false);
	let editingPaymentId = $state<number | null>(null);
	let paymentForm = $state({ ...emptyPayment });
	let paymentErrors = $state<Record<string, string>>({});
	let paymentDeleteTarget = $state<Payment | null>(null);

	function invalidatePayments() {
		queryClient.invalidateQueries({ queryKey: ['payments', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-summary', portfolioId] });
	}

	const savePaymentMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null ? payments.create(data) : payments.update(id, data),
		onSuccess: (_r, vars) => {
			showSuccess(vars.id == null ? 'Payment created.' : 'Payment updated.');
			closePaymentForm();
			invalidatePayments();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const markPaidMutation = createMutation(() => ({
		mutationFn: (id: number) => payments.markPaid(id, {}),
		onSuccess: () => {
			showSuccess('Payment marked paid.');
			invalidatePayments();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deletePaymentMutation = createMutation(() => ({
		mutationFn: (id: number) => payments.delete(id),
		onSuccess: () => {
			showSuccess('Payment deleted.');
			paymentDeleteTarget = null;
			invalidatePayments();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreatePayment() {
		editingPaymentId = null;
		paymentForm = { ...emptyPayment };
		paymentErrors = {};
		showPaymentForm = true;
	}
	function openEditPayment(p: Payment) {
		editingPaymentId = p.id;
		paymentForm = { leaseId: String(p.leaseId), amount: String(p.amount), dueDate: p.dueDate?.slice(0, 10) ?? '', type: p.type, status: p.status };
		paymentErrors = {};
		showPaymentForm = true;
	}
	function closePaymentForm() {
		showPaymentForm = false;
		editingPaymentId = null;
		paymentErrors = {};
	}
	function submitPayment() {
		const result = parseForm(paymentSchema, paymentForm);
		if (result.errors) {
			paymentErrors = result.errors;
			return;
		}
		paymentErrors = {};
		savePaymentMutation.mutate({ id: editingPaymentId, data: { portfolioId, ...result.data } });
	}

	// --- Expense form/dialog ---
	const emptyExpense = { category: 'Repairs', description: '', amount: '', incurredAt: '', propertyId: '', vendorId: '', status: 'Pending' };
	let showExpenseForm = $state(false);
	let editingExpenseId = $state<number | null>(null);
	let expenseForm = $state({ ...emptyExpense });
	let expenseErrors = $state<Record<string, string>>({});
	let expenseDeleteTarget = $state<Expense | null>(null);

	function invalidateExpenses() {
		queryClient.invalidateQueries({ queryKey: ['expenses', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-summary', portfolioId] });
	}

	const saveExpenseMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null ? expenses.create(data) : expenses.update(id, data),
		onSuccess: (_r, vars) => {
			showSuccess(vars.id == null ? 'Expense created.' : 'Expense updated.');
			closeExpenseForm();
			invalidateExpenses();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteExpenseMutation = createMutation(() => ({
		mutationFn: (id: number) => expenses.delete(id),
		onSuccess: () => {
			showSuccess('Expense deleted.');
			expenseDeleteTarget = null;
			invalidateExpenses();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreateExpense() {
		editingExpenseId = null;
		expenseForm = { ...emptyExpense };
		expenseErrors = {};
		showExpenseForm = true;
	}
	function openEditExpense(e: Expense) {
		editingExpenseId = e.id;
		expenseForm = {
			category: e.category, description: e.description, amount: String(e.amount),
			incurredAt: e.incurredAt?.slice(0, 10) ?? '', propertyId: e.propertyId != null ? String(e.propertyId) : '',
			vendorId: e.vendorId != null ? String(e.vendorId) : '', status: e.status,
		};
		expenseErrors = {};
		showExpenseForm = true;
	}
	function closeExpenseForm() {
		showExpenseForm = false;
		editingExpenseId = null;
		expenseErrors = {};
	}
	function submitExpense() {
		const result = parseForm(expenseSchema, expenseForm);
		if (result.errors) {
			expenseErrors = result.errors;
			return;
		}
		expenseErrors = {};
		saveExpenseMutation.mutate({ id: editingExpenseId, data: { portfolioId, ...result.data } });
	}

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(value || 0);
	}
	const paymentsList = $derived(paymentsQuery.data ?? []);
	const expensesList = $derived(expensesQuery.data ?? []);
	const summary = $derived(accountingSummaryQuery.data as AccountingSummary | undefined);
	const inputClass = 'rounded border border-border bg-background px-3 py-2 text-sm';
</script>

<svelte:head>
	<title>Accounting - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="accounting-page">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Accounting</h1>
		<p class="text-sm text-muted-foreground">Rent ledger, receivables, expenses, and owner-facing books.</p>
	</div>

	<div class="mb-5 grid gap-4 md:grid-cols-4">
		<div class="rounded-lg border border-border bg-card p-4" data-testid="accounting-collected"><p class="text-xs text-muted-foreground">Collected</p><p class="text-2xl font-bold">{money(summary?.payments.collected || 0)}</p></div>
		<div class="rounded-lg border border-border bg-card p-4" data-testid="accounting-outstanding"><p class="text-xs text-muted-foreground">Outstanding</p><p class="text-2xl font-bold">{money(summary?.payments.outstanding || 0)}</p></div>
		<div class="rounded-lg border border-border bg-card p-4" data-testid="accounting-overdue"><p class="text-xs text-muted-foreground">Overdue</p><p class="text-2xl font-bold">{money(summary?.payments.overdue || 0)}</p></div>
		<div class="rounded-lg border border-border bg-card p-4" data-testid="accounting-expenses"><p class="text-xs text-muted-foreground">Expenses</p><p class="text-2xl font-bold">{money(summary?.totalExpenses || 0)}</p></div>
	</div>

	<div class="grid gap-4 lg:grid-cols-2">
		<div class="rounded-lg border border-border bg-card">
			<div class="flex items-center justify-between border-b border-border px-4 py-3">
				<span class="font-semibold">Payments</span>
				<button data-testid="payment-create-button" class="inline-flex items-center gap-1 rounded bg-primary px-2.5 py-1.5 text-xs text-white" onclick={openCreatePayment}><Plus class="h-3.5 w-3.5" /> Add</button>
			</div>
			<div class="border-b border-border px-3 py-2"><SearchInput bind:value={paymentSearch} placeholder="Search payments…" testid="payment-search" /></div>
			<div class="max-h-[40vh] space-y-2 overflow-y-auto p-3" data-testid="payments-list">
				{#if paymentsQuery.isLoading}
					<p class="py-4 text-center text-sm text-muted-foreground" data-testid="payments-loading">Loading…</p>
				{:else if paymentsList.length === 0}
					<p class="py-4 text-center text-sm text-muted-foreground" data-testid="payments-empty">No payments found.</p>
				{:else}
					{#each paymentsList as payment (payment.id)}
						<div class="rounded border border-border bg-background p-3 text-sm" data-testid="payment-row" data-payment-id={payment.id}>
							<div class="flex items-center justify-between">
								<p class="font-medium">{payment.tenantName || payment.leaseNumber}</p>
								<span>{payment.status}</span>
							</div>
							<p class="text-xs text-muted-foreground">{payment.type} · {money(payment.amount)} · Due {new Date(payment.dueDate).toLocaleDateString()}</p>
							<div class="mt-2 flex gap-2">
								{#if payment.status !== 'Paid'}
									<button data-testid="payment-mark-paid" class="rounded border border-border px-2 py-1 text-xs" onclick={() => markPaidMutation.mutate(payment.id)}>Mark Paid</button>
								{/if}
								<button data-testid="payment-edit" aria-label="Edit payment" class="rounded border border-border px-2 py-1 text-xs" onclick={() => openEditPayment(payment)}><Pencil class="h-3.5 w-3.5" /></button>
								<button data-testid="payment-delete" aria-label="Delete payment" class="rounded border border-border px-2 py-1 text-xs hover:text-destructive" onclick={() => (paymentDeleteTarget = payment)}><Trash2 class="h-3.5 w-3.5" /></button>
							</div>
						</div>
					{/each}
				{/if}
			</div>
			<div class="border-t border-border px-3 py-2"><Pagination bind:skip={paymentSkip} take={PAGE_SIZE} count={paymentsList.length} testid="payment-pagination" /></div>
		</div>

		<div class="rounded-lg border border-border bg-card">
			<div class="flex items-center justify-between border-b border-border px-4 py-3">
				<span class="font-semibold">Expenses</span>
				<button data-testid="expense-create-button" class="inline-flex items-center gap-1 rounded bg-primary px-2.5 py-1.5 text-xs text-white" onclick={openCreateExpense}><Plus class="h-3.5 w-3.5" /> Add</button>
			</div>
			<div class="max-h-[46vh] space-y-2 overflow-y-auto p-3" data-testid="expenses-list">
				{#if expensesQuery.isLoading}
					<p class="py-4 text-center text-sm text-muted-foreground" data-testid="expenses-loading">Loading…</p>
				{:else if expensesList.length === 0}
					<p class="py-4 text-center text-sm text-muted-foreground" data-testid="expenses-empty">No expenses found.</p>
				{:else}
					{#each expensesList as expense (expense.id)}
						<div class="rounded border border-border bg-background p-3 text-sm" data-testid="expense-row" data-expense-id={expense.id}>
							<div class="flex items-center justify-between">
								<p class="font-medium">{expense.description}</p>
								<span>{expense.status}</span>
							</div>
							<p class="text-xs text-muted-foreground">{expense.category} · {money(expense.amount)} · {expense.propertyName || 'General'}</p>
							<div class="mt-2 flex gap-2">
								<button data-testid="expense-edit" aria-label="Edit expense" class="rounded border border-border px-2 py-1 text-xs" onclick={() => openEditExpense(expense)}><Pencil class="h-3.5 w-3.5" /></button>
								<button data-testid="expense-delete" aria-label="Delete expense" class="rounded border border-border px-2 py-1 text-xs hover:text-destructive" onclick={() => (expenseDeleteTarget = expense)}><Trash2 class="h-3.5 w-3.5" /></button>
							</div>
						</div>
					{/each}
				{/if}
			</div>
		</div>
	</div>
</div>

<Dialog open={showPaymentForm} title={editingPaymentId == null ? 'New Payment' : 'Edit Payment'} class="max-w-lg" onclose={closePaymentForm}>
	<div class="space-y-2" data-testid="payment-form">
		<div>
			<select data-testid="payment-lease-input" bind:value={paymentForm.leaseId} class="{inputClass} w-full">
				<option value="">Select lease</option>
				{#each leasesQuery.data || [] as lease}<option value={lease.id}>{lease.leaseNumber} · {lease.tenantName}</option>{/each}
			</select>
			{#if paymentErrors.leaseId}<p class="mt-1 text-xs text-destructive" data-testid="payment-lease-error">{paymentErrors.leaseId}</p>{/if}
		</div>
		<div class="grid grid-cols-2 gap-2">
			<div>
				<input data-testid="payment-amount-input" bind:value={paymentForm.amount} class="{inputClass} w-full" placeholder="Amount" />
				{#if paymentErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="payment-amount-error">{paymentErrors.amount}</p>{/if}
			</div>
			<div>
				<input data-testid="payment-due-date-input" type="date" bind:value={paymentForm.dueDate} class="{inputClass} w-full" />
				{#if paymentErrors.dueDate}<p class="mt-1 text-xs text-destructive" data-testid="payment-due-date-error">{paymentErrors.dueDate}</p>{/if}
			</div>
		</div>
		<div class="grid grid-cols-2 gap-2">
			<select data-testid="payment-type-input" bind:value={paymentForm.type} class={inputClass}>{#each PAYMENT_TYPES as t}<option value={t}>{t}</option>{/each}</select>
			<select data-testid="payment-status-input" bind:value={paymentForm.status} class={inputClass}>{#each PAYMENT_STATUSES as s}<option value={s}>{s}</option>{/each}</select>
		</div>
	</div>
	<div class="mt-4 flex justify-end gap-2">
		<button data-testid="payment-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground hover:bg-secondary" onclick={closePaymentForm}>Cancel</button>
		<button data-testid="payment-form-save" onclick={submitPayment} class="rounded bg-primary px-3 py-2 text-sm text-white" disabled={savePaymentMutation.isPending}>{savePaymentMutation.isPending ? 'Saving…' : 'Save Payment'}</button>
	</div>
</Dialog>

<Dialog open={showExpenseForm} title={editingExpenseId == null ? 'New Expense' : 'Edit Expense'} class="max-w-lg" onclose={closeExpenseForm}>
	<div class="space-y-2" data-testid="expense-form">
		<div>
			<input data-testid="expense-description-input" bind:value={expenseForm.description} class="{inputClass} w-full" placeholder="Description" />
			{#if expenseErrors.description}<p class="mt-1 text-xs text-destructive" data-testid="expense-description-error">{expenseErrors.description}</p>{/if}
		</div>
		<div class="grid grid-cols-2 gap-2">
			<div>
				<input data-testid="expense-amount-input" bind:value={expenseForm.amount} class="{inputClass} w-full" placeholder="Amount" />
				{#if expenseErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="expense-amount-error">{expenseErrors.amount}</p>{/if}
			</div>
			<div>
				<input data-testid="expense-incurred-input" type="date" bind:value={expenseForm.incurredAt} class="{inputClass} w-full" />
				{#if expenseErrors.incurredAt}<p class="mt-1 text-xs text-destructive" data-testid="expense-incurred-error">{expenseErrors.incurredAt}</p>{/if}
			</div>
		</div>
		<div class="grid grid-cols-2 gap-2">
			<select data-testid="expense-category-input" bind:value={expenseForm.category} class={inputClass}><option>Repairs</option><option>Utilities</option><option>Landscaping</option><option>Cleaning</option><option>Management</option><option>Other</option></select>
			<select data-testid="expense-status-input" bind:value={expenseForm.status} class={inputClass}><option>Pending</option><option>Approved</option><option>Paid</option></select>
		</div>
		<div class="grid grid-cols-2 gap-2">
			<select data-testid="expense-property-input" bind:value={expenseForm.propertyId} class={inputClass}><option value="">No property</option>{#each propertiesQuery.data || [] as property}<option value={property.id}>{property.name}</option>{/each}</select>
			<select data-testid="expense-vendor-input" bind:value={expenseForm.vendorId} class={inputClass}><option value="">No vendor</option>{#each vendorsQuery.data || [] as vendor}<option value={vendor.id}>{vendor.name}</option>{/each}</select>
		</div>
	</div>
	<div class="mt-4 flex justify-end gap-2">
		<button data-testid="expense-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground hover:bg-secondary" onclick={closeExpenseForm}>Cancel</button>
		<button data-testid="expense-form-save" onclick={submitExpense} class="rounded bg-primary px-3 py-2 text-sm text-white" disabled={saveExpenseMutation.isPending}>{saveExpenseMutation.isPending ? 'Saving…' : 'Save Expense'}</button>
	</div>
</Dialog>

<ConfirmDialog
	open={paymentDeleteTarget !== null}
	title="Delete payment"
	message={paymentDeleteTarget ? `Delete this ${money(paymentDeleteTarget.amount)} ${paymentDeleteTarget.type} charge?` : ''}
	busy={deletePaymentMutation.isPending}
	testid="payment-delete"
	onconfirm={() => paymentDeleteTarget && deletePaymentMutation.mutate(paymentDeleteTarget.id)}
	oncancel={() => (paymentDeleteTarget = null)}
/>
<ConfirmDialog
	open={expenseDeleteTarget !== null}
	title="Delete expense"
	message={expenseDeleteTarget ? `Delete “${expenseDeleteTarget.description}”?` : ''}
	busy={deleteExpenseMutation.isPending}
	testid="expense-delete"
	onconfirm={() => expenseDeleteTarget && deleteExpenseMutation.mutate(expenseDeleteTarget.id)}
	oncancel={() => (expenseDeleteTarget = null)}
/>
