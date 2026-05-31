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
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { Pencil, Trash2, Plus } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';

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

	// Derived labels for Select triggers
	const selectedLeaseLabel = $derived(
		(leasesQuery.data || []).find((l) => String(l.id) === paymentForm.leaseId)
			? `${(leasesQuery.data || []).find((l) => String(l.id) === paymentForm.leaseId)!.leaseNumber} · ${(leasesQuery.data || []).find((l) => String(l.id) === paymentForm.leaseId)!.tenantName}`
			: null
	);
	const selectedPropertyLabel = $derived(
		(propertiesQuery.data || []).find((p) => String(p.id) === expenseForm.propertyId)?.name ?? null
	);
	const selectedVendorLabel = $derived(
		(vendorsQuery.data || []).find((v) => String(v.id) === expenseForm.vendorId)?.name ?? null
	);
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
		<Card.Root class="gap-0 py-0" data-testid="accounting-collected">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Collected</p>
				<p class="text-2xl font-bold">{money(summary?.payments.collected || 0)}</p>
			</Card.Content>
		</Card.Root>
		<Card.Root class="gap-0 py-0" data-testid="accounting-outstanding">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Outstanding</p>
				<p class="text-2xl font-bold">{money(summary?.payments.outstanding || 0)}</p>
			</Card.Content>
		</Card.Root>
		<Card.Root class="gap-0 py-0" data-testid="accounting-overdue">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Overdue</p>
				<p class="text-2xl font-bold">{money(summary?.payments.overdue || 0)}</p>
			</Card.Content>
		</Card.Root>
		<Card.Root class="gap-0 py-0" data-testid="accounting-expenses">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Expenses</p>
				<p class="text-2xl font-bold">{money(summary?.totalExpenses || 0)}</p>
			</Card.Content>
		</Card.Root>
	</div>

	<div class="grid gap-4 lg:grid-cols-2">
		<Card.Root class="gap-0 py-0">
			<Card.Header class="flex-row items-center justify-between border-b border-border px-4 py-3 space-y-0">
				<Card.Title class="text-base font-semibold">Payments</Card.Title>
				<Card.Action>
					<Button data-testid="payment-create-button" size="sm" onclick={openCreatePayment}><Plus class="h-3.5 w-3.5" /> Add</Button>
				</Card.Action>
			</Card.Header>
			<div class="border-b border-border px-3 py-2"><SearchInput bind:value={paymentSearch} placeholder="Search payments…" testid="payment-search" /></div>
			<Card.Content class="max-h-[40vh] space-y-2 overflow-y-auto p-3" data-testid="payments-list">
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
									<Button data-testid="payment-mark-paid" variant="outline" size="sm" onclick={() => markPaidMutation.mutate(payment.id)}>Mark Paid</Button>
								{/if}
								<Button data-testid="payment-edit" aria-label="Edit payment" variant="outline" size="icon" onclick={() => openEditPayment(payment)}><Pencil class="h-3.5 w-3.5" /></Button>
								<Button data-testid="payment-delete" aria-label="Delete payment" variant="outline" size="icon" onclick={() => (paymentDeleteTarget = payment)}><Trash2 class="h-3.5 w-3.5" /></Button>
							</div>
						</div>
					{/each}
				{/if}
			</Card.Content>
			<Card.Footer class="border-t border-border px-3 py-2">
				<Pagination bind:skip={paymentSkip} take={PAGE_SIZE} count={paymentsList.length} testid="payment-pagination" />
			</Card.Footer>
		</Card.Root>

		<Card.Root class="gap-0 py-0">
			<Card.Header class="flex-row items-center justify-between border-b border-border px-4 py-3 space-y-0">
				<Card.Title class="text-base font-semibold">Expenses</Card.Title>
				<Card.Action>
					<Button data-testid="expense-create-button" size="sm" onclick={openCreateExpense}><Plus class="h-3.5 w-3.5" /> Add</Button>
				</Card.Action>
			</Card.Header>
			<Card.Content class="max-h-[46vh] space-y-2 overflow-y-auto p-3" data-testid="expenses-list">
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
							<div class="mt-2 flex items-center gap-2">
								<Button data-testid="expense-edit" aria-label="Edit expense" variant="outline" size="icon" onclick={() => openEditExpense(expense)}><Pencil class="h-3.5 w-3.5" /></Button>
								<Button data-testid="expense-delete" aria-label="Delete expense" variant="outline" size="icon" onclick={() => (expenseDeleteTarget = expense)}><Trash2 class="h-3.5 w-3.5" /></Button>
								{#if expense.hasReceipt}
									{#if expense.receiptIsImage}
										<a
											href="/expense-file/{expense.id}"
											target="_blank"
											rel="noopener noreferrer"
											data-testid="expense-receipt-{expense.id}"
											class="ml-auto shrink-0"
											aria-label="View receipt"
										>
											<img
												src="/expense-file/{expense.id}?thumb=true"
												alt="Receipt thumbnail"
												class="h-10 w-10 rounded object-cover ring-1 ring-border"
												loading="lazy"
											/>
										</a>
									{:else}
										<a
											href="/expense-file/{expense.id}"
											target="_blank"
											rel="noopener noreferrer"
											data-testid="expense-receipt-{expense.id}"
											class="ml-auto text-xs text-primary underline underline-offset-2 hover:text-primary/80"
										>Receipt (PDF)</a>
									{/if}
								{/if}
							</div>
						</div>
					{/each}
				{/if}
			</Card.Content>
		</Card.Root>
	</div>
</div>

<Dialog.Root
	open={showPaymentForm}
	onOpenChange={(v) => { if (!v) closePaymentForm(); }}
>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>{editingPaymentId == null ? 'New Payment' : 'Edit Payment'}</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-2" data-testid="payment-form">
			<div>
				<Select.Root type="single" bind:value={paymentForm.leaseId}>
					<Select.Trigger class="w-full" data-testid="payment-lease-input">
						{selectedLeaseLabel ?? 'Select lease'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="Select lease">Select lease</Select.Item>
						{#each leasesQuery.data || [] as lease}
							<Select.Item value={String(lease.id)} label="{lease.leaseNumber} · {lease.tenantName}">{lease.leaseNumber} · {lease.tenantName}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				{#if paymentErrors.leaseId}<p class="mt-1 text-xs text-destructive" data-testid="payment-lease-error">{paymentErrors.leaseId}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<Input data-testid="payment-amount-input" bind:value={paymentForm.amount} placeholder="Amount" />
					{#if paymentErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="payment-amount-error">{paymentErrors.amount}</p>{/if}
				</div>
				<div>
					<Input data-testid="payment-due-date-input" type="date" bind:value={paymentForm.dueDate} />
					{#if paymentErrors.dueDate}<p class="mt-1 text-xs text-destructive" data-testid="payment-due-date-error">{paymentErrors.dueDate}</p>{/if}
				</div>
			</div>
			<div class="grid grid-cols-2 gap-2">
				<Select.Root type="single" bind:value={paymentForm.type}>
					<Select.Trigger class="w-full" data-testid="payment-type-input">
						{paymentForm.type || 'Select type'}
					</Select.Trigger>
					<Select.Content>
						{#each PAYMENT_TYPES as t}
							<Select.Item value={t} label={t}>{t}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				<Select.Root type="single" bind:value={paymentForm.status}>
					<Select.Trigger class="w-full" data-testid="payment-status-input">
						{paymentForm.status || 'Select status'}
					</Select.Trigger>
					<Select.Content>
						{#each PAYMENT_STATUSES as s}
							<Select.Item value={s} label={s}>{s}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
		</div>
		<Dialog.Footer>
			<Button data-testid="payment-form-cancel" variant="outline" onclick={closePaymentForm}>Cancel</Button>
			<Button data-testid="payment-form-save" onclick={submitPayment} disabled={savePaymentMutation.isPending}>{savePaymentMutation.isPending ? 'Saving…' : 'Save Payment'}</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root
	open={showExpenseForm}
	onOpenChange={(v) => { if (!v) closeExpenseForm(); }}
>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>{editingExpenseId == null ? 'New Expense' : 'Edit Expense'}</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-2" data-testid="expense-form">
			<div>
				<Input data-testid="expense-description-input" bind:value={expenseForm.description} placeholder="Description" />
				{#if expenseErrors.description}<p class="mt-1 text-xs text-destructive" data-testid="expense-description-error">{expenseErrors.description}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<Input data-testid="expense-amount-input" bind:value={expenseForm.amount} placeholder="Amount" />
					{#if expenseErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="expense-amount-error">{expenseErrors.amount}</p>{/if}
				</div>
				<div>
					<Input data-testid="expense-incurred-input" type="date" bind:value={expenseForm.incurredAt} />
					{#if expenseErrors.incurredAt}<p class="mt-1 text-xs text-destructive" data-testid="expense-incurred-error">{expenseErrors.incurredAt}</p>{/if}
				</div>
			</div>
			<div class="grid grid-cols-2 gap-2">
				<Select.Root type="single" bind:value={expenseForm.category}>
					<Select.Trigger class="w-full" data-testid="expense-category-input">
						{expenseForm.category || 'Select category'}
					</Select.Trigger>
					<Select.Content>
						{#each ['Repairs', 'Utilities', 'Landscaping', 'Cleaning', 'Management', 'Other'] as cat}
							<Select.Item value={cat} label={cat}>{cat}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				<Select.Root type="single" bind:value={expenseForm.status}>
					<Select.Trigger class="w-full" data-testid="expense-status-input">
						{expenseForm.status || 'Select status'}
					</Select.Trigger>
					<Select.Content>
						{#each ['Pending', 'Approved', 'Paid'] as s}
							<Select.Item value={s} label={s}>{s}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<div class="grid grid-cols-2 gap-2">
				<Select.Root type="single" bind:value={expenseForm.propertyId}>
					<Select.Trigger class="w-full" data-testid="expense-property-input">
						{selectedPropertyLabel ?? 'No property'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="No property">No property</Select.Item>
						{#each propertiesQuery.data || [] as property}
							<Select.Item value={String(property.id)} label={property.name}>{property.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				<Select.Root type="single" bind:value={expenseForm.vendorId}>
					<Select.Trigger class="w-full" data-testid="expense-vendor-input">
						{selectedVendorLabel ?? 'No vendor'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="No vendor">No vendor</Select.Item>
						{#each vendorsQuery.data || [] as vendor}
							<Select.Item value={String(vendor.id)} label={vendor.name}>{vendor.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
		</div>
		<Dialog.Footer>
			<Button data-testid="expense-form-cancel" variant="outline" onclick={closeExpenseForm}>Cancel</Button>
			<Button data-testid="expense-form-save" onclick={submitExpense} disabled={saveExpenseMutation.isPending}>{saveExpenseMutation.isPending ? 'Saving…' : 'Save Expense'}</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

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
	message={expenseDeleteTarget ? `Delete "${expenseDeleteTarget.description}"?` : ''}
	busy={deleteExpenseMutation.isPending}
	testid="expense-delete"
	onconfirm={() => expenseDeleteTarget && deleteExpenseMutation.mutate(expenseDeleteTarget.id)}
	oncancel={() => (expenseDeleteTarget = null)}
/>
