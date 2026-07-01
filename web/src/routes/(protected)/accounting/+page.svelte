<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { payments } from '$lib/api/endpoints/payments';
	import { expenses } from '$lib/api/endpoints/expenses';
	import {
		accounting,
		downloadScheduleECsv,
		type ReconciledAccountingTransaction
	} from '$lib/api/endpoints/accounting';
	import { leases } from '$lib/api/endpoints/leases';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { properties } from '$lib/api/endpoints/properties';
	import { units } from '$lib/api/endpoints/units';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import type { Payment, Expense, AccountingReports, AccountingSummary, AccountingTransaction, Vendor, WorkOrder } from '$lib/types';
	import { recordHref } from '$lib/navigation/record-href';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { PAYMENT_METHODS } from '$lib/constants/payments';
	import { paymentSchema, expenseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { paymentTypeLabel } from '$lib/utils/payment-labels';
	import {
		EXPENSE_CATEGORIES,
		EXPENSE_CATEGORY_OPTIONS,
		formatExpenseCategory
	} from '$lib/accounting/expense-categories';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import StepperNextButton from '$lib/components/shared/StepperNextButton.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import { Pencil, Trash2, Plus, Download, FileBarChart, Landmark, Check, Sparkles } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';
	import * as Tabs from '$lib/components/ui/tabs';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import HelpTooltip from '$lib/components/ui/HelpTooltip.svelte';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import { clearFieldError } from '$lib/forms/form-errors';
	import * as Tooltip from '$lib/components/ui/tooltip';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { formatLeasePickerLabel } from '$lib/accounting/payment-detail-display';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const accountingTabs = [
		{ value: 'ledger', label: 'Ledger' },
		{ value: 'reports', label: 'Reports' },
		{ value: 'overview', label: 'Overview' },
	];
	// Landing on the day-to-day Ledger view; Reports and Overview are secondary. Seeded from the URL so
	// a deep-linked / Back-navigated tab is restored (the grid-state persistence below keeps it synced).
	const initialTab = page.url.searchParams.get('tab');
	let activeTab = $state(
		initialTab && accountingTabs.some((t) => t.value === initialTab) ? initialTab : 'ledger'
	);
	const PAGE_SIZE = 20;
	const PAYMENT_STATUSES = ['Scheduled', 'Paid', 'Partial', 'Late', 'Waived'];
	const PAYMENT_TYPES = ['Rent', 'SecurityDeposit', 'LateFee', 'Utility', 'Other'];
	const EXPENSE_STATUSES = ['Pending', 'Approved', 'Paid'];

	// --- Ledger grid state, persisted in the URL query string -------------------
	// Filter / sort / search / paging (and the active tab) round-trip through the URL so they survive
	// navigation away-and-back and browser Back/Forward (EdiPlatform's grid pattern). Seed the initial
	// values from the current URL on mount, then mirror state -> URL via a replaceState goto.
	const initialParams = page.url.searchParams;
	const DEFAULT_SORT = '-createdAt';

	let transactionSearch = $state(readGridParam(initialParams, 'q'));
	let transactionKindFilter = $state(readGridParam(initialParams, 'kind'));
	let transactionStatusFilter = $state(readGridParam(initialParams, 'status'));
	let transactionCategoryFilter = $state(readGridParam(initialParams, 'category'));
	let transactionPropertyFilter = $state(readGridParam(initialParams, 'property'));
	let transactionFromFilter = $state(readGridParam(initialParams, 'from'));
	let transactionToFilter = $state(readGridParam(initialParams, 'to'));
	let transactionPage = $state(readGridParam(initialParams, 'page', 1));
	// Default newest-entered-first: a just-scanned item lands at the top of the ledger even when its
	// transaction date is wrong/old. The "Date" (transaction date) column stays sortable too.
	let transactionSort = $state(readGridParam(initialParams, 'sort') || DEFAULT_SORT);
	let transactionDeleteTarget = $state<AccountingTransaction | null>(null);
	const debouncedTransactionSearch = debounced(() => transactionSearch, 300);
	const selectedPropertyFilter = $derived(transactionPropertyFilter ? Number(transactionPropertyFilter) : undefined);

	// Reset to page 1 whenever a filter/search changes — but NOT on the initial mount, so a deep-linked
	// or restored ?page=3 loads as-is instead of being clobbered back to 1.
	let filterResetPrimed = false;
	$effect(() => {
		debouncedTransactionSearch.value;
		transactionKindFilter;
		transactionStatusFilter;
		transactionCategoryFilter;
		transactionPropertyFilter;
		transactionFromFilter;
		transactionToFilter;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		transactionPage = 1;
	});

	// Mirror the current ledger grid state into the URL query string (state -> URL). syncGridUrl uses a
	// replaceState goto so each keystroke doesn't stack history, omits empties/defaults to keep the URL
	// tidy, and no-ops when the URL already matches (so this effect can't loop). Going away and back —
	// or browser Back/Forward — remounts the page, and the $state seeds above read these params straight
	// back. TanStack already re-fetches off the state vars, so this is purely the persistence layer.
	$effect(() => {
		syncGridUrl(
			{
				tab: activeTab,
				q: transactionSearch,
				kind: transactionKindFilter,
				status: transactionStatusFilter,
				category: transactionCategoryFilter,
				property: transactionPropertyFilter,
				from: transactionFromFilter,
				to: transactionToFilter,
				page: transactionPage,
				sort: transactionSort,
			},
			{ tab: 'ledger', page: 1, sort: DEFAULT_SORT }
		);
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
	// amountPaid is the cash-collected-so-far split that only applies to a Partial payment; blank for
	// every other status (dropped on submit so the server clears it).
	const emptyPayment = { leaseId: '', amount: '', amountPaid: '', dueDate: '', paymentType: 'Rent', status: 'Scheduled' };
	let showPaymentForm = $state(false);
	let editingPaymentId = $state<number | null>(null);
	let paymentForm = $state({ ...emptyPayment });
	let paymentErrors = $state<Record<string, string>>({});
	let paymentDeleteTarget = $state<Payment | null>(null);

	function clearPaymentError(field: string) {
		if (!paymentErrors[field]) return;
		const next = { ...paymentErrors };
		delete next[field];
		paymentErrors = next;
	}

	$effect(() => {
		if (paymentForm.leaseId) clearPaymentError('leaseId');
	});
	$effect(() => {
		if (paymentForm.amount) clearPaymentError('amount');
	});
	$effect(() => {
		if (paymentForm.dueDate) clearPaymentError('dueDate');
	});
	$effect(() => {
		if (paymentForm.amountPaid) clearPaymentError('amountPaid');
	});

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

	// --- Mark Paid modal ---
	// Capture how the money arrived (method / reference) + when, with smart defaults so one click
	// is still fast: date = today, method = the last method this user picked (remembered in
	// localStorage). The mark-paid endpoint accepts paidDate / method / externalReference / notes; the
	// payment record already carries its own amount, so there's no amount field here. Methods come from
	// the shared canonical list so web + mobile offer identical values.
	const LAST_METHOD_KEY = 'rc.payments.lastMethod';
	function loadLastMethod(): string {
		if (typeof localStorage === 'undefined') return '';
		try {
			return localStorage.getItem(LAST_METHOD_KEY) ?? '';
		} catch {
			return '';
		}
	}
	function rememberLastMethod(method: string) {
		if (!method || typeof localStorage === 'undefined') return;
		try {
			localStorage.setItem(LAST_METHOD_KEY, method);
		} catch {
			/* storage may be unavailable */
		}
	}
	const emptyMarkPaid = { paidDate: '', method: '', externalReference: '', notes: '' };
	let showMarkPaidForm = $state(false);
	let markPaidTarget = $state<AccountingTransaction | null>(null);
	let markPaidForm = $state({ ...emptyMarkPaid });

	function todayLocal(): string {
		const d = new Date();
		const p = (n: number) => String(n).padStart(2, '0');
		return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
	}

	// A ledger row's destination: unit-tied Payment/Expense rows fold into their unit's Command Center
	// tab (via recordHref); Bank rows (no detail page / no unit) keep the server detailHref fallback.
	function ledgerHref(t: AccountingTransaction): string {
		if (t.kind === 'Payment') return recordHref('payment', { id: t.id, unitId: t.unitId });
		if (t.kind === 'Expense') return recordHref('expense', { id: t.id, unitId: t.unitId });
		return t.detailHref ?? `/accounting/${t.kind.toLowerCase()}s/${t.id}`;
	}

	function openMarkPaid(t: AccountingTransaction) {
		markPaidTarget = t;
		markPaidForm = { paidDate: todayLocal(), method: loadLastMethod(), externalReference: '', notes: '' };
		showMarkPaidForm = true;
	}
	function closeMarkPaid() {
		showMarkPaidForm = false;
		markPaidTarget = null;
	}

	const markPaidMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number; data: Record<string, unknown> }) => payments.markPaid(id, data),
		onSuccess: (_r, vars) => {
			showSuccess('Payment marked paid.');
			rememberLastMethod(String(vars.data.method ?? ''));
			closeMarkPaid();
			invalidatePayments();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitMarkPaid() {
		if (!markPaidTarget) return;
		const data: Record<string, unknown> = {};
		if (markPaidForm.paidDate) data.paidDate = markPaidForm.paidDate;
		if (markPaidForm.method) data.method = markPaidForm.method;
		if (markPaidForm.externalReference.trim()) data.externalReference = markPaidForm.externalReference.trim();
		if (markPaidForm.notes.trim()) data.notes = markPaidForm.notes.trim();
		markPaidMutation.mutate({ id: markPaidTarget.id, data });
	}

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
		paymentForm = { leaseId: String(p.leaseId), amount: String(p.amount), amountPaid: p.amountPaid != null ? String(p.amountPaid) : '', dueDate: p.dueDate?.slice(0, 10) ?? '', paymentType: p.paymentType, status: p.status };
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
		// amountPaid only travels for a Partial payment. For any other status send null so the server
		// clears a stale collected-so-far (e.g. when editing a payment away from Partial); mirrors
		// PaymentService.NormalizeAmountPaid, which nulls AmountPaid for non-Partial statuses.
		const { amountPaid, ...rest } = result.data;
		const data: Record<string, unknown> =
			rest.status === 'Partial' ? { portfolioId, ...rest, amountPaid } : { portfolioId, ...rest, amountPaid: null };
		savePaymentMutation.mutate({ id: editingPaymentId, data });
	}

	// --- Expense form/dialog ---
	const emptyExpense = {
		category: 'Repairs', description: '', amount: '', subtotal: '', taxAmount: '',
		incurredAt: '', dueDate: '', paidAt: '', propertyId: '', unitId: '', vendorId: '', workOrderId: '',
		status: 'Pending', billableToOwner: false, notes: '',
		vendorAddress: '', vendorPhone: '', vendorWebsite: '', vendorTaxId: '',
		receiptNumber: '', paymentMethod: '', cardLast4: '', taxRate: '', tip: '', discount: '', shipping: ''
	};
	let showExpenseForm = $state(false);
	let editingExpenseId = $state<number | null>(null);
	let expenseForm = $state({ ...emptyExpense });
	let expenseErrors = $state<Record<string, string>>({});
	let expenseStep = $state(0);
	let completedExpenseSteps = $state<number[]>([]);
	let expenseDeleteTarget = $state<Expense | null>(null);
	let lastExpenseVendorAutofillId = $state('');
	let lastExpenseWorkOrderAutofillId = $state('');
	// Holds the parsed ReceiptData of the expense being edited, so line items / extra survive a re-save.
	let editingReceipt = $state<Record<string, any> | null>(null);
	const expensePropertyId = $derived(expenseForm.propertyId ? Number(expenseForm.propertyId) : null);
	const expenseUnitsQuery = createQuery(() => ({
		queryKey: ['units', portfolioId, 'expense-form', expensePropertyId],
		queryFn: () => units.list({ propertyId: expensePropertyId ?? undefined, take: 500, sort: 'unitNumber' }),
		enabled: showExpenseForm && expensePropertyId != null
	}));

	const expenseSteps: FormStepperStep[] = [
		{ id: 'source', label: 'Source', description: 'Links and defaults' },
		{ id: 'details', label: 'Details', description: 'Description and amount' },
		{ id: 'dates', label: 'Dates', description: 'Incurred and paid' },
		{ id: 'context', label: 'Context', description: 'Category and notes' },
		{ id: 'vendor', label: 'Vendor', description: 'Receipt contact' },
		{ id: 'receipt', label: 'Receipt', description: 'Payment proof' },
		{ id: 'adjustments', label: 'Adjustments', description: 'Tax and extras' }
	];
	const expenseStepFields = [
		['workOrderId', 'propertyId', 'unitId', 'vendorId'],
		['description', 'amount', 'subtotal', 'taxAmount'],
		['incurredAt', 'dueDate', 'paidAt'],
		['category', 'status', 'billableToOwner', 'notes'],
		['vendorAddress', 'vendorPhone', 'vendorWebsite', 'vendorTaxId'],
		['receiptNumber', 'paymentMethod', 'cardLast4'],
		['taxRate', 'tip', 'discount', 'shipping']
	] as const;

	function clearExpenseError(field: string) {
		const next = clearFieldError(expenseErrors, field);
		if (next !== expenseErrors) expenseErrors = next;
	}

	$effect(() => {
		if (expenseForm.description.trim()) clearExpenseError('description');
	});
	$effect(() => {
		if (expenseForm.amount) clearExpenseError('amount');
	});
	$effect(() => {
		if (expenseForm.incurredAt) clearExpenseError('incurredAt');
	});
	$effect(() => {
		if (expenseForm.subtotal) clearExpenseError('subtotal');
	});
	$effect(() => {
		if (expenseForm.taxAmount) clearExpenseError('taxAmount');
	});
	$effect(() => {
		if (expenseForm.taxRate) clearExpenseError('taxRate');
	});

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
		expenseStep = 0;
		completedExpenseSteps = [];
		lastExpenseVendorAutofillId = '';
		lastExpenseWorkOrderAutofillId = '';
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
			unitId: e.unitId != null ? String(e.unitId) : '',
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
		expenseStep = 0;
		completedExpenseSteps = [];
		lastExpenseVendorAutofillId = '';
		lastExpenseWorkOrderAutofillId = '';
		showExpenseForm = true;
	}
	function vendorAddress(vendor: Vendor) {
		const region = [vendor.state, vendor.postalCode].filter(Boolean).join(' ');
		const cityLine = [vendor.city, region].filter(Boolean).join(', ');
		return [vendor.addressLine1, cityLine].filter(Boolean).join(', ');
	}
	function fillExpenseTextField(field: keyof typeof emptyExpense, value: string | number | null | undefined) {
		if (value == null || value === '') return;
		if (typeof expenseForm[field] === 'boolean') return;
		if (String(expenseForm[field] ?? '').trim()) return;
		(expenseForm as Record<string, string | boolean>)[field] = String(value);
	}
	function autofillExpenseFromVendor(vendor: Vendor | undefined) {
		if (!vendor) return false;
		fillExpenseTextField('vendorAddress', vendorAddress(vendor));
		fillExpenseTextField('vendorPhone', vendor.phone);
		fillExpenseTextField('vendorWebsite', vendor.website);
		fillExpenseTextField('vendorTaxId', vendor.taxId);
		return true;
	}
	function autofillExpenseFromVendorId(vendorId: string) {
		if (!vendorId) return false;
		const vendor = (vendorsQuery.data || []).find((v) => String(v.id) === vendorId);
		return autofillExpenseFromVendor(vendor);
	}
	function dateOnly(value: string | null | undefined) {
		return value?.slice(0, 10);
	}
	function autofillExpenseFromWorkOrder(workOrder: WorkOrder | undefined) {
		if (!workOrder) return false;
		fillExpenseTextField('propertyId', workOrder.propertyId);
		fillExpenseTextField('unitId', workOrder.unitId);
		fillExpenseTextField('description', workOrder.title);
		fillExpenseTextField('amount', workOrder.actualCost ?? workOrder.estimatedCost);
		fillExpenseTextField('incurredAt', dateOnly(workOrder.completedAt ?? workOrder.scheduledFor ?? workOrder.requestedAt));
		if (workOrder.vendorId && !expenseForm.vendorId) {
			expenseForm.vendorId = String(workOrder.vendorId);
		}
		return true;
	}
	function autofillExpenseFromWorkOrderId(workOrderId: string) {
		if (!workOrderId) return false;
		const workOrder = (workOrdersQuery.data || []).find((wo) => String(wo.id) === workOrderId);
		return autofillExpenseFromWorkOrder(workOrder);
	}
	$effect(() => {
		const vendorId = expenseForm.vendorId;
		vendorsQuery.data;
		if (!showExpenseForm || !vendorId) {
			if (!vendorId) lastExpenseVendorAutofillId = '';
			return;
		}
		if (lastExpenseVendorAutofillId === vendorId) return;
		if (autofillExpenseFromVendorId(vendorId)) lastExpenseVendorAutofillId = vendorId;
	});
	$effect(() => {
		const workOrderId = expenseForm.workOrderId;
		workOrdersQuery.data;
		if (!showExpenseForm || !workOrderId) {
			if (!workOrderId) lastExpenseWorkOrderAutofillId = '';
			return;
		}
		if (lastExpenseWorkOrderAutofillId === workOrderId) return;
		if (autofillExpenseFromWorkOrderId(workOrderId)) lastExpenseWorkOrderAutofillId = workOrderId;
	});
	function closeExpenseForm() {
		showExpenseForm = false;
		editingExpenseId = null;
		expenseErrors = {};
		expenseStep = 0;
		completedExpenseSteps = [];
		lastExpenseVendorAutofillId = '';
		lastExpenseWorkOrderAutofillId = '';
	}
	function expenseStepErrorFields(step: number, errors: Record<string, string>) {
		const visibleFields = new Set<string>(expenseStepFields[step] ?? []);
		return Object.entries(errors).filter(([field]) => visibleFields.has(field));
	}
	function firstExpenseErrorStep(errors: Record<string, string>) {
		return expenseStepFields.findIndex((fields) => fields.some((field) => errors[field]));
	}
	function markExpenseStepInvalid(step: number) {
		completedExpenseSteps = completedExpenseSteps.filter((completedStep) => completedStep < step);
	}
	function validateExpenseStep(step: number) {
		const result = parseForm(expenseSchema, expenseForm);
		const currentErrors = result.errors ? Object.fromEntries(expenseStepErrorFields(step, result.errors)) : {};
		const currentFields = new Set<string>(expenseStepFields[step] ?? []);
		const nextErrors = Object.fromEntries(Object.entries(expenseErrors).filter(([field]) => !currentFields.has(field)));
		expenseErrors = { ...nextErrors, ...currentErrors };
		const isValid = Object.keys(currentErrors).length === 0;
		if (!isValid) markExpenseStepInvalid(step);
		return isValid;
	}
	function nextExpenseStep() {
		if (!validateExpenseStep(expenseStep)) return;
		if (completedExpenseSteps.includes(expenseStep)) {
			expenseStep = Math.min(expenseStep + 1, expenseSteps.length - 1);
			return;
		}
		completedExpenseSteps = [...completedExpenseSteps, expenseStep];
		window.setTimeout(() => {
			expenseStep = Math.min(expenseStep + 1, expenseSteps.length - 1);
		}, 260);
	}
	function submitExpense() {
		const result = parseForm(expenseSchema, expenseForm);
		if (result.errors) {
			expenseErrors = result.errors;
			const firstErrorStep = firstExpenseErrorStep(result.errors);
			if (firstErrorStep >= 0) {
				expenseStep = firstErrorStep;
				markExpenseStepInvalid(firstErrorStep);
			}
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
			propertyId: d.propertyId, unitId: d.unitId, vendorId: d.vendorId, workOrderId: d.workOrderId,
			billableToOwner: d.billableToOwner, notes: d.notes
		};
		if (receiptData != null) data.receiptData = receiptData;
		saveExpenseMutation.mutate({ id: editingExpenseId, data });
	}

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2 }).format(value || 0);
	}
	// Short, compact date for the inline reconciliation chips (e.g. "Jun 3").
	function shortDate(value: string | null | undefined) {
		if (!value) return '';
		return new Date(value).toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
	}
	// Calendar day for the "Entered" column — rendered in UTC to match the grid's date columns so a
	// UTC timestamp shows the same day for every viewer instead of drifting a day back.
	function enteredDate(value: string | null | undefined) {
		if (!value) return '–';
		const d = new Date(value);
		return isNaN(d.getTime()) ? '–' : d.toLocaleDateString(undefined, { timeZone: 'UTC' });
	}
	// Full local timestamp for the "Entered" tooltip.
	function fullStamp(value: string | null | undefined) {
		if (!value) return '';
		const d = new Date(value);
		return isNaN(d.getTime()) ? '' : d.toLocaleString();
	}
	// True when a row was edited meaningfully after creation (>1 min apart), so the "Entered" cell can
	// hint that it's been touched and surface the updated time.
	function wasEdited(created: string | null | undefined, updated: string | null | undefined) {
		if (!created || !updated) return false;
		const c = new Date(created).getTime();
		const u = new Date(updated).getTime();
		return !isNaN(c) && !isNaN(u) && u - c > 60_000;
	}

	// One-tap confirm of a suggested bank match for a ledger row. Optimistic: the row's
	// suggestedBankMatch is consumed and `reconciled` flips to true so the chip becomes the green
	// "Cleared" badge immediately; we invalidate accounting-transactions so the server truth (cleared
	// bank name/date) lands. Never auto-confirmed — only fires when the user taps the chip.
	let confirmingMatchId = $state<number | null>(null);
	const confirmBankMatchMutation = createMutation(() => ({
		mutationFn: (bankTransactionId: number) => accounting.confirmBankMatch(bankTransactionId),
		onMutate: (bankTransactionId: number) => {
			confirmingMatchId = bankTransactionId;
		},
		onSuccess: () => {
			showSuccess('Matched to your bank — marked cleared.');
			invalidatePayments();
			invalidateExpenses();
		},
		onError: (err) => showError(apiErrorMessage(err)),
		onSettled: () => {
			confirmingMatchId = null;
		}
	}));
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

	const selectedLeaseLabel = $derived.by(() => {
		const lease = (leasesQuery.data || []).find((l) => String(l.id) === paymentForm.leaseId);
		return lease ? formatLeasePickerLabel(lease) : null;
	});
	const selectedPropertyLabel = $derived(
		(propertiesQuery.data || []).find((p) => String(p.id) === expenseForm.propertyId)?.name ?? null
	);
	const selectedVendorLabel = $derived(
		(vendorsQuery.data || []).find((v) => String(v.id) === expenseForm.vendorId)?.name ?? null
	);
	const selectedUnitLabel = $derived(
		(expenseUnitsQuery.data || []).find((u) => String(u.id) === expenseForm.unitId)?.unitNumber ?? null
	);
	const selectedWorkOrderLabel = $derived(
		(workOrdersQuery.data || []).find((w) => String(w.id) === expenseForm.workOrderId)?.title ?? null
	);

	// ── DataGrid column definitions ───────────────────────────────────────────────

	const transactionColumns: ColumnDef<ReconciledAccountingTransaction>[] = [
		{
			key: 'date',
			title: 'Date',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			// "Entered" = when the row was scanned/created. Default sort so freshly-scanned items
			// land at the top even when their transaction Date is wrong/old. Tooltip reveals the
			// edited time when it differs from created.
			key: 'createdAt',
			title: 'Entered',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
			cell: transactionEnteredCell,
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
			accessor: (t) => t.kind === 'Expense' ? formatExpenseCategory(t.category) : t.category,
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
			key: 'bank',
			title: 'Bank',
			mobileRole: 'meta',
			width: '13rem',
			cell: transactionBankCell,
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
			isAction: true,
			mobileRole: 'hidden',
			cell: transactionActionsCell,
		},
	];
</script>

{#snippet transactionEnteredCell(t: AccountingTransaction)}
	{@const edited = wasEdited(t.createdAt, t.updatedAt)}
	<span
		class="inline-flex items-center gap-1"
		title={`Entered ${fullStamp(t.createdAt)}${edited ? ` · Edited ${fullStamp(t.updatedAt)}` : ''}`}
		data-testid="transaction-entered-{t.kind.toLowerCase()}-{t.id}"
	>
		{enteredDate(t.createdAt)}
		{#if edited}
			<span class="text-[10px] font-normal uppercase tracking-wide text-muted-foreground">· edited</span>
		{/if}
	</span>
{/snippet}

{#snippet transactionKindCell(t: AccountingTransaction)}
	<span class="m3-tone-chip inline-flex rounded-full border px-2 py-0.5 text-xs font-medium {t.kind === 'Payment' ? 'm3-tone--success' : t.kind === 'Bank' ? 'm3-tone--info' : 'm3-tone--warning'}">
		{t.kind}
	</span>
{/snippet}

{#snippet transactionStatusCell(t: AccountingTransaction)}
	<StatusBadge status={t.status} />
{/snippet}

{#snippet transactionBankCell(t: ReconciledAccountingTransaction)}
	{#if t.kind === 'Bank'}
		<!-- Bank rows are the source of truth, not reconciled against anything. -->
		<span class="text-xs text-muted-foreground">—</span>
	{:else if t.reconciled}
		<span
			data-testid="txn-reconciled-{t.id}"
			class="inline-flex max-w-full items-center gap-1 rounded-full border border-success/40 bg-success/10 px-2 py-0.5 text-xs font-medium text-success"
			title={`Cleared${t.clearedBankName ? ' · ' + t.clearedBankName : ''}${t.clearedAt ? ' · ' + shortDate(t.clearedAt) : ''}`}
		>
			<Check class="h-3 w-3 shrink-0" />
			<span class="truncate">
				Cleared{t.clearedBankName ? ` · ${t.clearedBankName}` : ''}{t.clearedAt ? ` · ${shortDate(t.clearedAt)}` : ''}
			</span>
		</span>
	{:else if t.suggestedBankMatch}
		{@const match = t.suggestedBankMatch}
		<Tooltip.Provider delayDuration={150}>
			<Tooltip.Root>
				<Tooltip.Trigger
					data-testid="txn-match-chip-{t.id}"
					class="inline-flex items-center gap-1 rounded-full border border-primary/40 bg-primary/10 px-2 py-0.5 text-xs font-medium text-primary transition-colors hover:bg-primary/20 disabled:cursor-not-allowed disabled:opacity-60"
					disabled={confirmingMatchId === match.bankTransactionId}
					aria-label={`Confirm bank match: ${match.name}, ${money(match.amount)} on ${shortDate(match.date)}`}
					onclick={(ev: MouseEvent) => {
						ev.stopPropagation();
						confirmBankMatchMutation.mutate(match.bankTransactionId);
					}}
				>
					<Sparkles class="h-3 w-3 shrink-0" />
					{confirmingMatchId === match.bankTransactionId ? 'Confirming…' : 'Match?'}
				</Tooltip.Trigger>
				<Tooltip.Content side="top" class="max-w-xs">
					<div class="space-y-1" data-testid="txn-match-confirm-{t.id}">
						<p class="text-xs font-semibold">Found a matching bank line</p>
						<p class="text-xs">
							{match.name} ·
							<span class="font-mono tabular-nums">{money(match.amount)}</span>
							· {shortDate(match.date)}
						</p>
						<p class="text-[11px] text-muted-foreground">Tap the chip to confirm — we won't count it twice.</p>
					</div>
				</Tooltip.Content>
			</Tooltip.Root>
		</Tooltip.Provider>
	{:else}
		<span class="text-xs text-muted-foreground">—</span>
	{/if}
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
				onclick={(ev) => { ev.stopPropagation(); openMarkPaid(t); }}
			>Mark paid</Button>
		{/if}
		{#if t.kind !== 'Bank'}
			<Button
				data-testid="transaction-edit"
				aria-label="Open transaction"
				variant="outline"
				size="icon"
				onclick={(ev) => { ev.stopPropagation(); goto(ledgerHref(t)); }}
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
	<title>Money - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="accounting-page">
	<PageHeader
		class="mb-5"
		band
		art={1}
		tone="mint"
		eyebrow="Money"
		title="Money"
		description="Rent ledger, receivables, expenses, and owner-facing books."
		data-testid="accounting-header"
	/>

	<!-- Compact KPI strip — always visible across tabs so the headline numbers are one glance away. -->
	<!-- These KPIs come from /accounting/summary (no date range), so they're all-time, while the
	     ledger below honors the From/To filter. Label them so the two aren't read as one period. -->
	<div class="mb-1.5 flex flex-wrap items-baseline gap-x-2 gap-y-0.5" data-testid="accounting-kpi-period-label">
		<span class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">All-time</span>
		<span class="text-xs text-muted-foreground">These four totals cover all dates — the date filter on the ledger below doesn't change them.</span>
	</div>
	<div class="mb-5 grid gap-3 sm:grid-cols-2 md:grid-cols-4">
		<Card.Root class="m3-tonal-card m3-tonal-card--mint gap-0 py-0" data-testid="accounting-collected">
			<Card.Content class="p-4">
				<p class="text-xs font-medium text-muted-foreground" title="Rent and fees received (security deposits are tracked separately under Deposits).">Total Collected</p>
				{#if accountingSummaryQuery.isLoading}
					<div class="mt-1 h-8 w-24 animate-pulse rounded bg-muted"></div>
				{:else}
					<p class="font-mono text-2xl font-bold tabular-nums text-success">{money(summary?.payments.collected || 0)}</p>
				{/if}
			</Card.Content>
		</Card.Root>
		<Card.Root class="m3-tonal-card m3-tonal-card--sky gap-0 py-0" data-testid="accounting-outstanding">
			<Card.Content class="p-4">
				<p class="text-xs font-medium text-muted-foreground">Outstanding</p>
				{#if accountingSummaryQuery.isLoading}
					<div class="mt-1 h-8 w-24 animate-pulse rounded bg-muted"></div>
				{:else}
					<p class="font-mono text-2xl font-bold tabular-nums">{money(summary?.payments.outstanding || 0)}</p>
				{/if}
			</Card.Content>
		</Card.Root>
		<Card.Root class="m3-tonal-card m3-tonal-card--rose gap-0 py-0" data-testid="accounting-overdue">
			<Card.Content class="p-4">
				<p class="text-xs font-medium text-muted-foreground">Overdue</p>
				{#if accountingSummaryQuery.isLoading}
					<div class="mt-1 h-8 w-24 animate-pulse rounded bg-muted"></div>
				{:else}
					<p class="font-mono text-2xl font-bold tabular-nums {(summary?.payments.overdue || 0) > 0 ? 'text-destructive' : ''}">{money(summary?.payments.overdue || 0)}</p>
				{/if}
			</Card.Content>
		</Card.Root>
		<Card.Root class="m3-tonal-card m3-tonal-card--amber gap-0 py-0" data-testid="accounting-expenses">
			<Card.Content class="p-4">
				<p class="text-xs font-medium text-muted-foreground">Expenses</p>
				{#if accountingSummaryQuery.isLoading}
					<div class="mt-1 h-8 w-24 animate-pulse rounded bg-muted"></div>
				{:else}
					<p class="font-mono text-2xl font-bold tabular-nums text-[var(--warning)]">{money(summary?.totalExpenses || 0)}</p>
				{/if}
			</Card.Content>
		</Card.Root>
	</div>

	<Tabs.Root bind:value={activeTab} class="w-full">
		<Tabs.List class="mb-5" data-testid="accounting-tabs">
			{#each accountingTabs as t}
				<Tabs.Trigger value={t.value} data-testid="accounting-tab-{t.value}">{t.label}</Tabs.Trigger>
			{/each}
		</Tabs.List>

		<!-- ───────────────────────── REPORTS ───────────────────────── -->
		<Tabs.Content value="reports">
		<div data-testid="accounting-reports">
		<div class="mb-3 flex items-center justify-between">
			<div>
				<h2 class="text-lg font-semibold">Reports</h2>
				<p class="text-sm text-muted-foreground">Ledger, property profit and loss, Schedule E totals, and 1099 review.</p>
			</div>
			<div class="flex flex-wrap items-center justify-end gap-2">
				<Button variant="outline" size="sm" href="/accounting/year-end" data-testid="accounting-report-year-end-link">
					<FileBarChart class="h-4 w-4" />
					Year-end view
				</Button>
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
			<Card.Root class="m3-tonal-card m3-tonal-card--mint gap-0 py-0">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Net cash flow</p>
					<p class="font-mono text-2xl font-bold tabular-nums {(reports?.netCashFlow || 0) < 0 ? 'text-destructive' : (reports?.netCashFlow || 0) > 0 ? 'text-success' : ''}">{money(reports?.netCashFlow || 0)}</p>
					<p class="mt-1 text-xs text-muted-foreground"><span class="text-success">{money(reports?.totalIncome || 0)}</span> income / <span class="text-[var(--warning)]">{money(reports?.totalExpenses || 0)}</span> expenses</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="m3-tonal-card m3-tonal-card--violet gap-0 py-0">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Ledger rows</p>
					<p class="text-2xl font-bold font-mono tabular-nums">{reports?.ledger.length ?? 0}</p>
					<p class="mt-1 text-xs text-muted-foreground">Recent payments and expenses</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="m3-tonal-card m3-tonal-card--sky gap-0 py-0">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Properties</p>
					<p class="text-2xl font-bold font-mono tabular-nums">{reports?.properties.length ?? 0}</p>
					<p class="mt-1 text-xs text-muted-foreground">Property-level P&amp;L</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="m3-tonal-card m3-tonal-card--amber gap-0 py-0">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">1099 review</p>
					<p class="font-mono text-2xl font-bold tabular-nums {vendorReviewCount > 0 ? 'text-warning' : ''}">{vendorReviewCount}</p>
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
					<Tooltip.Provider delayDuration={150}>
						<div class="grid gap-2 md:grid-cols-2 xl:grid-cols-4">
						{#each recentLedger as row}
							<a href={row.sourceHref} class="flex items-center justify-between gap-3 rounded-md px-2 py-1.5 hover:bg-muted/60">
								<span class="min-w-0">
									<span class="flex items-center gap-1.5">
										<span class="truncate text-sm">{row.description}</span>
										{#if row.explanation}
											<HelpTooltip text={row.explanation} label="Why this is here" />
										{/if}
									</span>
									<span class="block text-xs text-muted-foreground">{row.type} · {row.counterparty ?? row.propertyName ?? 'General'}</span>
								</span>
								<span class="shrink-0 font-mono text-sm tabular-nums">{money(row.amount)}</span>
							</a>
						{:else}
							<p class="text-sm text-muted-foreground">No ledger activity yet.</p>
						{/each}
						</div>
					</Tooltip.Provider>
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
							<a href="/vendors/{vendor.vendorId}" class="flex items-center justify-between gap-3 rounded-md px-2 py-1.5 hover:bg-muted/60">
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

		</Tabs.Content>

		<!-- ───────────────────────── LEDGER ───────────────────────── -->
		<Tabs.Content value="ledger">
	<div>
		<div class="mb-3 flex flex-wrap items-center justify-between gap-3">
			<div>
				<div class="flex items-center gap-1.5">
					<h2 class="text-lg font-semibold">Transactions</h2>
					<HelpPopover
						title="Bank reconciliation"
						summary="The Bank column confirms whether each recorded payment or expense actually cleared your bank."
						detail="A green “Cleared” badge means it matched a bank line; a blue “Match?” chip is a suggested match — tap it to confirm. We never count anything twice."
						learnMoreUrl={undefined}
					/>
				</div>
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
			onRowClick={(t) => goto(ledgerHref(t))}
			data-testid="transactions-list"
			pageSize={PAGE_SIZE}
			page={transactionPage}
			totalCount={transactionTotalCount}
			serverSide
			onPageChange={(page) => (transactionPage = page)}
			sort={transactionSort}
			onSortChange={(sort) => { transactionSort = sort ?? '-createdAt'; transactionPage = 1; }}
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
							{transactionCategoryFilter ? formatExpenseCategory(transactionCategoryFilter) : 'All categories'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="All categories">All categories</Select.Item>
							{#each (transactionKindFilter === 'Payment' ? PAYMENT_TYPES.map((value) => ({ value, label: value })) : transactionKindFilter === 'Expense' ? EXPENSE_CATEGORY_OPTIONS : transactionKindFilter === 'Bank' ? ['Deposit', 'Withdrawal'].map((value) => ({ value, label: value })) : [...PAYMENT_TYPES.map((value) => ({ value, label: value })), ...EXPENSE_CATEGORY_OPTIONS, ...['Deposit', 'Withdrawal'].map((value) => ({ value, label: value }))]) as option}
								<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
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
					<DatePicker testid="transaction-from-filter" bind:value={transactionFromFilter} placeholder="From date" max={transactionToFilter || undefined} />
					<DatePicker testid="transaction-to-filter" bind:value={transactionToFilter} placeholder="To date" min={transactionFromFilter || undefined} />
				</div>
			{/snippet}
		</DataGrid>
	</div>
		</Tabs.Content>

		<!-- ───────────────────────── OVERVIEW ───────────────────────── -->
		<Tabs.Content value="overview">
			<Card.Root class="m3-expressive-card m3-expressive-card--info gap-0 py-0" data-testid="accounting-money-snapshot">
				<Card.Header class="px-5 pb-2 pt-5">
					<div class="flex items-center gap-1.5">
						<Card.Title class="text-base">Money Snapshot</Card.Title>
						<HelpPopover
							title="Money Snapshot"
							summary="A plain-English summary of your money for this portfolio — what came in, what went out, and where you stand."
							detail="No accounting jargon: it reads the same numbers as the ledger and explains them in everyday terms."
							learnMoreUrl={undefined}
						/>
					</div>
					<Card.Description>Plain-English accounting summary for this portfolio.</Card.Description>
				</Card.Header>
				<Card.Content class="px-5 pb-5 pt-0">
					{#if accountingSummaryQuery.isLoading}
						<div class="space-y-2">
							<div class="h-5 w-64 animate-pulse rounded bg-muted"></div>
							<div class="h-4 w-full max-w-2xl animate-pulse rounded bg-muted"></div>
						</div>
					{:else if summary?.snapshot}
						<p class="text-base font-semibold">{summary.snapshot.title}</p>
						<p class="mt-1 text-sm text-muted-foreground">{summary.snapshot.summary}</p>
						<div class="mt-3 grid gap-2 md:grid-cols-2">
							{#each summary.snapshot.bullets as bullet}
								<div class="flex items-start gap-2 rounded-md border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground">
									<span class="mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full bg-primary"></span>
									<span>{bullet}</span>
								</div>
							{/each}
						</div>
					{:else}
						<p class="text-sm text-muted-foreground">No money snapshot is available yet.</p>
					{/if}
				</Card.Content>
			</Card.Root>
		</Tabs.Content>
	</Tabs.Root>
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
				<label for="payment-lease-input" class="mb-1 block text-xs font-medium text-muted-foreground">Property / unit</label>
				<Select.Root type="single" bind:value={paymentForm.leaseId}>
					<Select.Trigger id="payment-lease-input" class="w-full" data-testid="payment-lease-input">
						{selectedLeaseLabel ?? 'Property / unit'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="Property / unit">Property / unit</Select.Item>
						{#each leasesQuery.data || [] as lease}
							<Select.Item value={String(lease.id)} label={formatLeasePickerLabel(lease)}>{formatLeasePickerLabel(lease)}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				{#if paymentErrors.leaseId}<p class="mt-1 text-xs text-destructive" data-testid="payment-lease-error">{paymentErrors.leaseId}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<Input data-testid="payment-amount-input" bind:value={paymentForm.amount} placeholder="Amount" type="text" inputmode="decimal" mask="currency" />
					{#if paymentErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="payment-amount-error">{paymentErrors.amount}</p>{/if}
				</div>
				<div>
					<DatePicker testid="payment-due-date-input" bind:value={paymentForm.dueDate} placeholder="Due date" />
					{#if paymentErrors.dueDate}<p class="mt-1 text-xs text-destructive" data-testid="payment-due-date-error">{paymentErrors.dueDate}</p>{/if}
				</div>
			</div>
			<div class="grid grid-cols-2 gap-2">
				<Select.Root type="single" bind:value={paymentForm.paymentType}>
					<Select.Trigger class="w-full" data-testid="payment-type-input">
						{paymentForm.paymentType ? paymentTypeLabel(paymentForm.paymentType) : 'Select type'}
					</Select.Trigger>
					<Select.Content>
						{#each PAYMENT_TYPES as t}
							<Select.Item value={t} label={paymentTypeLabel(t)}>{paymentTypeLabel(t)}</Select.Item>
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
			<!-- Partial payments collect only some of the amount up front. The "Amount paid" split is
			     shown (and required) only for status = Partial; the remainder (amount − paid) stays owed.
			     Hidden for every other status, where it has no meaning. -->
			{#if paymentForm.status === 'Partial'}
				<div>
					<label for="payment-amount-paid-input" class="mb-1 block text-xs font-medium text-muted-foreground">Amount paid (so far)</label>
					<Input id="payment-amount-paid-input" data-testid="payment-amount-paid-input" bind:value={paymentForm.amountPaid} placeholder="Amount paid" type="text" inputmode="decimal" mask="currency" />
					{#if paymentErrors.amountPaid}
						<p class="mt-1 text-xs text-destructive" data-testid="payment-amount-paid-error">{paymentErrors.amountPaid}</p>
					{:else}
						<p class="mt-1 text-xs text-muted-foreground">How much was collected. The rest stays owed.</p>
					{/if}
				</div>
			{/if}
		</div>
		<Dialog.Footer>
			<Button data-testid="payment-form-cancel" variant="outline" onclick={closePaymentForm}>Cancel</Button>
			<Button data-testid="payment-form-save" onclick={submitPayment} disabled={savePaymentMutation.isPending}>{savePaymentMutation.isPending ? 'Saving…' : 'Save payment'}</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Mark Paid: capture how/when the money arrived. Smart defaults (today + last-used method) keep
	 it one quick confirm; the payment record carries its own amount so there's no amount field. -->
<Dialog.Root
	open={showMarkPaidForm}
	onOpenChange={(v) => { if (!v) closeMarkPaid(); }}
>
	<Dialog.Content class="max-w-md" data-testid="mark-paid-dialog">
		<Dialog.Header>
			<Dialog.Title>Mark paid</Dialog.Title>
			{#if markPaidTarget}
				<Dialog.Description data-testid="mark-paid-summary">
					{markPaidTarget.description} · {money(markPaidTarget.amount)}
				</Dialog.Description>
			{/if}
		</Dialog.Header>
		<div class="space-y-3" data-testid="mark-paid-form">
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Date received</span>
				<DatePicker testid="mark-paid-date-input" bind:value={markPaidForm.paidDate} placeholder="Date received" />
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Method</span>
				<Select.Root type="single" bind:value={markPaidForm.method}>
					<Select.Trigger class="w-full" data-testid="mark-paid-method-input">
						{markPaidForm.method || 'Select method'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="No method">No method</Select.Item>
						{#each PAYMENT_METHODS as m}
							<Select.Item value={m} label={m}>{m}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Reference</span>
				<Input
					data-testid="mark-paid-reference-input"
					bind:value={markPaidForm.externalReference}
					placeholder="Check #, confirmation #, etc. (optional)"
				/>
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Notes</span>
				<textarea
					data-testid="mark-paid-notes-input"
					bind:value={markPaidForm.notes}
					rows={2}
					maxlength={2000}
					placeholder="Anything to remember about this payment (optional)"
					class="w-full resize-none rounded-md border border-border bg-background px-3 py-2 text-sm shadow-xs outline-none placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
				></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<Button data-testid="mark-paid-cancel" variant="outline" onclick={closeMarkPaid}>Cancel</Button>
			<Button data-testid="mark-paid-confirm" onclick={submitMarkPaid} disabled={markPaidMutation.isPending}>
				{markPaidMutation.isPending ? 'Saving…' : 'Mark paid'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root
	open={showExpenseForm}
	onOpenChange={(v) => { if (!v) closeExpenseForm(); }}
>
	<Dialog.Content class="max-h-[85vh] max-w-3xl overflow-y-auto">
		<Dialog.Header>
			<Dialog.Title>{editingExpenseId == null ? 'New Expense' : 'Edit Expense'}</Dialog.Title>
		</Dialog.Header>
		<FormStepper
			steps={expenseSteps}
			bind:currentStep={expenseStep}
			completedSteps={completedExpenseSteps}
			testid="expense-stepper"
		>
				<div class="space-y-4" data-testid="expense-form">
					{#if expenseStep === 0}
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Work order</span>
							<Select.Root
								type="single"
								value={expenseForm.workOrderId}
								onValueChange={(value) => {
									expenseForm.workOrderId = value;
								}}
							>
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
						<div class="grid gap-3 sm:grid-cols-3">
							<div>
								<span class="mb-1 block text-xs text-muted-foreground">Property</span>
								<Select.Root
									type="single"
									value={expenseForm.propertyId}
									onValueChange={(value) => {
										expenseForm.propertyId = value;
										expenseForm.unitId = '';
									}}
								>
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
								<span class="mb-1 block text-xs text-muted-foreground">Unit</span>
								<Select.Root type="single" bind:value={expenseForm.unitId} disabled={!expenseForm.propertyId}>
									<Select.Trigger class="w-full" data-testid="expense-unit-input">
										{#if !expenseForm.propertyId}
											Select property first
										{:else}
											{selectedUnitLabel ?? 'No unit'}
										{/if}
									</Select.Trigger>
									<Select.Content>
										<Select.Item value="" label="No unit">No unit</Select.Item>
										{#each expenseUnitsQuery.data || [] as unit}
											<Select.Item value={String(unit.id)} label={unit.unitNumber}>{unit.unitNumber}</Select.Item>
										{/each}
									</Select.Content>
								</Select.Root>
							</div>
							<div>
								<span class="mb-1 block text-xs text-muted-foreground">Vendor</span>
								<Select.Root
									type="single"
									value={expenseForm.vendorId}
									onValueChange={(value) => {
										expenseForm.vendorId = value;
									}}
								>
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
					{:else if expenseStep === 1}
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Description</span>
							<Input data-testid="expense-description-input" bind:value={expenseForm.description} placeholder="Description" />
							{#if expenseErrors.description}<p class="mt-1 text-xs text-destructive" data-testid="expense-description-error">{expenseErrors.description}</p>{/if}
						</div>
						<div class="grid gap-3 sm:grid-cols-3">
							<div>
								<span class="mb-1 block text-xs text-muted-foreground">Amount</span>
								<Input data-testid="expense-amount-input" bind:value={expenseForm.amount} placeholder="0.00" type="text" inputmode="decimal" mask="currency" />
								{#if expenseErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="expense-amount-error">{expenseErrors.amount}</p>{/if}
							</div>
							<div>
								<span class="mb-1 block text-xs text-muted-foreground">Subtotal</span>
								<Input data-testid="expense-subtotal-input" bind:value={expenseForm.subtotal} placeholder="0.00" type="text" inputmode="decimal" mask="currency" />
								{#if expenseErrors.subtotal}<p class="mt-1 text-xs text-destructive">{expenseErrors.subtotal}</p>{/if}
							</div>
							<div>
								<span class="mb-1 block text-xs text-muted-foreground">Tax</span>
								<Input data-testid="expense-tax-input" bind:value={expenseForm.taxAmount} placeholder="0.00" type="text" inputmode="decimal" mask="currency" />
								{#if expenseErrors.taxAmount}<p class="mt-1 text-xs text-destructive">{expenseErrors.taxAmount}</p>{/if}
							</div>
						</div>
					{:else if expenseStep === 2}
						<div class="grid gap-3 sm:grid-cols-3">
							<div>
								<span class="mb-1 block text-xs text-muted-foreground">Incurred</span>
								<DatePicker testid="expense-incurred-input" bind:value={expenseForm.incurredAt} placeholder="Incurred date" />
								{#if expenseErrors.incurredAt}<p class="mt-1 text-xs text-destructive" data-testid="expense-incurred-error">{expenseErrors.incurredAt}</p>{/if}
							</div>
							<div>
								<span class="mb-1 block text-xs text-muted-foreground">Due date</span>
								<DatePicker testid="expense-due-input" bind:value={expenseForm.dueDate} placeholder="Due date" />
							</div>
							<div>
								<span class="mb-1 block text-xs text-muted-foreground">Paid date</span>
								<DatePicker testid="expense-paid-input" bind:value={expenseForm.paidAt} placeholder="Paid date" />
							</div>
						</div>
					{:else if expenseStep === 3}
						<div class="grid gap-3 sm:grid-cols-2">
							<div>
								<span class="mb-1 block text-xs text-muted-foreground">Category</span>
								<Select.Root type="single" bind:value={expenseForm.category}>
									<Select.Trigger class="w-full" data-testid="expense-category-input">
										{expenseForm.category ? formatExpenseCategory(expenseForm.category) : 'Select category'}
									</Select.Trigger>
									<Select.Content>
										{#each EXPENSE_CATEGORY_OPTIONS as option}
											<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
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
						<label class="flex items-center gap-2 text-sm">
							<Checkbox bind:checked={expenseForm.billableToOwner} data-testid="expense-billable-input" />
							Billable to owner
					</label>
					<div>
							<span class="mb-1 block text-xs text-muted-foreground">Notes</span>
							<textarea data-testid="expense-notes-input" bind:value={expenseForm.notes} rows="3" class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"></textarea>
						</div>
					{:else if expenseStep === 4}
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Vendor address</span>
							<Input data-testid="expense-vendor-address-input" bind:value={expenseForm.vendorAddress} />
						</div>
						<div class="grid gap-3 sm:grid-cols-3">
							<div>
								<span class="mb-1 block text-xs text-muted-foreground">Vendor phone</span>
								<Input data-testid="expense-vendor-phone-input" bind:value={expenseForm.vendorPhone} type="tel" autocomplete="tel" inputmode="tel" mask="phone" />
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Vendor website</span>
							<Input data-testid="expense-vendor-website-input" bind:value={expenseForm.vendorWebsite} type="url" autocomplete="url" />
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Vendor tax ID</span>
								<Input data-testid="expense-vendor-taxid-input" bind:value={expenseForm.vendorTaxId} />
							</div>
						</div>
					{:else if expenseStep === 5}
						<div class="grid gap-3 sm:grid-cols-3">
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
								<Input data-testid="expense-card-last4-input" bind:value={expenseForm.cardLast4} inputmode="numeric" maxlength={4} mask="cardLast4" />
							</div>
						</div>
					{:else}
						<div class="grid gap-3 sm:grid-cols-4">
							<div>
								<span class="mb-1 block text-xs text-muted-foreground">Tax rate</span>
								<Input data-testid="expense-tax-rate-input" bind:value={expenseForm.taxRate} inputmode="decimal" mask="percentage" />
							{#if expenseErrors.taxRate}<p class="mt-1 text-xs text-destructive">{expenseErrors.taxRate}</p>{/if}
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Tip</span>
							<Input data-testid="expense-tip-input" bind:value={expenseForm.tip} inputmode="decimal" mask="currency" />
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Discount</span>
							<Input data-testid="expense-discount-input" bind:value={expenseForm.discount} inputmode="decimal" mask="currency" />
						</div>
						<div>
							<span class="mb-1 block text-xs text-muted-foreground">Shipping</span>
							<Input data-testid="expense-shipping-input" bind:value={expenseForm.shipping} inputmode="decimal" mask="currency" />
						</div>
					</div>
				{/if}
			</div>
		</FormStepper>
		<Dialog.Footer>
			<Button data-testid="expense-form-cancel" variant="outline" onclick={closeExpenseForm}>Cancel</Button>
			{#if expenseStep > 0}
				<Button data-testid="expense-step-back" variant="outline" onclick={() => (expenseStep = Math.max(expenseStep - 1, 0))}>Back</Button>
				{/if}
				{#if expenseStep < expenseSteps.length - 1}
					<StepperNextButton
						testid="expense-step-next"
						onclick={nextExpenseStep}
						complete={completedExpenseSteps.includes(expenseStep)}
					/>
				{:else}
				<Button data-testid="expense-form-save" onclick={submitExpense} disabled={saveExpenseMutation.isPending}>{saveExpenseMutation.isPending ? 'Saving…' : 'Save expense'}</Button>
			{/if}
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
	message={paymentDeleteTarget ? `Delete this ${money(paymentDeleteTarget.amount)} ${paymentTypeLabel(paymentDeleteTarget.paymentType).toLowerCase()} charge?` : ''}
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
