<script lang="ts">
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { payments } from '$lib/api/endpoints/payments';
	import { expenses } from '$lib/api/endpoints/expenses';
	import { accounting, downloadScheduleECsv } from '$lib/api/endpoints/accounting';
	import { leases } from '$lib/api/endpoints/leases';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { properties } from '$lib/api/endpoints/properties';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import type { Payment, Expense, AccountingReports, AccountingSummary, AccountingTransaction } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { paymentSchema, expenseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Pencil, Trash2, Plus, Download, FileBarChart, Landmark } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';
	import { Checkbox } from '$lib/components/ui/checkbox';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;
	const PAYMENT_STATUSES = ['Scheduled', 'Paid', 'Partial', 'Late', 'Waived'];
	const PAYMENT_TYPES = ['Rent', 'SecurityDeposit', 'LateFee', 'Utility', 'Other'];
	// Schedule E categories (mirrors RentalCommand.Core.Enums.ScheduleECategory — the values the API accepts).
	const EXPENSE_CATEGORIES = [
		'Advertising', 'AutoTravel', 'CleaningMaintenance', 'Commissions', 'Insurance',
		'LegalProfessional', 'ManagementFees', 'MortgageInterest', 'Repairs', 'Supplies',
		'Taxes', 'Utilities', 'Depreciation', 'Other'
	];
	const EXPENSE_STATUSES = ['Pending', 'Approved', 'Paid'];

	let transactionSearch = $state('');
	let transactionKindFilter = $state('');
	let transactionStatusFilter = $state('');
	let transactionCategoryFilter = $state('');
	let transactionPropertyFilter = $state('');
	let transactionFromFilter = $state('');
	let transactionToFilter = $state('');
	let transactionPage = $state(1);
	let transactionSort = $state('-date');
	let transactionDeleteTarget = $state<AccountingTransaction | null>(null);
	const debouncedTransactionSearch = debounced(() => transactionSearch, 300);
	const selectedPropertyFilter = $derived(transactionPropertyFilter ? Number(transactionPropertyFilter) : undefined);

	$effect(() => {
		debouncedTransactionSearch.value;
		transactionKindFilter;
		transactionStatusFilter;
		transactionCategoryFilter;
		transactionPropertyFilter;
		transactionFromFilter;
		transactionToFilter;
		transactionPage = 1;
	});

	const transactionsQuery = createQuery(() => ({
		queryKey: [
			'accounting-transactions',
			portfolioId,
			debouncedTransactionSearch.value,
			transactionKindFilter,
			transactionStatusFilter,
			transactionCategoryFilter,
			transactionPropertyFilter,
			transactionFromFilter,
			transactionToFilter,
			transactionPage,
			transactionSort,
		],
		queryFn: () => accounting.transactions({
			search: debouncedTransactionSearch.value,
			kind: transactionKindFilter || undefined,
			status: transactionStatusFilter || undefined,
			category: transactionCategoryFilter || undefined,
			propertyId: selectedPropertyFilter,
			from: transactionFromFilter || undefined,
			to: transactionToFilter || undefined,
			skip: (transactionPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: transactionSort,
		}),
	}));
	// Single accounting rollup: payment collection (collected/outstanding/overdue) + expense totals.
	const accountingSummaryQuery = createQuery(() => ({ queryKey: ['accounting-summary', portfolioId], queryFn: () => accounting.summary() }));
	const accountingReportsQuery = createQuery(() => ({ queryKey: ['accounting-reports', portfolioId], queryFn: () => accounting.reports() }));
	const leasesQuery = createQuery(() => ({ queryKey: ['leases', portfolioId], queryFn: () => leases.list(portfolioId, { take: 200 }) }));
	const vendorsQuery = createQuery(() => ({ queryKey: ['vendors', portfolioId], queryFn: () => vendors.list(portfolioId, { take: 200 }) }));
	const propertiesQuery = createQuery(() => ({ queryKey: ['properties', portfolioId], queryFn: () => properties.list(portfolioId, { take: 200 }) }));
	const workOrdersQuery = createQuery(() => ({ queryKey: ['work-orders', portfolioId], queryFn: () => workOrders.list(portfolioId, { take: 200 }) }));

	// --- Payment form/dialog ---
	const emptyPayment = { leaseId: '', amount: '', dueDate: '', paymentType: 'Rent', status: 'Scheduled' };
	let showPaymentForm = $state(false);
	let editingPaymentId = $state<number | null>(null);
	let paymentForm = $state({ ...emptyPayment });
	let paymentErrors = $state<Record<string, string>>({});
	let paymentDeleteTarget = $state<Payment | null>(null);

	function invalidatePayments() {
		queryClient.invalidateQueries({ queryKey: ['payments', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-transactions', portfolioId] });
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
		paymentForm = { leaseId: String(p.leaseId), amount: String(p.amount), dueDate: p.dueDate?.slice(0, 10) ?? '', paymentType: p.paymentType, status: p.status };
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
		category: 'Repairs', description: '', amount: '', subtotal: '', taxAmount: '',
		incurredAt: '', dueDate: '', paidAt: '', propertyId: '', vendorId: '', workOrderId: '',
		status: 'Pending', billableToOwner: false, notes: '',
		vendorAddress: '', vendorPhone: '', vendorWebsite: '', vendorTaxId: '',
		receiptNumber: '', paymentMethod: '', cardLast4: '', taxRate: '', tip: '', discount: '', shipping: ''
	};
	let showExpenseForm = $state(false);
	let editingExpenseId = $state<number | null>(null);
	let expenseForm = $state({ ...emptyExpense });
	let expenseErrors = $state<Record<string, string>>({});
	let expenseDeleteTarget = $state<Expense | null>(null);
	// Holds the parsed ReceiptData of the expense being edited, so line items / extra survive a re-save.
	let editingReceipt = $state<Record<string, any> | null>(null);

	function invalidateExpenses() {
		queryClient.invalidateQueries({ queryKey: ['expenses', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-transactions', portfolioId] });
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

	const deleteTransactionMutation = createMutation(() => ({
		mutationFn: (t: AccountingTransaction) =>
			t.kind === 'Payment' ? payments.delete(t.id) : expenses.delete(t.id),
		onSuccess: () => {
			showSuccess('Transaction deleted.');
			transactionDeleteTarget = null;
			invalidatePayments();
			invalidateExpenses();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreateExpense() {
		editingExpenseId = null;
		editingReceipt = null;
		expenseForm = { ...emptyExpense };
		expenseErrors = {};
		showExpenseForm = true;
	}
	function openEditExpense(e: Expense) {
		editingExpenseId = e.id;
		let rd: Record<string, any> | null = null;
		try { rd = e.receiptData ? JSON.parse(e.receiptData) : null; } catch { rd = null; }
		editingReceipt = rd;
		expenseForm = {
			category: e.category, description: e.description, amount: String(e.amount),
			subtotal: e.subtotal != null ? String(e.subtotal) : '',
			taxAmount: e.taxAmount != null ? String(e.taxAmount) : '',
			incurredAt: e.incurredAt?.slice(0, 10) ?? '',
			dueDate: e.dueDate?.slice(0, 10) ?? '',
			paidAt: e.paidAt?.slice(0, 10) ?? '',
			propertyId: e.propertyId != null ? String(e.propertyId) : '',
			vendorId: e.vendorId != null ? String(e.vendorId) : '',
			workOrderId: e.workOrderId != null ? String(e.workOrderId) : '',
			status: e.status,
			billableToOwner: !!e.billableToOwner,
			notes: e.notes ?? '',
			vendorAddress: rd?.vendor?.address ?? '',
			vendorPhone: rd?.vendor?.phone ?? '',
			vendorWebsite: rd?.vendor?.website ?? '',
			vendorTaxId: rd?.vendor?.taxId ?? '',
			receiptNumber: rd?.receiptNumber ?? '',
			paymentMethod: rd?.paymentMethod ?? '',
			cardLast4: rd?.cardLast4 ?? '',
			taxRate: rd?.taxRate != null ? String(rd.taxRate) : '',
			tip: rd?.tip != null ? String(rd.tip) : '',
			discount: rd?.discount != null ? String(rd.discount) : '',
			shipping: rd?.shipping != null ? String(rd.shipping) : ''
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
		const d = result.data;
		// Nest the receipt-detail fields into the same ReceiptData JSON shape the scan path produces,
		// preserving any line items / extra that came from a prior scan of this expense.
		const hasReceiptDetail = !!(
			d.vendorAddress || d.vendorPhone || d.vendorWebsite || d.vendorTaxId || d.receiptNumber ||
			d.paymentMethod || d.cardLast4 || d.taxRate != null || d.tip != null || d.discount != null ||
			d.shipping != null || (editingReceipt?.lineItems?.length ?? 0) > 0
		);
		const receiptData = hasReceiptDetail
			? JSON.stringify({
					documentKind: editingReceipt?.documentKind ?? null,
					dueDate: d.dueDate,
					vendor: { address: d.vendorAddress, phone: d.vendorPhone, website: d.vendorWebsite, taxId: d.vendorTaxId },
					receiptNumber: d.receiptNumber,
					paymentMethod: d.paymentMethod,
					cardLast4: d.cardLast4,
					taxRate: d.taxRate,
					tip: d.tip,
					discount: d.discount,
					shipping: d.shipping,
					lineItems: editingReceipt?.lineItems ?? [],
					extra: editingReceipt?.extra ?? {}
				})
			: null;
		const data: Record<string, unknown> = {
			portfolioId,
			description: d.description, amount: d.amount, subtotal: d.subtotal, taxAmount: d.taxAmount,
			category: d.category, status: d.status,
			incurredAt: d.incurredAt, dueDate: d.dueDate, paidAt: d.paidAt,
			propertyId: d.propertyId, vendorId: d.vendorId, workOrderId: d.workOrderId,
			billableToOwner: d.billableToOwner, notes: d.notes
		};
		if (receiptData != null) data.receiptData = receiptData;
		saveExpenseMutation.mutate({ id: editingExpenseId, data });
	}

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2 }).format(value || 0);
	}
	const reportYear = $derived(new Date().getFullYear());
	const transactionRows = $derived(transactionsQuery.data?.items ?? []);
	const transactionTotalCount = $derived(transactionsQuery.data?.totalCount ?? 0);
	const transactionStatusOptions = $derived.by(() =>
		transactionKindFilter === 'Payment'
			? PAYMENT_STATUSES
			: transactionKindFilter === 'Expense'
				? EXPENSE_STATUSES
				: transactionKindFilter === 'Bank'
					? ['Unmatched', 'Suggested', 'Matched']
					: [...PAYMENT_STATUSES, ...EXPENSE_STATUSES.filter((status) => !PAYMENT_STATUSES.includes(status)), 'Unmatched', 'Suggested', 'Matched']
	);
	const summary = $derived(accountingSummaryQuery.data as AccountingSummary | undefined);
	const reports = $derived(accountingReportsQuery.data as AccountingReports | undefined);
	const recentLedger = $derived((reports?.ledger ?? []).slice(0, 8));
	const vendorReviewCount = $derived((reports?.vendors1099 ?? []).filter((v) => v.needsW9 || v.needs1099Review).length);

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
	const selectedWorkOrderLabel = $derived(
		(workOrdersQuery.data || []).find((w) => String(w.id) === expenseForm.workOrderId)?.title ?? null
	);

	// ── DataGrid column definitions ───────────────────────────────────────────────

	const transactionColumns: ColumnDef<AccountingTransaction>[] = [
		{
			key: 'date',
			title: 'Date',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'kind',
			title: 'Type',
			sortable: true,
			mobileRole: 'badge',
			cell: transactionKindCell,
		},
		{
			key: 'description',
			title: 'Description',
			sortable: true,
			mobileRole: 'title',
		},
		{
			key: 'counterparty',
			title: 'Counterparty',
			mobileRole: 'subtitle',
			accessor: (t) => t.counterparty ?? '—',
		},
		{
			key: 'amount',
			title: 'Amount',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
		},
		{
			key: 'category',
			title: 'Category',
			mobileRole: 'meta',
		},
		{
			key: 'propertyName',
			title: 'Property',
			mobileRole: 'meta',
			accessor: (t) => t.propertyName ?? 'General',
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: transactionStatusCell,
		},
		{
			key: 'hasReceipt',
			title: 'Receipt',
			mobileRole: 'hidden',
			width: '5rem',
			align: 'center',
			cell: transactionReceiptCell,
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			width: '8rem',
			cell: transactionActionsCell,
		},
	];
</script>

{#snippet transactionKindCell(t: AccountingTransaction)}
	<span class="inline-flex rounded-full border px-2 py-0.5 text-xs font-medium {t.kind === 'Payment' ? 'border-emerald-500/40 bg-emerald-500/10 text-emerald-500' : t.kind === 'Bank' ? 'border-sky-500/40 bg-sky-500/10 text-sky-500' : 'border-amber-500/40 bg-amber-500/10 text-amber-500'}">
		{t.kind}
	</span>
{/snippet}

{#snippet transactionStatusCell(t: AccountingTransaction)}
	<StatusBadge status={t.status} />
{/snippet}

{#snippet transactionReceiptCell(t: AccountingTransaction)}
	{#if t.kind === 'Expense' && t.hasReceipt}
		{#if t.receiptIsImage}
			<a
				href="/expense-file/{t.id}"
				target="_blank"
				rel="noopener noreferrer"
				data-testid="expense-receipt-{t.id}"
				aria-label="View receipt"
				onclick={(ev) => ev.stopPropagation()}
			>
				<img
					src="/expense-file/{t.id}?thumb=true"
					alt="Receipt thumbnail"
					class="h-10 w-10 rounded object-cover ring-1 ring-border"
					loading="lazy"
				/>
			</a>
		{:else}
			<a
				href="/expense-file/{t.id}"
				target="_blank"
				rel="noopener noreferrer"
				data-testid="expense-receipt-{t.id}"
				class="text-xs text-primary underline underline-offset-2 hover:text-primary/80"
				onclick={(ev) => ev.stopPropagation()}
			>PDF</a>
		{/if}
	{/if}
{/snippet}

{#snippet transactionActionsCell(t: AccountingTransaction)}
	<div class="flex items-center gap-1">
		{#if t.kind === 'Bank'}
			<Button
				data-testid="bank-transaction-open"
				variant="outline"
				size="sm"
				onclick={(ev) => { ev.stopPropagation(); goto('/banking'); }}
			>Reconcile</Button>
		{:else if t.kind === 'Payment' && t.status !== 'Paid'}
			<Button
				data-testid="payment-mark-paid"
				variant="outline"
				size="sm"
				onclick={(ev) => { ev.stopPropagation(); markPaidMutation.mutate(t.id); }}
			>Mark Paid</Button>
		{/if}
		{#if t.kind !== 'Bank'}
			<Button
				data-testid="transaction-edit"
				aria-label="Open transaction"
				variant="outline"
				size="icon"
				onclick={(ev) => { ev.stopPropagation(); goto(t.detailHref ?? `/accounting/${t.kind.toLowerCase()}s/${t.id}`); }}
			><Pencil class="h-3.5 w-3.5" /></Button>
			<Button
				data-testid="transaction-delete"
				aria-label="Delete transaction"
				variant="outline"
				size="icon"
				onclick={(ev) => { ev.stopPropagation(); transactionDeleteTarget = t; }}
			><Trash2 class="h-3.5 w-3.5" /></Button>
		{/if}
	</div>
{/snippet}

<svelte:head>
	<title>Accounting - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="accounting-page">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Accounting</h1>
		<p class="text-sm text-muted-foreground">Rent ledger, receivables, expenses, and owner-facing books.</p>
	</div>

	<div class="mb-5 grid gap-4 md:grid-cols-4">
		<Card.Root class="gap-0 py-0" data-testid="accounting-collected">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Collected</p>
				{#if accountingSummaryQuery.isLoading}
					<div class="mt-1 h-8 w-24 animate-pulse rounded bg-muted"></div>
				{:else}
					<p class="text-2xl font-bold font-mono tabular-nums">{money(summary?.payments.collected || 0)}</p>
				{/if}
			</Card.Content>
		</Card.Root>
		<Card.Root class="gap-0 py-0" data-testid="accounting-outstanding">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Outstanding</p>
				{#if accountingSummaryQuery.isLoading}
					<div class="mt-1 h-8 w-24 animate-pulse rounded bg-muted"></div>
				{:else}
					<p class="text-2xl font-bold font-mono tabular-nums">{money(summary?.payments.outstanding || 0)}</p>
				{/if}
			</Card.Content>
		</Card.Root>
		<Card.Root class="gap-0 py-0" data-testid="accounting-overdue">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Overdue</p>
				{#if accountingSummaryQuery.isLoading}
					<div class="mt-1 h-8 w-24 animate-pulse rounded bg-muted"></div>
				{:else}
					<p class="text-2xl font-bold font-mono tabular-nums">{money(summary?.payments.overdue || 0)}</p>
				{/if}
			</Card.Content>
		</Card.Root>
		<Card.Root class="gap-0 py-0" data-testid="accounting-expenses">
			<Card.Content class="p-4">
				<p class="text-xs text-muted-foreground">Expenses</p>
				{#if accountingSummaryQuery.isLoading}
					<div class="mt-1 h-8 w-24 animate-pulse rounded bg-muted"></div>
				{:else}
					<p class="text-2xl font-bold font-mono tabular-nums">{money(summary?.totalExpenses || 0)}</p>
				{/if}
			</Card.Content>
		</Card.Root>
	</div>

	<Card.Root class="mb-6 gap-0 py-0" data-testid="accounting-money-snapshot">
		<Card.Header class="px-4 pt-4 pb-2">
			<Card.Title class="text-base">Money Snapshot</Card.Title>
			<Card.Description>Plain-English accounting summary for this portfolio.</Card.Description>
		</Card.Header>
		<Card.Content class="px-4 pb-4 pt-0">
			{#if accountingSummaryQuery.isLoading}
				<div class="space-y-2">
					<div class="h-5 w-64 animate-pulse rounded bg-muted"></div>
					<div class="h-4 w-full max-w-2xl animate-pulse rounded bg-muted"></div>
				</div>
			{:else if summary?.snapshot}
				<p class="font-medium">{summary.snapshot.title}</p>
				<p class="mt-1 text-sm text-muted-foreground">{summary.snapshot.summary}</p>
				<div class="mt-3 grid gap-2 md:grid-cols-2">
					{#each summary.snapshot.bullets as bullet}
						<div class="rounded-md border border-border bg-background px-3 py-2 text-sm text-muted-foreground">
							{bullet}
						</div>
					{/each}
				</div>
			{:else}
				<p class="text-sm text-muted-foreground">No money snapshot is available yet.</p>
			{/if}
		</Card.Content>
	</Card.Root>

	<div class="mb-6" data-testid="accounting-reports">
		<div class="mb-3 flex items-center justify-between">
			<div>
				<h2 class="text-lg font-semibold">Reports</h2>
				<p class="text-sm text-muted-foreground">Ledger, property profit and loss, Schedule E totals, and 1099 review.</p>
			</div>
			<div class="flex flex-wrap items-center justify-end gap-2">
				<Button variant="outline" size="sm" onclick={() => downloadScheduleECsv(reportYear)} data-testid="accounting-report-schedule-e-export">
					<Download class="h-4 w-4" />
					Schedule E CSV
				</Button>
				<Button variant="outline" size="sm" href="/tax" data-testid="accounting-report-tax-link">
					<FileBarChart class="h-4 w-4" />
					Tax Reports
				</Button>
				<Button variant="outline" size="sm" href="/owners-report" data-testid="accounting-report-owner-link">
					<Landmark class="h-4 w-4" />
					Owner Reports
				</Button>
				<Button variant="outline" size="sm" href="/banking" data-testid="accounting-banking-link">
					<Landmark class="h-4 w-4" />
					Banking
				</Button>
				{#if reports?.generatedAt}
					<p class="w-full text-right text-xs text-muted-foreground">Updated {new Date(reports.generatedAt).toLocaleString()}</p>
				{/if}
			</div>
		</div>

		<div class="grid gap-4 lg:grid-cols-4">
			<Card.Root class="gap-0 py-0">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Net cash flow</p>
					<p class="text-2xl font-bold font-mono tabular-nums">{money(reports?.netCashFlow || 0)}</p>
					<p class="mt-1 text-xs text-muted-foreground">Income {money(reports?.totalIncome || 0)} / expenses {money(reports?.totalExpenses || 0)}</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="gap-0 py-0">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Ledger rows</p>
					<p class="text-2xl font-bold font-mono tabular-nums">{reports?.ledger.length ?? 0}</p>
					<p class="mt-1 text-xs text-muted-foreground">Recent payments and expenses</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="gap-0 py-0">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Properties</p>
					<p class="text-2xl font-bold font-mono tabular-nums">{reports?.properties.length ?? 0}</p>
					<p class="mt-1 text-xs text-muted-foreground">Property-level P&amp;L</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="gap-0 py-0">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">1099 review</p>
					<p class="text-2xl font-bold font-mono tabular-nums">{vendorReviewCount}</p>
					<p class="mt-1 text-xs text-muted-foreground">Vendors needing W-9 or review</p>
				</Card.Content>
			</Card.Root>
		</div>

		<div class="mt-4 space-y-2">
			<details class="group rounded-lg border bg-card" open>
				<summary class="flex cursor-pointer list-none items-center justify-between gap-3 px-4 py-3 text-sm font-semibold">
					<span>Recent Ledger</span>
					<span class="text-xs text-muted-foreground group-open:hidden">Show</span>
					<span class="hidden text-xs text-muted-foreground group-open:inline">Hide</span>
				</summary>
				<div class="border-t px-4 py-3">
					<div class="grid gap-2 md:grid-cols-2 xl:grid-cols-4">
					{#each recentLedger as row}
						<a href={row.sourceHref} class="flex items-center justify-between gap-3 rounded-md px-2 py-1.5 hover:bg-muted/60">
							<span class="min-w-0">
								<span class="block truncate text-sm">{row.description}</span>
								<span class="block text-xs text-muted-foreground">{row.type} · {row.counterparty ?? row.propertyName ?? 'General'}</span>
							</span>
							<span class="shrink-0 font-mono text-sm tabular-nums">{money(row.amount)}</span>
						</a>
					{:else}
						<p class="text-sm text-muted-foreground">No ledger activity yet.</p>
					{/each}
					</div>
				</div>
			</details>
			<details class="group rounded-lg border bg-card">
				<summary class="flex cursor-pointer list-none items-center justify-between gap-3 px-4 py-3 text-sm font-semibold">
					<span>Property P&amp;L</span>
					<span class="text-xs text-muted-foreground group-open:hidden">Show</span>
					<span class="hidden text-xs text-muted-foreground group-open:inline">Hide</span>
				</summary>
				<div class="border-t px-4 py-3">
					<div class="grid gap-2 md:grid-cols-2 xl:grid-cols-3">
					{#each (reports?.properties ?? []).slice(0, 6) as property}
						<a href="/properties/{property.propertyId}" class="flex items-center justify-between gap-3 rounded-md px-2 py-1.5 hover:bg-muted/60">
							<span class="min-w-0">
								<span class="block truncate text-sm">{property.propertyName}</span>
								<span class="block text-xs text-muted-foreground">{money(property.income)} income / {money(property.expenses)} expenses</span>
							</span>
							<span class="shrink-0 font-mono text-sm tabular-nums">{money(property.net)}</span>
						</a>
					{:else}
						<p class="text-sm text-muted-foreground">No property report data yet.</p>
					{/each}
					</div>
				</div>
			</details>
			<details class="group rounded-lg border bg-card">
				<summary class="flex cursor-pointer list-none items-center justify-between gap-3 px-4 py-3 text-sm font-semibold">
					<span>Schedule E</span>
					<span class="text-xs text-muted-foreground group-open:hidden">Show</span>
					<span class="hidden text-xs text-muted-foreground group-open:inline">Hide</span>
				</summary>
				<div class="border-t px-4 py-3">
					<div class="grid gap-2 md:grid-cols-2 xl:grid-cols-4">
					{#each (reports?.scheduleE ?? []).slice(0, 8) as line}
						<div class="flex items-center justify-between gap-3 rounded-md px-2 py-1.5">
							<span class="truncate text-sm">{line.categoryName}</span>
							<span class="shrink-0 font-mono text-sm tabular-nums">{money(line.total)}</span>
						</div>
					{:else}
						<p class="text-sm text-muted-foreground">No Schedule E totals yet.</p>
					{/each}
					</div>
				</div>
			</details>
			<details class="group rounded-lg border bg-card">
				<summary class="flex cursor-pointer list-none items-center justify-between gap-3 px-4 py-3 text-sm font-semibold">
					<span>1099 Review</span>
					<span class="text-xs text-muted-foreground group-open:hidden">Show</span>
					<span class="hidden text-xs text-muted-foreground group-open:inline">Hide</span>
				</summary>
				<div class="border-t px-4 py-3">
					<div class="grid gap-2 md:grid-cols-2 xl:grid-cols-3">
						{#each (reports?.vendors1099 ?? []).slice(0, 9) as vendor}
							<a href="/owners" class="flex items-center justify-between gap-3 rounded-md px-2 py-1.5 hover:bg-muted/60">
								<span class="min-w-0">
									<span class="block truncate text-sm">{vendor.vendorName}</span>
									<span class="block text-xs text-muted-foreground">
										{vendor.needsW9 ? 'Needs W-9' : vendor.needs1099Review ? 'Needs 1099 review' : 'Tracked vendor'}
									</span>
								</span>
								<span class="shrink-0 font-mono text-sm tabular-nums">{money(vendor.totalPaid)}</span>
							</a>
						{:else}
							<p class="text-sm text-muted-foreground">No vendors need 1099 review.</p>
						{/each}
					</div>
				</div>
			</details>
		</div>
	</div>

	<div>
		<div class="mb-3 flex flex-wrap items-center justify-between gap-3">
			<div>
				<h2 class="text-lg font-semibold">Transactions</h2>
				<p class="text-sm text-muted-foreground">Payments, expenses, deposits, and withdrawals in one paged ledger.</p>
			</div>
			<div class="flex flex-wrap gap-2">
				<Button data-testid="payment-create-button" variant="outline" class="shrink-0 gap-2" onclick={openCreatePayment}>
					<Plus class="h-4 w-4" />
					New Payment
				</Button>
				<Button data-testid="expense-create-button" class="shrink-0 gap-2" onclick={openCreateExpense}>
					<Plus class="h-4 w-4" />
					New Expense
				</Button>
			</div>
		</div>
		<DataGrid
			data={transactionRows}
			columns={transactionColumns}
			loading={transactionsQuery.isLoading || transactionsQuery.isFetching}
			emptyMessage="No transactions found."
			getRowKey={(t) => `${t.kind}-${t.id}`}
			getRowTestId={(t) => `transaction-row-${t.kind.toLowerCase()}-${t.id}`}
			onRowClick={(t) => goto(t.detailHref ?? `/accounting/${t.kind.toLowerCase()}s/${t.id}`)}
			data-testid="transactions-list"
			pageSize={PAGE_SIZE}
			page={transactionPage}
			totalCount={transactionTotalCount}
			serverSide
			onPageChange={(page) => (transactionPage = page)}
			sort={transactionSort}
			onSortChange={(sort) => { transactionSort = sort ?? '-date'; transactionPage = 1; }}
		>
			{#snippet toolbar()}
				<div class="grid w-full gap-2 lg:grid-cols-[minmax(14rem,1fr)_9rem_10rem_11rem_12rem_9rem_9rem]">
					<SearchInput bind:value={transactionSearch} placeholder="Search descriptions, property, tenant, vendor…" testid="transaction-search" />
					<Select.Root type="single" bind:value={transactionKindFilter}>
						<Select.Trigger data-testid="transaction-kind-filter">
							{transactionKindFilter || 'All types'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="All types">All types</Select.Item>
							<Select.Item value="Payment" label="Payments">Payments</Select.Item>
							<Select.Item value="Expense" label="Expenses">Expenses</Select.Item>
							<Select.Item value="Bank" label="Bank activity">Bank activity</Select.Item>
						</Select.Content>
					</Select.Root>
					<Select.Root type="single" bind:value={transactionStatusFilter}>
						<Select.Trigger data-testid="transaction-status-filter">
							{transactionStatusFilter || 'All statuses'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="All statuses">All statuses</Select.Item>
							{#each transactionStatusOptions as s}
								<Select.Item value={s} label={s}>{s}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
					<Select.Root type="single" bind:value={transactionCategoryFilter}>
						<Select.Trigger data-testid="transaction-category-filter">
							{transactionCategoryFilter || 'All categories'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="All categories">All categories</Select.Item>
							{#each (transactionKindFilter === 'Payment' ? PAYMENT_TYPES : transactionKindFilter === 'Expense' ? EXPENSE_CATEGORIES : transactionKindFilter === 'Bank' ? ['Deposit', 'Withdrawal'] : [...PAYMENT_TYPES, ...EXPENSE_CATEGORIES, 'Deposit', 'Withdrawal']) as c}
								<Select.Item value={c} label={c}>{c}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
					<Select.Root type="single" bind:value={transactionPropertyFilter}>
						<Select.Trigger data-testid="transaction-property-filter">
							{propertiesQuery.data?.find((p) => String(p.id) === transactionPropertyFilter)?.name ?? 'All properties'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="All properties">All properties</Select.Item>
							{#each propertiesQuery.data || [] as property}
								<Select.Item value={String(property.id)} label={property.name}>{property.name}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
					<Input data-testid="transaction-from-filter" type="date" bind:value={transactionFromFilter} aria-label="From date" />
					<Input data-testid="transaction-to-filter" type="date" bind:value={transactionToFilter} aria-label="To date" />
				</div>
			{/snippet}
		</DataGrid>
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
				<Select.Root type="single" bind:value={paymentForm.paymentType}>
					<Select.Trigger class="w-full" data-testid="payment-type-input">
						{paymentForm.paymentType || 'Select type'}
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
	<Dialog.Content class="max-h-[85vh] max-w-2xl overflow-y-auto">
		<Dialog.Header>
			<Dialog.Title>{editingExpenseId == null ? 'New Expense' : 'Edit Expense'}</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-4" data-testid="expense-form">
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Description</span>
				<Input data-testid="expense-description-input" bind:value={expenseForm.description} placeholder="Description" />
				{#if expenseErrors.description}<p class="mt-1 text-xs text-destructive" data-testid="expense-description-error">{expenseErrors.description}</p>{/if}
			</div>
			<div class="grid grid-cols-3 gap-2">
				<div>
					<span class="mb-1 block text-xs text-muted-foreground">Amount</span>
					<Input data-testid="expense-amount-input" bind:value={expenseForm.amount} placeholder="0.00" />
					{#if expenseErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="expense-amount-error">{expenseErrors.amount}</p>{/if}
				</div>
				<div>
					<span class="mb-1 block text-xs text-muted-foreground">Subtotal</span>
					<Input data-testid="expense-subtotal-input" bind:value={expenseForm.subtotal} placeholder="0.00" />
					{#if expenseErrors.subtotal}<p class="mt-1 text-xs text-destructive">{expenseErrors.subtotal}</p>{/if}
				</div>
				<div>
					<span class="mb-1 block text-xs text-muted-foreground">Tax</span>
					<Input data-testid="expense-tax-input" bind:value={expenseForm.taxAmount} placeholder="0.00" />
					{#if expenseErrors.taxAmount}<p class="mt-1 text-xs text-destructive">{expenseErrors.taxAmount}</p>{/if}
				</div>
			</div>
			<div class="grid grid-cols-3 gap-2">
				<div>
					<span class="mb-1 block text-xs text-muted-foreground">Incurred</span>
					<Input data-testid="expense-incurred-input" type="date" bind:value={expenseForm.incurredAt} />
					{#if expenseErrors.incurredAt}<p class="mt-1 text-xs text-destructive" data-testid="expense-incurred-error">{expenseErrors.incurredAt}</p>{/if}
				</div>
				<div>
					<span class="mb-1 block text-xs text-muted-foreground">Due date</span>
					<Input data-testid="expense-due-input" type="date" bind:value={expenseForm.dueDate} />
				</div>
				<div>
					<span class="mb-1 block text-xs text-muted-foreground">Paid date</span>
					<Input data-testid="expense-paid-input" type="date" bind:value={expenseForm.paidAt} />
				</div>
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<span class="mb-1 block text-xs text-muted-foreground">Category</span>
					<Select.Root type="single" bind:value={expenseForm.category}>
						<Select.Trigger class="w-full" data-testid="expense-category-input">
							{expenseForm.category || 'Select category'}
						</Select.Trigger>
						<Select.Content>
							{#each EXPENSE_CATEGORIES as cat}
								<Select.Item value={cat} label={cat}>{cat}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div>
					<span class="mb-1 block text-xs text-muted-foreground">Status</span>
					<Select.Root type="single" bind:value={expenseForm.status}>
						<Select.Trigger class="w-full" data-testid="expense-status-input">
							{expenseForm.status || 'Select status'}
						</Select.Trigger>
						<Select.Content>
							{#each EXPENSE_STATUSES as s}
								<Select.Item value={s} label={s}>{s}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
			</div>
			<div class="grid grid-cols-3 gap-2">
				<div>
					<span class="mb-1 block text-xs text-muted-foreground">Property</span>
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
				</div>
				<div>
					<span class="mb-1 block text-xs text-muted-foreground">Vendor</span>
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
				<div>
					<span class="mb-1 block text-xs text-muted-foreground">Work order</span>
					<Select.Root type="single" bind:value={expenseForm.workOrderId}>
						<Select.Trigger class="w-full" data-testid="expense-workorder-input">
							{selectedWorkOrderLabel ?? 'No work order'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="No work order">No work order</Select.Item>
							{#each workOrdersQuery.data || [] as wo}
								<Select.Item value={String(wo.id)} label={wo.title}>{wo.title}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
			</div>
			<label class="flex items-center gap-2 text-sm">
				<Checkbox bind:checked={expenseForm.billableToOwner} data-testid="expense-billable-input" />
				Billable to owner
			</label>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Notes</span>
				<textarea data-testid="expense-notes-input" bind:value={expenseForm.notes} rows="2" class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"></textarea>
			</div>
			<div class="border-t border-border pt-3">
				<h3 class="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">Receipt details (optional)</h3>
				<div class="space-y-2">
					<div>
						<span class="mb-1 block text-xs text-muted-foreground">Vendor address</span>
						<Input data-testid="expense-vendor-address-input" bind:value={expenseForm.vendorAddress} />
					</div>
					<div class="grid grid-cols-3 gap-2">
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Vendor phone</span>
							<Input data-testid="expense-vendor-phone-input" bind:value={expenseForm.vendorPhone} />
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Vendor website</span>
							<Input data-testid="expense-vendor-website-input" bind:value={expenseForm.vendorWebsite} />
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Vendor tax ID</span>
							<Input data-testid="expense-vendor-taxid-input" bind:value={expenseForm.vendorTaxId} />
						</div>
					</div>
					<div class="grid grid-cols-3 gap-2">
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Receipt #</span>
							<Input data-testid="expense-receipt-number-input" bind:value={expenseForm.receiptNumber} />
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Payment method</span>
							<Input data-testid="expense-payment-method-input" bind:value={expenseForm.paymentMethod} />
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Card last 4</span>
							<Input data-testid="expense-card-last4-input" bind:value={expenseForm.cardLast4} />
						</div>
					</div>
					<div class="grid grid-cols-4 gap-2">
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Tax rate</span>
							<Input data-testid="expense-tax-rate-input" bind:value={expenseForm.taxRate} />
							{#if expenseErrors.taxRate}<p class="mt-1 text-xs text-destructive">{expenseErrors.taxRate}</p>{/if}
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Tip</span>
							<Input data-testid="expense-tip-input" bind:value={expenseForm.tip} />
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Discount</span>
							<Input data-testid="expense-discount-input" bind:value={expenseForm.discount} />
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Shipping</span>
							<Input data-testid="expense-shipping-input" bind:value={expenseForm.shipping} />
						</div>
					</div>
				</div>
			</div>
		</div>
		<Dialog.Footer>
			<Button data-testid="expense-form-cancel" variant="outline" onclick={closeExpenseForm}>Cancel</Button>
			<Button data-testid="expense-form-save" onclick={submitExpense} disabled={saveExpenseMutation.isPending}>{saveExpenseMutation.isPending ? 'Saving…' : 'Save Expense'}</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={transactionDeleteTarget !== null}
	title="Delete transaction"
	message={transactionDeleteTarget ? `Delete this ${transactionDeleteTarget.kind.toLowerCase()} for ${money(transactionDeleteTarget.amount)}?` : ''}
	busy={deleteTransactionMutation.isPending}
	testid="transaction-delete"
	onconfirm={() => transactionDeleteTarget && deleteTransactionMutation.mutate(transactionDeleteTarget)}
	oncancel={() => (transactionDeleteTarget = null)}
/>
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
	message={expenseDeleteTarget ? `Delete "${expenseDeleteTarget.description}"?` : ''}
	busy={deleteExpenseMutation.isPending}
	testid="expense-delete"
	onconfirm={() => expenseDeleteTarget && deleteExpenseMutation.mutate(expenseDeleteTarget.id)}
	oncancel={() => (expenseDeleteTarget = null)}
/>
