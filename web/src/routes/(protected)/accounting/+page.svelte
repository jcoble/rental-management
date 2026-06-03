<script lang="ts">
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { payments } from '$lib/api/endpoints/payments';
	import { expenses } from '$lib/api/endpoints/expenses';
	import { accounting } from '$lib/api/endpoints/accounting';
	import { leases } from '$lib/api/endpoints/leases';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { properties } from '$lib/api/endpoints/properties';
	import type { Payment, Expense, AccountingSummary, AccountingReports } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { paymentSchema, expenseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { Pencil, Trash2, Plus } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;
	const PAYMENT_STATUSES = ['Scheduled', 'Paid', 'Partial', 'Late', 'Waived', 'Failed', 'Refunded'];
	const PAYMENT_TYPES = ['Rent', 'SecurityDeposit', 'LateFee', 'Utility', 'Other'];
	const EXPENSE_STATUSES = ['Pending', 'Approved', 'Paid', 'Rejected', 'Draft'];
	const SCHEDULE_E_CATEGORIES = [
		'Advertising',
		'AutoTravel',
		'CleaningMaintenance',
		'Commissions',
		'Insurance',
		'LegalProfessional',
		'ManagementFees',
		'MortgageInterest',
		'Repairs',
		'Supplies',
		'Taxes',
		'Utilities',
		'Depreciation',
		'Other'
	];

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
	const accountingReportsQuery = createQuery(() => ({ queryKey: ['accounting-reports', portfolioId], queryFn: () => accounting.reports() }));
	const leasesQuery = createQuery(() => ({ queryKey: ['leases', portfolioId], queryFn: () => leases.list(portfolioId, { take: 200 }) }));
	const vendorsQuery = createQuery(() => ({ queryKey: ['vendors', portfolioId], queryFn: () => vendors.list(portfolioId, { take: 200 }) }));
	const propertiesQuery = createQuery(() => ({ queryKey: ['properties', portfolioId], queryFn: () => properties.list(portfolioId, { take: 200 }) }));

	// --- Payment form/dialog ---
	const emptyPayment = {
		leaseId: '',
		amount: '',
		dueDate: '',
		paymentType: 'Rent',
		status: 'Scheduled',
		paidDate: '',
		method: '',
		externalReference: '',
		notes: ''
	};
	let showPaymentForm = $state(false);
	let editingPaymentId = $state<number | null>(null);
	let paymentForm = $state({ ...emptyPayment });
	let paymentErrors = $state<Record<string, string>>({});
	let paymentDeleteTarget = $state<Payment | null>(null);

	function invalidatePayments() {
		queryClient.invalidateQueries({ queryKey: ['payments', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-summary', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-reports', portfolioId] });
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
		paymentForm = {
			leaseId: String(p.leaseId),
			amount: String(p.amount),
			dueDate: p.dueDate?.slice(0, 10) ?? '',
			paymentType: p.paymentType,
			status: p.status,
			paidDate: p.paidDate?.slice(0, 10) ?? '',
			method: p.method ?? '',
			externalReference: p.externalReference ?? '',
			notes: p.notes ?? ''
		};
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
	const emptyExpense = {
		category: 'Repairs',
		description: '',
		amount: '',
		incurredAt: '',
		propertyId: '',
		vendorId: '',
		status: 'Pending',
		dueDate: '',
		paidAt: '',
		billableToOwner: false,
		notes: '',
		subtotal: '',
		taxAmount: '',
		receiptData: ''
	};
	let showExpenseForm = $state(false);
	let editingExpenseId = $state<number | null>(null);
	let expenseForm = $state({ ...emptyExpense });
	let expenseErrors = $state<Record<string, string>>({});
	let expenseDeleteTarget = $state<Expense | null>(null);

	function invalidateExpenses() {
		queryClient.invalidateQueries({ queryKey: ['expenses', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-summary', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-reports', portfolioId] });
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
			dueDate: e.dueDate?.slice(0, 10) ?? '',
			paidAt: e.paidAt?.slice(0, 10) ?? '',
			billableToOwner: e.billableToOwner,
			notes: e.notes ?? '',
			subtotal: e.subtotal != null ? String(e.subtotal) : '',
			taxAmount: e.taxAmount != null ? String(e.taxAmount) : '',
			receiptData: e.receiptData ?? ''
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
	function shortDate(value: string) {
		return new Date(value).toLocaleDateString();
	}
	const paymentsList = $derived(paymentsQuery.data ?? []);
	const expensesList = $derived(expensesQuery.data ?? []);
	const summary = $derived(accountingSummaryQuery.data as AccountingSummary | undefined);
	const reports = $derived(accountingReportsQuery.data as AccountingReports | undefined);
	const recentLedger = $derived((reports?.ledger ?? []).slice(0, 12));
	const topProperties = $derived((reports?.properties ?? []).slice(0, 6));
	const topScheduleE = $derived((reports?.scheduleE ?? []).slice(0, 6));
	const vendorAlerts = $derived((reports?.vendors1099 ?? []).filter((v) => v.needsW9 || v.needs1099Review).slice(0, 6));
	const inputClass = 'h-10 rounded border border-border bg-background px-3 py-2 text-sm';
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

	<div class="mb-5 grid gap-4 xl:grid-cols-[1.3fr_1fr]" data-testid="accounting-reports">
		<div class="rounded-lg border border-border bg-card">
			<div class="flex items-center justify-between border-b border-border px-4 py-3">
				<div>
					<p class="font-semibold">Transaction ledger</p>
					<p class="text-xs text-muted-foreground">Income, expenses, and source links</p>
				</div>
				<p class="text-right text-xs text-muted-foreground">Net {money(reports?.netCashFlow || 0)}</p>
			</div>
			<div class="divide-y divide-border" data-testid="accounting-ledger">
				{#if accountingReportsQuery.isLoading}
					<p class="px-4 py-6 text-center text-sm text-muted-foreground">Loading reports…</p>
				{:else if recentLedger.length === 0}
					<p class="px-4 py-6 text-center text-sm text-muted-foreground">No ledger activity yet.</p>
				{:else}
					{#each recentLedger as row (`${row.type}-${row.id}`)}
						<a href={row.sourceHref} class="grid gap-2 px-4 py-3 text-sm hover:bg-secondary sm:grid-cols-[6rem_1fr_auto]" data-testid="ledger-row">
							<p class="text-xs text-muted-foreground">{shortDate(row.date)}</p>
							<div class="min-w-0">
								<p class="truncate font-medium">{row.description}</p>
								<p class="truncate text-xs text-muted-foreground">{row.counterparty || 'No counterparty'} · {row.propertyName || 'General'} · {row.status}</p>
							</div>
							<p class:font-semibold={row.amount >= 0} class:text-destructive={row.amount < 0}>{money(row.amount)}</p>
						</a>
					{/each}
				{/if}
			</div>
		</div>

		<div class="grid gap-4">
			<div class="rounded-lg border border-border bg-card">
				<div class="border-b border-border px-4 py-3">
					<p class="font-semibold">Property P&amp;L</p>
					<p class="text-xs text-muted-foreground">Income, expenses, and overdue balances</p>
				</div>
				<div class="space-y-3 p-4" data-testid="property-financials">
					{#if topProperties.length === 0}
						<p class="text-sm text-muted-foreground">No property financials yet.</p>
					{:else}
						{#each topProperties as property (property.propertyId)}
							<a href={`/properties/${property.propertyId}`} class="block rounded border border-border bg-background p-3 text-sm hover:bg-secondary">
								<div class="flex items-center justify-between gap-3">
									<p class="truncate font-medium">{property.propertyName}</p>
									<p class="shrink-0 font-semibold">{money(property.net)}</p>
								</div>
								<p class="mt-1 text-xs text-muted-foreground">Income {money(property.income)} · Expenses {money(property.expenses)} · Overdue {money(property.overdue)}</p>
							</a>
						{/each}
					{/if}
				</div>
			</div>

			<div class="rounded-lg border border-border bg-card">
				<div class="border-b border-border px-4 py-3">
					<p class="font-semibold">Schedule E</p>
					<p class="text-xs text-muted-foreground">Tax categories from expenses</p>
				</div>
				<div class="space-y-2 p-4" data-testid="schedule-e-report">
					{#if topScheduleE.length === 0}
						<p class="text-sm text-muted-foreground">No categorized expenses yet.</p>
					{:else}
						{#each topScheduleE as category (category.categoryName)}
							<div class="flex items-center justify-between gap-3 text-sm">
								<p class="truncate">{category.categoryName} <span class="text-xs text-muted-foreground">({category.count})</span></p>
								<p class="shrink-0 font-medium">{money(category.total)}</p>
							</div>
						{/each}
					{/if}
				</div>
			</div>

			<div class="rounded-lg border border-border bg-card">
				<div class="border-b border-border px-4 py-3">
					<p class="font-semibold">1099 checklist</p>
					<p class="text-xs text-muted-foreground">Eligible vendors needing W-9 or review</p>
				</div>
				<div class="space-y-2 p-4" data-testid="vendor-1099-report">
					{#if vendorAlerts.length === 0}
						<p class="text-sm text-muted-foreground">No vendor tax follow-up needed.</p>
					{:else}
						{#each vendorAlerts as vendor (vendor.vendorId)}
							<div class="rounded border border-border bg-background p-3 text-sm">
								<div class="flex items-center justify-between gap-3">
									<p class="truncate font-medium">{vendor.vendorName}</p>
									<p class="shrink-0">{money(vendor.totalPaid)}</p>
								</div>
								<p class="mt-1 text-xs text-muted-foreground">{vendor.needsW9 ? 'W-9 needed' : 'W-9 on file'} · {vendor.needs1099Review ? '1099 threshold met' : 'Below 1099 threshold'}</p>
							</div>
						{/each}
					{/if}
				</div>
			</div>
		</div>
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
							<p class="text-xs text-muted-foreground">{payment.paymentType} · {money(payment.amount)} · Due {new Date(payment.dueDate).toLocaleDateString()}</p>
							<div class="mt-2 flex gap-2">
								{#if payment.status !== 'Paid'}
									<button data-testid="payment-mark-paid" class="rounded border border-border px-2 py-1 text-xs" onclick={() => markPaidMutation.mutate(payment.id)}>Mark Paid</button>
								{/if}
								<a href={`/accounting/payments/${payment.id}`} data-testid="payment-details" class="rounded border border-border px-2 py-1 text-xs text-primary hover:bg-secondary">Details</a>
								<button data-testid="payment-edit" aria-label="Edit payment" class="rounded border border-border px-2 py-1 text-xs" onclick={() => goto(`/accounting/payments/${payment.id}`)}><Pencil class="h-3.5 w-3.5" /></button>
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
							<div class="mt-2 flex items-center gap-2">
								<a href={`/accounting/expenses/${expense.id}`} data-testid="expense-details" class="rounded border border-border px-2 py-1 text-xs text-primary hover:bg-secondary">Details</a>
								<button data-testid="expense-edit" aria-label="Edit expense" class="rounded border border-border px-2 py-1 text-xs" onclick={() => goto(`/accounting/expenses/${expense.id}`)}><Pencil class="h-3.5 w-3.5" /></button>
								<button data-testid="expense-delete" aria-label="Delete expense" class="rounded border border-border px-2 py-1 text-xs hover:text-destructive" onclick={() => (expenseDeleteTarget = expense)}><Trash2 class="h-3.5 w-3.5" /></button>
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
			</div>
		</div>
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
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="payment-lease-input">Lease</label>
				<select id="payment-lease-input" data-testid="payment-lease-input" bind:value={paymentForm.leaseId} class="{inputClass} w-full">
					<option value="">Select lease</option>
					{#each leasesQuery.data || [] as lease}<option value={lease.id}>{lease.leaseNumber} · {lease.tenantName}</option>{/each}
				</select>
				{#if paymentErrors.leaseId}<p class="mt-1 text-xs text-destructive" data-testid="payment-lease-error">{paymentErrors.leaseId}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="payment-amount-input">Amount</label>
					<input id="payment-amount-input" data-testid="payment-amount-input" bind:value={paymentForm.amount} class="{inputClass} w-full" placeholder="Amount" />
					{#if paymentErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="payment-amount-error">{paymentErrors.amount}</p>{/if}
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="payment-due-date-input">Due date</label>
					<input id="payment-due-date-input" data-testid="payment-due-date-input" type="date" bind:value={paymentForm.dueDate} class="{inputClass} w-full" />
					{#if paymentErrors.dueDate}<p class="mt-1 text-xs text-destructive" data-testid="payment-due-date-error">{paymentErrors.dueDate}</p>{/if}
				</div>
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="payment-type-input">Payment type</label>
					<select id="payment-type-input" data-testid="payment-type-input" bind:value={paymentForm.paymentType} class="{inputClass} w-full">{#each PAYMENT_TYPES as t}<option value={t}>{t}</option>{/each}</select>
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="payment-status-input">Status</label>
					<select id="payment-status-input" data-testid="payment-status-input" bind:value={paymentForm.status} class="{inputClass} w-full">{#each PAYMENT_STATUSES as s}<option value={s}>{s}</option>{/each}</select>
				</div>
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="payment-paid-date-input">Paid date</label>
					<input id="payment-paid-date-input" data-testid="payment-paid-date-input" type="date" bind:value={paymentForm.paidDate} class="{inputClass} w-full" />
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="payment-method-input">Method</label>
					<input id="payment-method-input" data-testid="payment-method-input" bind:value={paymentForm.method} class="{inputClass} w-full" placeholder="Method (check, cash, ACH)" />
				</div>
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="payment-reference-input">Reference</label>
				<input id="payment-reference-input" data-testid="payment-reference-input" bind:value={paymentForm.externalReference} class="{inputClass} w-full" placeholder="Check #, deposit ID, or bank reference" />
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="payment-notes-input">Notes</label>
				<textarea id="payment-notes-input" data-testid="payment-notes-input" bind:value={paymentForm.notes} class="{inputClass} min-h-20 w-full" placeholder="Notes"></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<button data-testid="payment-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground hover:bg-secondary" onclick={closePaymentForm}>Cancel</button>
			<button data-testid="payment-form-save" onclick={submitPayment} class="rounded bg-primary px-3 py-2 text-sm text-white" disabled={savePaymentMutation.isPending}>{savePaymentMutation.isPending ? 'Saving…' : 'Save Payment'}</button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root
	open={showExpenseForm}
	onOpenChange={(v) => { if (!v) closeExpenseForm(); }}
>
	<Dialog.Content class="max-w-2xl">
		<Dialog.Header>
			<Dialog.Title>{editingExpenseId == null ? 'New Expense' : 'Edit Expense'}</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-2" data-testid="expense-form">
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-description-input">Description</label>
				<input id="expense-description-input" data-testid="expense-description-input" bind:value={expenseForm.description} class="{inputClass} w-full" placeholder="Description" />
				{#if expenseErrors.description}<p class="mt-1 text-xs text-destructive" data-testid="expense-description-error">{expenseErrors.description}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-amount-input">Amount</label>
					<input id="expense-amount-input" data-testid="expense-amount-input" bind:value={expenseForm.amount} class="{inputClass} w-full" placeholder="Amount" />
					{#if expenseErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="expense-amount-error">{expenseErrors.amount}</p>{/if}
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-incurred-input">Incurred date</label>
					<input id="expense-incurred-input" data-testid="expense-incurred-input" type="date" bind:value={expenseForm.incurredAt} class="{inputClass} w-full" />
					{#if expenseErrors.incurredAt}<p class="mt-1 text-xs text-destructive" data-testid="expense-incurred-error">{expenseErrors.incurredAt}</p>{/if}
				</div>
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-category-input">Category</label>
					<select id="expense-category-input" data-testid="expense-category-input" bind:value={expenseForm.category} class="{inputClass} w-full">
						{#each SCHEDULE_E_CATEGORIES as category}<option value={category}>{category}</option>{/each}
					</select>
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-status-input">Status</label>
					<select id="expense-status-input" data-testid="expense-status-input" bind:value={expenseForm.status} class="{inputClass} w-full">
						{#each EXPENSE_STATUSES as status}<option value={status}>{status}</option>{/each}
					</select>
				</div>
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-property-input">Property</label>
					<select id="expense-property-input" data-testid="expense-property-input" bind:value={expenseForm.propertyId} class="{inputClass} w-full"><option value="">No property</option>{#each propertiesQuery.data || [] as property}<option value={property.id}>{property.name}</option>{/each}</select>
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-vendor-input">Vendor</label>
					<select id="expense-vendor-input" data-testid="expense-vendor-input" bind:value={expenseForm.vendorId} class="{inputClass} w-full"><option value="">No vendor</option>{#each vendorsQuery.data || [] as vendor}<option value={vendor.id}>{vendor.name}</option>{/each}</select>
				</div>
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-due-date-input">Due date</label>
					<input id="expense-due-date-input" data-testid="expense-due-date-input" type="date" bind:value={expenseForm.dueDate} class="{inputClass} w-full" />
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-paid-date-input">Paid date</label>
					<input id="expense-paid-date-input" data-testid="expense-paid-date-input" type="date" bind:value={expenseForm.paidAt} class="{inputClass} w-full" />
				</div>
			</div>
			<label class="flex items-center gap-2 rounded border border-border px-3 py-2 text-sm">
				<input data-testid="expense-billable-input" type="checkbox" bind:checked={expenseForm.billableToOwner} />
				<span>Billable to owner</span>
			</label>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-notes-input">Notes</label>
				<textarea id="expense-notes-input" data-testid="expense-notes-input" bind:value={expenseForm.notes} class="{inputClass} min-h-20 w-full" placeholder="Notes"></textarea>
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-subtotal-input">Receipt subtotal</label>
					<input id="expense-subtotal-input" data-testid="expense-subtotal-input" bind:value={expenseForm.subtotal} class="{inputClass} w-full" placeholder="Receipt subtotal" />
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-tax-input">Receipt tax</label>
					<input id="expense-tax-input" data-testid="expense-tax-input" bind:value={expenseForm.taxAmount} class="{inputClass} w-full" placeholder="Receipt tax" />
				</div>
			</div>
			<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-receipt-data-input">Receipt details JSON</label>
			<textarea
				id="expense-receipt-data-input"
				data-testid="expense-receipt-data-input"
				bind:value={expenseForm.receiptData}
				class="{inputClass} min-h-28 w-full font-mono text-xs"
				placeholder="Receipt details JSON"
			></textarea>
		</div>
		<Dialog.Footer>
			<button data-testid="expense-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground hover:bg-secondary" onclick={closeExpenseForm}>Cancel</button>
			<button data-testid="expense-form-save" onclick={submitExpense} class="rounded bg-primary px-3 py-2 text-sm text-white" disabled={saveExpenseMutation.isPending}>{saveExpenseMutation.isPending ? 'Saving…' : 'Save Expense'}</button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={paymentDeleteTarget !== null}
	title="Delete payment"
	message={paymentDeleteTarget ? `Delete this ${money(paymentDeleteTarget.amount)} ${paymentDeleteTarget.paymentType} charge?` : ''}
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
