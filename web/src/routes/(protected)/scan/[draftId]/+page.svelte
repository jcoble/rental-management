<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { toast } from 'svelte-sonner';
	import { scan, type ScanFieldDto } from '$lib/api/scan';
	import { recordHref } from '$lib/navigation/record-href';
	import {
		tenantAccounts,
		type TenantAccountListItem
	} from '$lib/api/endpoints/tenant-accounts';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { units } from '$lib/api/endpoints/units';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import {
		loans,
		type Loan,
		type LoanPayment
	} from '$lib/api/endpoints/loans';
	import type { WorkOrder } from '$lib/types';
	import { invalidateQueriesAfterScanConfirm } from '$lib/scan/scan-confirm-invalidation';
	import {
		activeScanDateFieldNames,
		GENERIC_SCAN_DATE_FIELDS,
		hasVisibleScanDateInvalid
	} from '$lib/scan/scan-date-guard';
	import { LEASE_REVIEW_NEW_UNIT_DETAIL_FIELDS, seedLeaseUnitId, shouldSeedLeaseReviewState } from '$lib/scan/lease-review-state';
	import {
		buildScanReviewInitialEditedFields,
		buildScanReviewFieldGroups,
		fieldDisplayLabel,
		resolveScanReviewPropertyId,
		scanCategoryLabel,
		scanCategoryOptionsForTarget,
		scanCategoryValue
	} from '$lib/scan/scan-review-fields';
	import {
		createdRecordArticle,
		createdRecordHref,
		createdRecordLabel,
		isTerminalScanReview,
		shouldDisableScanReviewControls
	} from '$lib/scan/scan-review-state';
	import {
		applyScanContextOverrides,
		parseScanContext
	} from '$lib/scan/scan-context';
	import { scanProcessingCopy } from '$lib/scan/scan-copy';
	import { hasAllPropertiesRentalsManageAuthority } from '$lib/auth/property-authority';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Badge } from '$lib/components/ui/badge';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Table from '$lib/components/ui/table';
	import * as Select from '$lib/components/ui/select';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import LeaseScanSignatureChoice, {
		type LeaseScanReviewDisposition
	} from '$lib/components/scan/LeaseScanSignatureChoice.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import { AlertTriangle, CheckCircle2, X } from '@lucide/svelte';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';

	const LINE_ITEMS_FIELD = 'line_items';

	interface LineItem {
		description?: string | null;
		quantity?: number | null;
		unit_price?: number | null;
		amount?: number | null;
	}

	// Editable mirror of a line item. Numeric cells are kept as STRINGS (like every other
	// editable field on this page) and parsed at confirm time — this matches the scalar-field
	// flow, gives phones a numeric keypad via inputmode, and keeps a bad keystroke from ever
	// throwing. `key` is a stable client id so add/remove don't mis-bind the {#each} rows.
	interface EditableLineItem {
		key: number;
		description: string;
		quantity: string;
		unit_price: string;
		amount: string;
	}

	const queryClient = useQueryClient();
	const authState = getAuthState();
	const currentAccess = $derived(page.data.access ?? authState.accessEnvelope ?? null);
	const canCreateLeaseProperty = $derived(hasAllPropertiesRentalsManageAuthority(currentAccess));

	const draftId = $derived(parseInt(page.params.draftId ?? '0', 10));
	const launchContext = $derived(parseScanContext(page.url.searchParams));

	const draftQuery = createQuery(() => ({
		queryKey: ['scan', draftId],
		queryFn: () => scan.get(draftId),
		// Bounded poll: keep polling while the worker is still reading the document.
		// The worker flips the draft Pending → Processing → Reviewing, so we must
		// poll through BOTH Pending and Processing to catch the final Reviewing
		// state (SignalR also pushes the flip; the poll is a fallback).
		refetchInterval: (q) => {
			const s = q.state.data?.status;
			return s === 'Pending' || s === 'Processing' ? 1500 : false;
		},
		refetchIntervalInBackground: true,
		refetchOnReconnect: true
	}));

	const data = $derived(draftQuery.data);
	const scanContext = $derived({
		...launchContext,
		propertyId: data?.captureContext?.propertyId ?? launchContext.propertyId,
		unitId: data?.captureContext?.unitId ?? launchContext.unitId,
		leaseManagementId: data?.captureContext?.leaseManagementId ?? launchContext.leaseManagementId,
		leaseAgreementId: data?.captureContext?.leaseAgreementId ?? launchContext.leaseAgreementId,
		tenantAccountId: data?.captureContext?.tenantAccountId ?? launchContext.tenantAccountId,
		tenantLedgerEntryId: data?.captureContext?.tenantLedgerEntryId ?? launchContext.tenantLedgerEntryId,
		workOrderId: data?.captureContext?.workOrderId ?? launchContext.workOrderId,
		applicationId: data?.captureContext?.applicationId ?? launchContext.applicationId,
		rentalListingId: data?.captureContext?.rentalListingId ?? launchContext.rentalListingId,
		sourceLabel: data?.captureContext?.sourceLabel ?? launchContext.sourceLabel
	});
	const inheritedContextItems = $derived([
		scanContext.propertyId ? `Property #${scanContext.propertyId}` : null,
		scanContext.unitId ? `Unit #${scanContext.unitId}` : null,
		scanContext.leaseManagementId ? `Rental relationship #${scanContext.leaseManagementId}` : null,
		scanContext.leaseAgreementId ? `Agreement #${scanContext.leaseAgreementId}` : null,
		scanContext.tenantAccountId ? `Rental account #${scanContext.tenantAccountId}` : null,
		scanContext.tenantLedgerEntryId ? `Ledger entry #${scanContext.tenantLedgerEntryId}` : null,
		scanContext.workOrderId ? `Work order #${scanContext.workOrderId}` : null,
		scanContext.applicationId ? `Application #${scanContext.applicationId}` : null,
		scanContext.rentalListingId ? `Listing #${scanContext.rentalListingId}` : null
	].filter((item): item is string => item !== null));
	const processingCopy = $derived(scanProcessingCopy(data?.targetEntityType ?? scanContext.type));

	// Whether this draft targets a Payment (rent check) rather than an Expense
	const isPayment = $derived(data?.targetEntityType === 'Payment');
	const isWorkOrder = $derived(data?.targetEntityType === 'WorkOrder');
	const isLease = $derived(data?.targetEntityType === 'LeaseAgreement');
	// A scanned completed paper rental application → creates an applicant / RentalApplication.
	const isApplication = $derived(data?.targetEntityType === 'Application');
	// A scanned mortgage statement / closing disclosure → creates a Loan on the property.
	const isLoan = $derived(data?.targetEntityType === 'Loan');
	// Line items are an Expense concept — only the expense confirm path persists them — so the
	// editable line-items table and its override are scoped to expense drafts.
	const isExpense = $derived(!isPayment && !isWorkOrder && !isLease && !isApplication && !isLoan);

	// The worker is still reading the document while Pending or Processing.
	const isProcessing = $derived(data?.status === 'Pending' || data?.status === 'Processing');

	// Canonical tenant-account selector state (only used when isPayment).
	// String-backed for the shadcn Select; converted to a number at confirm time.
	let selectedTenantAccountId = $state<string>('');
	let selectedTenantAccountLabelText = $state<string>('');
	let tenantAccountSearch = $state<string>('');
	let tenantAccountSkip = $state(0);
	const TENANT_ACCOUNT_PAGE_SIZE = 25;
	type TenantAccountChoice = Pick<
		TenantAccountListItem,
		'tenantAccountId' | 'propertyName' | 'unitId' | 'unitNumber' | 'relationshipNumber'
	> & { primaryTenantName?: string | null };

	// Optional property selector for Expense drafts. Sends `propertyId` override.
	// 'none' is the sentinel for "no property" (empty string conflicts with the
	// Select's "nothing selected" state in bits-ui).
	const NO_PROPERTY = 'none';
	const NO_UNIT = 'none';
	const NO_WORK_ORDER = 'none';
	let selectedPropertyId = $state<string>(NO_PROPERTY);
	let selectedExpenseUnitId = $state<string>(NO_UNIT);
	let selectedExpenseWorkOrderId = $state<string>(NO_WORK_ORDER);
	let selectedExpenseWorkOrderLabelText = $state('');
	let propertySearch = $state('');
	let propertySkip = $state(0);
	let tenantSearch = $state('');
	let tenantSkip = $state(0);
	let unitSearch = $state('');
	let unitSkip = $state(0);
	let workOrderSearch = $state('');
	let workOrderSkip = $state(0);
	const CONTEXT_PAGE_SIZE = 20;
	type LoanReviewMode = 'match' | 'create';
	let loanReviewMode = $state<LoanReviewMode>('match');
	let selectedExistingLoanId = $state('');
	let selectedExistingLoanPaymentId = $state('');

	$effect(() => {
		if (!draftId || !isProcessing) return;
		const refresh = setInterval(() => {
			if (!draftQuery.isFetching) void draftQuery.refetch();
		}, 1500);
		return () => clearInterval(refresh);
	});

	const tenantAccountOptionsQuery = createQuery(() => ({
		queryKey: ['tenant-account-options', getCurrentPortfolioId(), tenantAccountSearch.trim(), tenantAccountSkip],
		queryFn: () => tenantAccounts.listPage({
			skip: tenantAccountSkip,
			take: TENANT_ACCOUNT_PAGE_SIZE,
			search: tenantAccountSearch.trim() || undefined
		}),
		enabled: isPayment
	}));
	const contextualTenantAccountId = $derived(
		data?.captureContext?.tenantAccountId ?? scanContext.tenantAccountId ?? null
	);
	const contextualAccountIsInPage = $derived(
		!!contextualTenantAccountId &&
		(tenantAccountOptionsQuery.data?.items.some(
			(account) => account.tenantAccountId === contextualTenantAccountId
		) ?? false)
	);
	const contextualTenantAccountQuery = createQuery(() => ({
		queryKey: ['tenant-account', getCurrentPortfolioId(), contextualTenantAccountId],
		queryFn: () => tenantAccounts.get(contextualTenantAccountId as number),
		enabled:
			isPayment &&
			!!contextualTenantAccountId &&
			!!tenantAccountOptionsQuery.data &&
			!contextualAccountIsInPage,
		retry: false
	}));
	const tenantAccountChoices = $derived.by<TenantAccountChoice[]>(() => {
		const choices: TenantAccountChoice[] = [...(tenantAccountOptionsQuery.data?.items ?? [])];
		const contextualAccount = contextualTenantAccountQuery.data;
		if (
			contextualAccount &&
			!choices.some((account) => account.tenantAccountId === contextualAccount.tenantAccountId)
		) {
			choices.unshift(contextualAccount);
		}
		return choices;
	});

	const propertiesQuery = createQuery(() => ({
		queryKey: [
			'properties',
			getCurrentPortfolioId(),
			'scan-context',
			propertySearch.trim(),
			propertySkip
		],
		queryFn: () =>
			properties.listPage(getCurrentPortfolioId(), {
				search: propertySearch.trim() || undefined,
				sort: 'name',
				skip: propertySkip,
				take: CONTEXT_PAGE_SIZE
			}),
		enabled: !isPayment
	}));
	const propertyChoices = $derived(propertiesQuery.data?.items ?? []);
	const selectedExpensePropertyId = $derived(
		selectedPropertyId !== NO_PROPERTY ? Number(selectedPropertyId) : null
	);
	const selectedExpenseUnitIdNumber = $derived(
		selectedExpenseUnitId !== NO_UNIT ? Number(selectedExpenseUnitId) : null
	);
	const selectedExpenseWorkOrderIdNumber = $derived(
		selectedExpenseWorkOrderId !== NO_WORK_ORDER ? Number(selectedExpenseWorkOrderId) : null
	);
	const selectedLoanPropertyIdNumber = $derived(
		selectedPropertyId !== NO_PROPERTY ? Number(selectedPropertyId) : null
	);

	const existingLoansQuery = createQuery(() => ({
		queryKey: ['scan-loans', getCurrentPortfolioId(), selectedLoanPropertyIdNumber],
		queryFn: () => loans.listPage({
			propertyId: selectedLoanPropertyIdNumber as number,
			sort: 'lender',
			skip: 0,
			take: 50
		}),
		enabled: isLoan && loanReviewMode === 'match' && !!selectedLoanPropertyIdNumber
	}));
	const existingLoanChoices = $derived(existingLoansQuery.data?.items ?? []);
	const selectedExistingLoan = $derived.by<Loan | null>(() =>
		existingLoanChoices.find((loan) => String(loan.id) === selectedExistingLoanId) ?? null
	);
	const existingLoanPaymentsQuery = createQuery(() => ({
		queryKey: ['scan-loan-payments', getCurrentPortfolioId(), selectedExistingLoanId],
		queryFn: () => loans.payments(Number(selectedExistingLoanId), {
			sort: 'dueDate',
			skip: 0,
			take: 50
		}),
		enabled: isLoan && loanReviewMode === 'match' && !!selectedExistingLoanId
	}));
	const existingLoanPaymentChoices = $derived(existingLoanPaymentsQuery.data ?? []);
	const selectedExistingLoanPayment = $derived.by<LoanPayment | null>(() =>
		existingLoanPaymentChoices.find(
			(payment) => String(payment.id) === selectedExistingLoanPaymentId
		) ?? null
	);

	function loanPaymentLabel(payment: LoanPayment): string {
		const dueDate = new Date(payment.dueDate).toLocaleDateString();
		return `${payment.periodKey} · due ${dueDate} · ${formatUsd(payment.totalAmount)} · ${formatStatusLabel(payment.status)}`;
	}

	const expenseUnitsQuery = createQuery(() => ({
		queryKey: [
			'scan-expense-units',
			getCurrentPortfolioId(),
			selectedExpensePropertyId,
			unitSearch.trim(),
			unitSkip
		],
		queryFn: () =>
			units.listWithHealthPage({
				propertyId: selectedExpensePropertyId as number,
				search: unitSearch.trim() || undefined,
				sort: 'unitNumber',
				skip: unitSkip,
				take: CONTEXT_PAGE_SIZE
			}),
		enabled: !!data && isExpense && !!selectedExpensePropertyId
	}));
	const expenseUnitChoices = $derived(expenseUnitsQuery.data?.items ?? []);

	const expenseWorkOrdersQuery = createQuery(() => ({
		queryKey: [
			'scan-expense-work-orders',
			getCurrentPortfolioId(),
			selectedExpensePropertyId,
			selectedExpenseUnitIdNumber,
			workOrderSearch.trim(),
			workOrderSkip
		],
		queryFn: () =>
			workOrders.listPage(getCurrentPortfolioId(), {
				propertyId: selectedExpensePropertyId ?? undefined,
				unitId: selectedExpenseUnitIdNumber ?? undefined,
				search: workOrderSearch.trim() || undefined,
				sort: '-requestedAt',
				skip: workOrderSkip,
				take: CONTEXT_PAGE_SIZE
			}),
		enabled: !!data && isExpense
	}));
	const selectedExpenseWorkOrderIsInPage = $derived(
		!!selectedExpenseWorkOrderIdNumber &&
		(expenseWorkOrdersQuery.data?.items.some((workOrder) => workOrder.id === selectedExpenseWorkOrderIdNumber) ?? false)
	);
	const selectedExpenseWorkOrderQuery = createQuery(() => ({
		queryKey: ['scan-expense-work-order', getCurrentPortfolioId(), selectedExpenseWorkOrderIdNumber],
		queryFn: () => workOrders.get(selectedExpenseWorkOrderIdNumber as number),
		enabled: !!data && isExpense && !!selectedExpenseWorkOrderIdNumber && !selectedExpenseWorkOrderIsInPage,
		retry: false
	}));
	const expenseWorkOrderChoices = $derived.by<WorkOrder[]>(() => {
		const choices = [...(expenseWorkOrdersQuery.data?.items ?? [])];
		const selected = selectedExpenseWorkOrderQuery.data;
		if (selected && !choices.some((workOrder) => workOrder.id === selected.id)) {
			choices.unshift(selected);
		}
		return choices;
	});

	// --- Lease draft selectors ---
	// A Lease confirm needs a property + unit; tenant is optional (when omitted the server matches the
	// extracted tenant_name or creates a new tenant). The property can be an EXISTING in-portfolio record
	// OR created from the scanned document (the empty-portfolio bootstrap — a brand-new landlord with zero
	// properties must be able to proceed). String-backed for the shadcn Select; converted at confirm time.
	const NO_TENANT = 'none';
	// Sentinel for the property dropdown's "create from this document" option (distinct from a real id).
	const CREATE_PROPERTY = '__create__';
	let selectedLeasePropertyId = $state<string>('');
	let selectedLeaseUnitId = $state<string>('');
	let selectedTenantId = $state<string>(NO_TENANT);
	let previousLeasePropertyId = $state<string>('');
	// True when the property dropdown is on "create new" — the unit becomes a free-text new unit number
	// (you can't pick a unit of a property that doesn't exist yet) and editable address fields appear.
	const isCreatingLeaseProperty = $derived(selectedLeasePropertyId === CREATE_PROPERTY);
	// The lease-import proposal from the API (link-existing vs create-new for property + unit).
	const leaseProposal = $derived(data?.leaseProposal ?? null);
	// One-shot guard so seeding the property pick from the proposal doesn't clobber the user's choice.
	let leasePropertySeeded = $state(false);
	// Editable new-property fields (create mode), seeded from the extracted lease fields.
	let newPropertyName = $state('');
	let newPropertyAddress = $state('');
	let newPropertyCity = $state('');
	let newPropertyState = $state('');
	let newPropertyPostal = $state('');
	let newPropertyRentalStructure = $state<'SingleRental' | 'MultiRental' | ''>('');
	let newUnitNumber = $state('');
	let newPropertyFieldsSeeded = $state(false);
	let leaseReviewDisposition = $state<LeaseScanReviewDisposition | ''>('');
	let leaseDocumentTemplateId = $state('');
	let possessionGivenOn = $state('');
	let rentTrackingStartMode = $state<
		'ForwardOnly' | 'BackfillFromLeaseStart' | 'CustomCutoffDate'
	>('ForwardOnly');
	let rentTrackingStartOn = $state('');

	function resetLeaseReviewState() {
		selectedLeasePropertyId = '';
		selectedLeaseUnitId = '';
		selectedTenantId = NO_TENANT;
		previousLeasePropertyId = '';
		leasePropertySeeded = false;
		newPropertyName = '';
		newPropertyAddress = '';
		newPropertyCity = '';
		newPropertyState = '';
		newPropertyPostal = '';
		newPropertyRentalStructure = '';
		newUnitNumber = '';
		newPropertyFieldsSeeded = false;
		leaseReviewDisposition = '';
		leaseDocumentTemplateId = '';
		possessionGivenOn = '';
		rentTrackingStartMode = 'ForwardOnly';
		rentTrackingStartOn = '';
	}

	// Units scoped to the chosen EXISTING property (required pick). Skipped in create-new mode (the
	// CREATE_PROPERTY sentinel isn't a real id — the unit is created from a free-text number instead).
	const leaseUnitsQuery = createQuery(() => ({
		queryKey: [
			'units-for-lease-scan',
			selectedLeasePropertyId,
			unitSearch.trim(),
			unitSkip
		],
		queryFn: () =>
			units.listWithHealthPage({
				propertyId: Number(selectedLeasePropertyId),
				search: unitSearch.trim() || undefined,
				sort: 'unitNumber',
				skip: unitSkip,
				take: CONTEXT_PAGE_SIZE
			}),
		enabled: isLease && !!selectedLeasePropertyId && selectedLeasePropertyId !== CREATE_PROPERTY
	}));
	const leaseUnitChoices = $derived(leaseUnitsQuery.data?.items ?? []);

	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', getCurrentPortfolioId(), 'scan-context', tenantSearch.trim(), tenantSkip],
		queryFn: () =>
			tenants.listPage(getCurrentPortfolioId(), {
				search: tenantSearch.trim() || undefined,
				sort: 'lastName',
				skip: tenantSkip,
				take: CONTEXT_PAGE_SIZE
			}),
		enabled: isLease
	}));
	const tenantChoices = $derived(tenantsQuery.data?.items ?? []);

	// Extracted tenant name from the scan (drives the "create new tenant" default).
	const extractedTenantName = $derived((fieldValue('tenant_name') ?? '').trim());

	// Label for the "create a new property from this document" option, using the extracted address/name.
	const newPropertyLabel = $derived.by(() => {
		const extracted = (
			fieldValue('property_address') ||
			fieldValue('property_name') ||
			leaseProposal?.property.label ||
			''
		).trim();
		return extracted ? `Create "${extracted}" as a new property` : 'Create a new property from the lease';
	});

	const selectedLeasePropertyLabel = $derived.by(() => {
		if (selectedLeasePropertyId === CREATE_PROPERTY) return newPropertyLabel;
		if (!selectedLeasePropertyId) return '— Select a property —';
		const sel = propertyChoices.find((p) => String(p.id) === selectedLeasePropertyId);
		return sel ? sel.name : `Property #${selectedLeasePropertyId}`;
	});

	const selectedLeaseUnitLabel = $derived.by(() => {
		if (!selectedLeaseUnitId) return '— Select a unit —';
		const sel = leaseUnitChoices.find((u) => String(u.id) === selectedLeaseUnitId);
		return sel ? `Unit ${sel.unitNumber}` : `Unit #${selectedLeaseUnitId}`;
	});

	function tenantLabel(t: { fullName?: string | null; firstName: string; lastName: string }): string {
		return t.fullName || `${t.firstName} ${t.lastName}`.trim();
	}

	const newTenantLabel = $derived(
		extractedTenantName
			? `Create "${extractedTenantName}" as a new tenant`
			: 'Create a new tenant from the lease'
	);

	const selectedTenantLabel = $derived.by(() => {
		if (selectedTenantId === NO_TENANT) return newTenantLabel;
		const sel = tenantChoices.find((t) => String(t.id) === selectedTenantId);
		return sel ? tenantLabel(sel) : `Tenant #${selectedTenantId}`;
	});

	// Applicant fields shown as editable inputs for an Application draft (in display order). Single-entity
	// review, like the Payment/Expense pattern — NOT the 4-step guided lease flow.
	const APPLICATION_FIELDS: { name: string; label: string; type: 'text' | 'date' | 'number' }[] = [
		{ name: 'first_name', label: 'First name', type: 'text' },
		{ name: 'last_name', label: 'Last name', type: 'text' },
		{ name: 'email', label: 'Email', type: 'text' },
		{ name: 'phone', label: 'Phone', type: 'text' },
		{ name: 'date_of_birth', label: 'Date of birth', type: 'date' },
		{ name: 'current_address', label: 'Current address', type: 'text' },
		{ name: 'employer', label: 'Employer', type: 'text' },
		{ name: 'monthly_income', label: 'Monthly income', type: 'number' },
		{ name: 'applying_for', label: 'Applying for', type: 'text' },
		{ name: 'desired_move_in_date', label: 'Desired move-in', type: 'date' },
		{ name: 'id_last4', label: 'ID last 4', type: 'text' },
		{ name: 'co_signer_name', label: 'Co-signer', type: 'text' }
	];

	// Application requires a first + last name (both [Required] on the create). Mirrors leaseSelectionInvalid.
	const applicationInvalid = $derived.by(() => {
		if (!isApplication) return false;
		return !(editedFields['first_name'] ?? '').trim() || !(editedFields['last_name'] ?? '').trim();
	});

	// Loan fields shown as editable inputs for a Loan draft (in display order). Single-entity review,
	// like the Application pattern; the escrow-cover flags + notes render separately below.
	const LOAN_FIELDS: { name: string; label: string; type: 'text' | 'date' | 'number' }[] = [
		{ name: 'lender', label: 'Lender', type: 'text' },
		{ name: 'original_amount', label: 'Original amount', type: 'number' },
		{ name: 'current_balance', label: 'Current balance', type: 'number' },
		{ name: 'annual_interest_rate_pct', label: 'Interest rate %', type: 'number' },
		{ name: 'term_months', label: 'Term (months)', type: 'number' },
		{ name: 'start_date', label: 'Start date', type: 'date' },
		{ name: 'day_of_month_due', label: 'Day of month due', type: 'number' },
		{ name: 'monthly_principal_interest', label: 'Monthly P&I', type: 'number' },
		{ name: 'monthly_escrow', label: 'Monthly escrow', type: 'number' }
	];
	const LOAN_STATEMENT_FIELDS: { name: string; label: string; type: 'date' | 'number'; required: boolean }[] = [
		{ name: 'current_balance', label: 'Opening balance', type: 'number', required: true },
		{ name: 'statement_principal_amount', label: 'Principal', type: 'number', required: true },
		{ name: 'statement_interest_amount', label: 'Interest', type: 'number', required: true },
		{ name: 'monthly_principal_interest', label: 'P&I', type: 'number', required: true },
		{ name: 'statement_escrow_amount', label: 'Escrow', type: 'number', required: true },
		{ name: 'statement_total_amount', label: 'Total cash', type: 'number', required: true },
		{ name: 'statement_effective_date', label: 'Statement due/payment date', type: 'date', required: false }
	];

	// Loan escrow-cover flags are booleans (not in the string-keyed editedFields). Seeded once from the
	// extraction, then owned by the user; reset per-draft alongside the other editable state.
	let loanEscrowCoversTaxes = $state(false);
	let loanEscrowCoversInsurance = $state(false);
	let loanEscrowSeeded = $state(false);

	function parseBoolish(value: string): boolean {
		return ['true', 'yes', 'y', '1'].includes(value.trim().toLowerCase());
	}

	// A Loan requires a lender + a property (the property comes from the scan-context deep-link, but the
	// reviewer can change it). Mirrors leaseSelectionInvalid / applicationInvalid.
	const loanInvalid = $derived.by(() => {
		if (!isLoan) return false;
		if (!selectedPropertyId || selectedPropertyId === NO_PROPERTY) return true;
		if (loanReviewMode === 'match') {
			return !selectedExistingLoanId ||
				!selectedExistingLoanPaymentId ||
				(selectedExistingLoanPayment?.status !== 'Scheduled'
					&& selectedExistingLoanPayment?.status !== 'Paid') ||
				LOAN_STATEMENT_FIELDS.some((field) =>
					field.required && parseAmount(editedFields[field.name]) == null);
		}
		return !(editedFields['lender'] ?? '').trim();
	});

	// Lease term fields shown as editable inputs (in display order).
	const LEASE_TERM_FIELDS: { name: string; label: string; type: 'text' | 'date' | 'number' }[] = [
		{ name: 'lease_number', label: 'Lease number', type: 'text' },
		{ name: 'start_date', label: 'Start date', type: 'date' },
		{ name: 'end_date', label: 'End date', type: 'date' },
		{ name: 'monthly_rent', label: 'Monthly rent', type: 'number' },
		{ name: 'security_deposit', label: 'Security deposit', type: 'number' },
		{ name: 'late_fee', label: 'Late fee', type: 'number' },
		{ name: 'rent_due_day', label: 'Rent due day', type: 'number' }
	];

	// Reset the unit pick only when the reviewer changes property. A paged/search result that does not
	// contain the selected unit must not erase a valid choice.
	$effect(() => {
		if (!canCreateLeaseProperty && selectedLeasePropertyId === CREATE_PROPERTY) {
			selectedLeasePropertyId = '';
		}
		const nextPropertyId = selectedLeasePropertyId;
		if (previousLeasePropertyId && previousLeasePropertyId !== nextPropertyId) {
			selectedLeaseUnitId = '';
			unitSearch = '';
			unitSkip = 0;
		}
		previousLeasePropertyId = nextPropertyId;
		// Create-new mode has no existing-unit pick — drop any stale id so it can't leak.
		if (isCreatingLeaseProperty) {
			if (selectedLeaseUnitId) selectedLeaseUnitId = '';
		}
	});

	const selectedPropertyLabel = $derived.by(() => {
		if (!selectedPropertyId || selectedPropertyId === NO_PROPERTY) return '— No property —';
		const sel = propertyChoices.find((p) => String(p.id) === selectedPropertyId);
		return sel ? sel.name : `Property #${selectedPropertyId}`;
	});

	const selectedExpenseUnitLabel = $derived.by(() => {
		if (!selectedExpenseUnitId || selectedExpenseUnitId === NO_UNIT) return '— No unit —';
		const selected = expenseUnitChoices.find((unit) => String(unit.id) === selectedExpenseUnitId);
		return selected ? `Unit ${selected.unitNumber}` : `Unit #${selectedExpenseUnitId}`;
	});

	function expenseWorkOrderLabel(workOrder: WorkOrder): string {
		const unit = workOrder.unitNumber ? ` · Unit ${workOrder.unitNumber}` : '';
		return `${workOrder.title}${unit} · ${formatStatusLabel(workOrder.status)}`;
	}

	const selectedExpenseWorkOrderLabel = $derived.by(() => {
		if (!selectedExpenseWorkOrderId || selectedExpenseWorkOrderId === NO_WORK_ORDER) return '— No work order —';
		const selected = expenseWorkOrderChoices.find((workOrder) => String(workOrder.id) === selectedExpenseWorkOrderId);
		return selected
			? expenseWorkOrderLabel(selected)
			: selectedExpenseWorkOrderLabelText || `Work order #${selectedExpenseWorkOrderId}`;
	});

	function clearExpenseWorkOrderSelection() {
		selectedExpenseWorkOrderId = NO_WORK_ORDER;
		selectedExpenseWorkOrderLabelText = '';
		workOrderSearch = '';
		workOrderSkip = 0;
	}

	function selectContextPropertyId(value: string | undefined): void {
		const next = value ?? NO_PROPERTY;
		selectedPropertyId = next;
		if (isExpense) {
			selectedExpenseUnitId = NO_UNIT;
			unitSearch = '';
			unitSkip = 0;
			clearExpenseWorkOrderSelection();
		}
	}

	function selectLoanPropertyId(value: string | undefined): void {
		selectedPropertyId = value ?? NO_PROPERTY;
		selectedExistingLoanId = '';
		selectedExistingLoanPaymentId = '';
	}

	function selectExistingLoanId(value: string | undefined): void {
		selectedExistingLoanId = value ?? '';
		selectedExistingLoanPaymentId = '';
	}

	function selectExpenseUnitId(value: string | undefined): void {
		selectedExpenseUnitId = value ?? NO_UNIT;
		clearExpenseWorkOrderSelection();
	}

	function selectExpenseWorkOrder(value: string | undefined): void {
		selectedExpenseWorkOrderId = value ?? NO_WORK_ORDER;
		const selected = expenseWorkOrderChoices.find((workOrder) => String(workOrder.id) === selectedExpenseWorkOrderId);
		selectedExpenseWorkOrderLabelText = selected ? expenseWorkOrderLabel(selected) : '';
		if (!selected) return;
		selectedPropertyId = String(selected.propertyId);
		selectedExpenseUnitId = selected.unitId ? String(selected.unitId) : NO_UNIT;
	}

	$effect(() => {
		if (!isExpense || selectedExpenseWorkOrderId === NO_WORK_ORDER) return;
		const selected = expenseWorkOrderChoices.find((workOrder) => String(workOrder.id) === selectedExpenseWorkOrderId);
		if (!selected) return;
		selectedExpenseWorkOrderLabelText = expenseWorkOrderLabel(selected);
		if (selectedPropertyId !== String(selected.propertyId)) selectedPropertyId = String(selected.propertyId);
		const nextUnitId = selected.unitId ? String(selected.unitId) : NO_UNIT;
		if (selectedExpenseUnitId !== nextUnitId) selectedExpenseUnitId = nextUnitId;
	});

	function tenantAccountLabel(account: TenantAccountChoice): string {
		const tenantName = account.primaryTenantName;
		const tenant = tenantName ? ` — ${tenantName}` : '';
		return `${account.propertyName} · Unit ${account.unitNumber}${tenant} · ${account.relationshipNumber}`;
	}

	const selectedTenantAccountLabel = $derived.by(() => {
		if (!selectedTenantAccountId) return '— Select a rental account —';
		const selected = tenantAccountChoices.find(
			(account) => String(account.tenantAccountId) === selectedTenantAccountId);
		return selected ? tenantAccountLabel(selected) : selectedTenantAccountLabelText || '— Select a rental account —';
	});

	function selectTenantAccount(value: string | undefined): void {
		selectedTenantAccountId = value ?? '';
		const selected = tenantAccountChoices.find(
			(account) => String(account.tenantAccountId) === selectedTenantAccountId);
		selectedTenantAccountLabelText = selected ? tenantAccountLabel(selected) : '';
	}

	// Editable field values (keyed by field name, scalars only)
	let editedFields = $state<Record<string, string>>({});
	let editedFieldsSeeded = $state(false);
	let scanDateInvalid = $state<Record<string, boolean>>({});
	const SCAN_DATE_FIELDS = GENERIC_SCAN_DATE_FIELDS;

	function isScanDateField(name: string): boolean {
		return SCAN_DATE_FIELDS.has(name);
	}

	// Editable line items. Seeded once from the extraction (see the data $effect) and then
	// owned by the user; the bounded poll / cache refresh must not clobber in-progress edits,
	// hence the one-shot `lineItemsInitialized` guard. `lineItemKeySeq` hands out stable row keys.
	let editedLineItems = $state<EditableLineItem[]>([]);
	let lineItemsInitialized = $state(false);
	let lineItemKeySeq = 0;

	// Parse a money-ish string ("$1,234.50") into a number, or null if unparseable.
	function parseAmount(raw: string | undefined | null): number | null {
		if (raw == null) return null;
		const cleaned = String(raw).replace(/[^0-9.\-]/g, '');
		if (cleaned === '' || cleaned === '-' || cleaned === '.') return null;
		const n = Number(cleaned);
		return Number.isFinite(n) ? n : null;
	}

	// The amount the server will use for the expense = edited total, falling back to subtotal.
	// (Mirrors the server: Amount = total ?? subtotal.) Drives the $0 confirm guard.
	const resolvedAmount = $derived.by(() =>
		parseAmount(editedFields['total']) ?? parseAmount(editedFields['subtotal'])
	);

	// --- Editable line items: seed/add/remove + a running total echo ---

	function toEditableLineItems(items: LineItem[]): EditableLineItem[] {
		return items.map((li) => ({
			key: lineItemKeySeq++,
			description: li.description ?? '',
			quantity: li.quantity != null ? String(li.quantity) : '',
			unit_price: li.unit_price != null ? String(li.unit_price) : '',
			amount: li.amount != null ? String(li.amount) : ''
		}));
	}

	function addLineItem() {
		editedLineItems = [
			...editedLineItems,
			{ key: lineItemKeySeq++, description: '', quantity: '', unit_price: '', amount: '' }
		];
	}

	function removeLineItem(key: number) {
		editedLineItems = editedLineItems.filter((li) => li.key !== key);
	}

	// Sum of the per-row amounts the user currently sees — echoed under the table so a hand
	// correction stays honest. Compared to the subtotal (sum of line items should equal it) only
	// as a soft, non-blocking hint: tax/tip/shipping legitimately separate the subtotal from the
	// grand total, so we never auto-overwrite or block confirm on a mismatch.
	const lineItemsTotal = $derived(
		editedLineItems.reduce((sum, li) => sum + (parseAmount(li.amount) ?? 0), 0)
	);
	const subtotalAmount = $derived(parseAmount(editedFields['subtotal']));
	const lineItemsMismatch = $derived(
		isExpense &&
			editedLineItems.length > 0 &&
			subtotalAmount != null &&
			Math.abs(lineItemsTotal - subtotalAmount) > 0.01
	);

	// Block confirming an expense whose amount is blank/0/negative (server rejects amount <= 0).
	// Only applies to Expense drafts — Payment, WorkOrder, Lease, and Application have their own guards.
	const amountInvalid = $derived(isExpense && (resolvedAmount == null || resolvedAmount <= 0));

	// Lease drafts require either (a) an existing in-portfolio property + unit, or (b) create-new with a
	// usable property address/name + a unit number. Create-new is what lets a brand-new landlord with an
	// empty portfolio confirm at all.
	const leaseSelectionInvalid = $derived.by(() => {
		if (!isLease) return false;
		if (isCreatingLeaseProperty) {
			if (!canCreateLeaseProperty) return true;
			const hasPropertyAnchor = !!(newPropertyAddress.trim() || newPropertyName.trim());
			return !hasPropertyAnchor || !newPropertyRentalStructure || !newUnitNumber.trim();
		}
		return !selectedLeasePropertyId || !selectedLeaseUnitId;
	});
	const leaseSignatureChoiceInvalid = $derived(
		isLease &&
			!leaseReviewDisposition
	);
	const leaseRentTrackingInvalid = $derived(
		isLease && rentTrackingStartMode === 'CustomCutoffDate' && !rentTrackingStartOn
	);

	// Paid / Unpaid toggle — true = already paid (receipt), false = unpaid bill
	let isPaid = $state(true);
	let isPaidInitialized = $state(false);

	function defaultIsPaidFromKind(kind: string | undefined): boolean {
		if (!kind) return true;
		return !['Bill', 'Invoice', 'UtilityBill', 'PropertyTax'].includes(kind);
	}

	let editableStateDraftId = $state<number | null>(null);
	$effect(() => {
		if (!draftId || editableStateDraftId === draftId) return;
		editableStateDraftId = draftId;
		editedFields = {};
		editedFieldsSeeded = false;
		scanDateInvalid = {};
		editedLineItems = [];
		lineItemsInitialized = false;
		lineItemKeySeq = 0;
		isPaid = true;
		isPaidInitialized = false;
		selectedTenantAccountId = '';
		selectedTenantAccountLabelText = '';
		tenantAccountSearch = '';
		tenantAccountSkip = 0;
		selectedPropertyId = NO_PROPERTY;
		selectedExpenseUnitId = NO_UNIT;
		selectedExpenseWorkOrderId = NO_WORK_ORDER;
		selectedExpenseWorkOrderLabelText = '';
		propertySearch = '';
		propertySkip = 0;
		tenantSearch = '';
		tenantSkip = 0;
		unitSearch = '';
		unitSkip = 0;
		workOrderSearch = '';
		workOrderSkip = 0;
		loanEscrowCoversTaxes = false;
		loanEscrowCoversInsurance = false;
		loanEscrowSeeded = false;
		loanReviewMode = 'match';
		selectedExistingLoanId = '';
		selectedExistingLoanPaymentId = '';
		resetLeaseReviewState();
	});

	// Initialize editable fields when data arrives
	$effect(() => {
		if (data?.fields) {
			let initial: Record<string, string> = {};
			if (!editedFieldsSeeded && data.fields.length > 0) {
				initial = buildScanReviewInitialEditedFields(data.fields, data.targetEntityType);
				editedFieldsSeeded = true;
			} else {
				for (const f of data.fields) {
					if (f.name === LINE_ITEMS_FIELD) continue;
					if (!(f.name in editedFields)) {
						initial[f.name] = f.name === 'category'
							? scanCategoryValue(f.value, data.targetEntityType)
							: f.value;
					}
				}
			}
			if (Object.keys(initial).length > 0) {
				editedFields = { ...editedFields, ...initial };
			}
			// Seed the editable line-items table once extraction has produced fields. The one-shot
			// guard means later refetches (the bounded poll, cache refresh) never discard edits.
			if (!lineItemsInitialized && data.fields.length > 0) {
				editedLineItems = toEditableLineItems(parseLineItems(data.fields));
				lineItemsInitialized = true;
			}
			// Initialize isPaid from document_kind once on first data arrival
			if (!isPaidInitialized) {
				const kindField = data.fields.find((f) => f.name === 'document_kind');
				isPaid = defaultIsPaidFromKind(kindField?.value);
				isPaidInitialized = true;
			}
			if ((data.targetEntityType === 'WorkOrder' || data.targetEntityType === 'Expense' || data.targetEntityType === 'Application' || data.targetEntityType === 'Loan') && selectedPropertyId === NO_PROPERTY) {
				const resolvedPropertyId = resolveScanReviewPropertyId({
					contextPropertyId: scanContext.propertyId,
					fields: data.fields,
					properties: propertyChoices
				});
				if (resolvedPropertyId) selectedPropertyId = resolvedPropertyId;
			}
			if (data.targetEntityType === 'Expense' && selectedExpenseWorkOrderId === NO_WORK_ORDER) {
				const contextWorkOrderId = data.captureContext?.workOrderId ?? scanContext.workOrderId;
				if (contextWorkOrderId) selectedExpenseWorkOrderId = String(contextWorkOrderId);
			}
			if (data.targetEntityType === 'Expense' && selectedExpenseUnitId === NO_UNIT) {
				const contextUnitId = data.captureContext?.unitId ?? scanContext.unitId;
				if (contextUnitId) selectedExpenseUnitId = String(contextUnitId);
			}
			// Loan drafts: seed the escrow-cover checkboxes from the extraction once.
			if (data.targetEntityType === 'Loan' && !loanEscrowSeeded && data.fields.length > 0) {
				loanEscrowCoversTaxes = parseBoolish(fieldValue('escrow_covers_taxes'));
				loanEscrowCoversInsurance = parseBoolish(fieldValue('escrow_covers_insurance'));
				loanEscrowSeeded = true;
			}
			if (data.targetEntityType === 'Payment' && !selectedTenantAccountId && tenantAccountOptionsQuery.data) {
				const extractedAccountId = Number(fieldValue('tenant_account_id') || 0);
				const contextAccountId = data.captureContext?.tenantAccountId ?? scanContext.tenantAccountId;
				const extractedRelationship = fieldValue('relationship_number').trim().toLowerCase();
				const match = tenantAccountChoices.find((account) =>
					(!!contextAccountId && account.tenantAccountId === contextAccountId)
					|| (extractedAccountId > 0 && account.tenantAccountId === extractedAccountId)
					|| (!!extractedRelationship && account.relationshipNumber.toLowerCase() === extractedRelationship)
					|| (!!scanContext.unitId && account.unitId === scanContext.unitId));
				if (match) {
					selectedTenantAccountId = String(match.tenantAccountId);
					selectedTenantAccountLabelText = tenantAccountLabel(match);
				}
			}
			// Lease drafts: seed the property/unit pickers + the create-new fields. Tenant stays on
			// "create new" unless the user picks.
			if (data.targetEntityType === 'LeaseAgreement' && shouldSeedLeaseReviewState(data.status, data.fields.length)) {
				// Seed editable new-property fields from the extracted lease fields (one-shot, so later
				// refetches / the user's own edits aren't clobbered).
				if (!newPropertyFieldsSeeded) {
					newPropertyName = fieldValue('property_name');
					newPropertyAddress = fieldValue('property_address');
					newPropertyCity = fieldValue('property_city');
					newPropertyState = fieldValue('property_state');
					newPropertyPostal = fieldValue('property_postal_code');
					newUnitNumber = fieldValue('unit_number');
					newPropertyFieldsSeeded = true;
				}
				// Seed the property pick once: prefer an existing in-portfolio match (extracted id or the
				// proposal's link), otherwise default to "create new" so an empty portfolio can proceed.
				if (!leasePropertySeeded && (propertiesQuery.data || leaseProposal)) {
					const propertyField = data.fields.find((f) => f.name === 'property_id' || f.name === 'propertyId');
					const extractedId = propertyField?.value;
					if (extractedId && propertyChoices.some((p) => String(p.id) === extractedId)) {
						selectedLeasePropertyId = extractedId;
					} else if (leaseProposal?.property.action === 'link' && leaseProposal.property.existingId != null) {
						selectedLeasePropertyId = String(leaseProposal.property.existingId);
					} else if (canCreateLeaseProperty && leaseProposal && (leaseProposal.property.action === 'create' || leaseProposal.property.action === 'select')) {
						// Nothing to link — start in create-new mode (the empty-portfolio bootstrap).
						selectedLeasePropertyId = CREATE_PROPERTY;
					}
					leasePropertySeeded = true;
				}
				if (!selectedLeaseUnitId && !isCreatingLeaseProperty) {
					const unitField = data.fields.find((f) => f.name === 'unit_id' || f.name === 'unitId');
					const seededUnitId = seedLeaseUnitId(unitField?.value, leaseProposal?.unit, isCreatingLeaseProperty);
					if (seededUnitId) selectedLeaseUnitId = seededUnitId;
				}
			}
		}
	});

	// Svelte action to focus the first low-confidence field
	let firstLowFocused = $state(false);
	function focusFirstLow(node: HTMLElement, isFirstLow: boolean) {
		if (isFirstLow && !firstLowFocused) {
			firstLowFocused = true;
			// defer to next tick so the DOM is ready
			setTimeout(() => node.focus(), 0);
		}
		return {};
	}

	function confidenceLevel(confidence: number): 'high' | 'medium' | 'low' {
		if (confidence >= 0.8) return 'high';
		if (confidence >= 0.5) return 'medium';
		return 'low';
	}

	function confidenceLabel(confidence: number): string {
		const level = confidenceLevel(confidence);
		if (level === 'medium') return 'Medium confidence';
		if (level === 'low') return 'Low confidence';
		return '';
	}

	// User-facing status wording. "Reviewing" really means "waiting for you to review".
	function statusLabel(s: string): string {
		switch (s) {
			case 'Pending':
			case 'Processing': return 'Processing';
			case 'Reviewing': return 'Ready to review';
			case 'Confirmed': return 'Confirmed';
			case 'Failed': return 'Extraction failed';
			case 'Rejected': return 'Rejected';
			default: return formatStatusLabel(s);
		}
	}

	function statusBadgeClass(status: string): string {
		switch (status) {
			case 'Pending':
			case 'Processing':
				return 'm3-tone-chip border m3-tone--warning';
			case 'Reviewing':
				return 'm3-tone-chip border m3-tone--info';
			case 'Confirmed':
				return 'm3-tone-chip border m3-tone--success';
			case 'Failed':
			case 'Rejected':
				return 'm3-tone-chip border m3-tone--error';
			default:
				return '';
		}
	}

	function fieldInputClass(_field: ScanFieldDto): string {
		// Confidence is conveyed by the label text only — no colored outline on the input.
		return '';
	}

	function isFirstLowField(fields: ScanFieldDto[], field: ScanFieldDto): boolean {
		const firstLow = fields.filter((f) => f.name !== LINE_ITEMS_FIELD).find((f) => confidenceLevel(f.confidence) === 'low');
		return firstLow?.name === field.name;
	}

	// Parse line_items field value
	function parseLineItems(fields: ScanFieldDto[]): LineItem[] {
		const f = fields.find((f) => f.name === LINE_ITEMS_FIELD);
		if (!f?.value) return [];
		try {
			const parsed = JSON.parse(f.value);
			if (Array.isArray(parsed)) return parsed as LineItem[];
			return [];
		} catch {
			return [];
		}
	}

	function formatMoney(val: number | null | undefined): string {
		if (val == null) return '';
		return val.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
	}

	function fieldValue(name: string): string {
		return data?.fields.find((f) => f.name === name)?.value ?? '';
	}

	// Preview goes through a same-origin, cookie-authed SvelteKit route (the API's
	// /file endpoint needs a JWT bearer an <img>/<iframe> can't send). The inline preview
	// uses the default (downsized) thumbnail for speed; "Open in new tab" requests the
	// full-resolution original the landlord captured (?full=true).
	const fileUrl = $derived(data ? `/scan-file/${data.id}` : '');
	const fileUrlFull = $derived(data ? `/scan-file/${data.id}?full=true` : '');

	// Success state — what was just created, so the landlord keeps context
	// instead of being dumped onto /accounting. (Lease drafts navigate straight to
	// the new lease instead, so they don't use this card.)
	let confirmedRecord = $state<{ type: 'Expense' | 'Payment' | 'WorkOrder'; id: number | null; amount: number | null; unitId: number | null } | null>(null);
	let depositFundingRedirectPending = $state(false);
	const isTerminal = $derived(isTerminalScanReview(data?.status, !!confirmedRecord));
	const reviewControlsDisabled = $derived(shouldDisableScanReviewControls(data?.status, !!confirmedRecord));
	const scanDatePickerInvalid = $derived(
		hasVisibleScanDateInvalid(
			scanDateInvalid,
			activeScanDateFieldNames({
				targetEntityType: data?.targetEntityType,
				loanReviewMode,
				fields: data?.fields ?? [],
				isProcessing,
				status: data?.status,
				isTerminal
			})
		)
	);
	const canRecoverPaymentDepositHandoff = $derived(
		isPayment &&
			!!data?.sourceStoredFileId &&
			(data.status === 'Rejected' || data.status === 'Failed') &&
			!confirmedRecord
	);
	const showPaymentTenantAccountSelector = $derived(isPayment && (!isTerminal || canRecoverPaymentDepositHandoff));
	const disablePaymentTenantAccountSelector = $derived(reviewControlsDisabled && !canRecoverPaymentDepositHandoff);
	const lowConfidenceFields = $derived(
		(data?.fields ?? []).filter(
			(field) => field.name !== LINE_ITEMS_FIELD && confidenceLevel(field.confidence) === 'low'
		)
	);
	const mediumConfidenceFields = $derived(
		(data?.fields ?? []).filter(
			(field) => field.name !== LINE_ITEMS_FIELD && confidenceLevel(field.confidence) === 'medium'
		)
	);
	const proposedCommand = $derived.by(() => {
		if (isPayment) return 'Record one payment receipt on the selected rental account';
		if (isWorkOrder) return 'Create one work order for the selected property';
		if (isLease) {
			return isCreatingLeaseProperty
				? 'Create a property and unit, then create the rental relationship and agreement'
				: 'Create one rental relationship and agreement for the selected unit';
		}
		if (isApplication) return 'Create one rental application';
		if (isLoan) {
			return loanReviewMode === 'match'
				? 'Match this statement and record one scheduled loan payment'
				: 'Add one loan to the selected property';
		}
		return 'Create one expense and attach this source document';
	});
	const proposedDestination = $derived.by(() => {
		if (isPayment) return selectedTenantAccountLabel;
		if (isLease) {
			if (isCreatingLeaseProperty) {
				return `${newPropertyAddress || newPropertyName || 'New property'} · Unit ${newUnitNumber || 'not selected'}`;
			}
			return `${selectedLeasePropertyLabel} · ${selectedLeaseUnitLabel}`;
		}
		if (isApplication) {
			return selectedPropertyId !== NO_PROPERTY
				? selectedPropertyLabel
				: scanContext.unitId
					? `Unit #${scanContext.unitId}`
					: 'Applications queue';
		}
		if (isLoan && loanReviewMode === 'match') {
			const loan = selectedExistingLoan;
			const payment = selectedExistingLoanPayment;
			return loan && payment
				? `${selectedPropertyLabel} · ${loan.lender} · ${payment.periodKey}`
				: selectedPropertyLabel;
		}
		if (isWorkOrder || isLoan || isExpense) return selectedPropertyLabel;
		return 'Review required';
	});
	const extractionWarningCount = $derived(
		lowConfidenceFields.length + (data?.missingRequired?.length ?? 0) + (data?.ambiguous ? 1 : 0)
	);
	const linkedRecordHref = $derived((() => {
		const type = confirmedRecord?.type ?? data?.createdEntityType;
		const id = confirmedRecord?.id ?? data?.createdEntityId;
		const unitId = confirmedRecord?.unitId ?? data?.createdUnitId ?? scanContext.unitId ?? null;
		return createdRecordHref(type, id, {
			unitId,
			tenantAccountId: selectedTenantAccountId ? Number(selectedTenantAccountId) : null,
			leaseManagementId: data?.captureContext?.leaseManagementId ?? scanContext.leaseManagementId ?? null
		});
	})());
	const depositFundingHref = $derived.by(() => {
		if (!isPayment || !data?.sourceStoredFileId) return null;
		const accountId = selectedTenantAccountId
			? Number(selectedTenantAccountId)
			: data.captureContext?.tenantAccountId ?? scanContext.tenantAccountId ?? null;
		if (!accountId || !Number.isFinite(accountId) || accountId <= 0) return null;
		const params = new URLSearchParams({
			sourceStoredFileId: String(data.sourceStoredFileId),
			sourceDraftId: String(data.id)
		});
		return `/deposits/${accountId}?${params.toString()}`;
	});

	async function recordScanAsDepositFunds(): Promise<void> {
		if (!depositFundingHref || !data) return;
		depositFundingRedirectPending = true;
		try {
			if (data.status !== 'Rejected' && data.status !== 'Failed') {
				await scan.reject(
					data.id,
					'Reclassified as a security deposit receipt; record it through the deposit workflow.'
				);
				queryClient.invalidateQueries({ queryKey: ['scans'] });
				queryClient.invalidateQueries({ queryKey: ['scan', draftId] });
			}
			await goto(depositFundingHref);
		} catch (err) {
			depositFundingRedirectPending = false;
			toast.error(err instanceof Error ? err.message : 'Could not open deposit funding.');
		}
	}

	function formatUsd(val: number | null): string {
		return val == null ? '' : formatAccountingCurrency(val);
	}

	// A draft that was already confirmed in a PRIOR session (status Confirmed, no fresh in-session
	// confirmedRecord, and not a Lease — leases navigate away on confirm). For these we show a
	// prominent "view the created record" card instead of leaving a disabled Confirm button as the
	// only signpost. Only show when we actually know what record to link to.
	const alreadyConfirmed = $derived(
		data?.status === 'Confirmed' && !confirmedRecord && !isLease && !isLoan && !!data?.createdEntityId
	);

	// Friendly label for the created record's type (drives the "View/Edit the Payment" button).
	const createdTypeLabel = $derived.by(() => {
		const type = confirmedRecord?.type ?? data?.createdEntityType;
		return createdRecordLabel(type);
	});

	const returnToSourceLabel = $derived.by(() => {
		const href = scanContext.returnTo ?? '';
		if (href.startsWith('/units/')) return 'Back to unit';
		if (href.startsWith('/properties/')) return 'Back to property';
		if (href.startsWith('/maintenance')) return 'Back to maintenance';
		if (href.startsWith('/leases/')) return 'Back to lease';
		return 'Back to workflow';
	});

	// Confirm mutation
	const confirmMutation = createMutation(() => ({
		mutationFn: () => {
			const overrides = buildOverridesJson();
			return scan.confirm(draftId, overrides);
		},
		onSuccess: (result) => {
			queryClient.invalidateQueries({ queryKey: ['scan', draftId] });
			invalidateQueriesAfterScanConfirm(queryClient, result.entityType);
			// Lease drafts go straight to the new lease detail page.
			if (isLease) {
				const leaseManagementId = result.leaseManagementId ?? null;
				toast.success('Lease created');
				if (leaseManagementId) {
					const unitId = result.unitId ?? (selectedLeaseUnitId ? Number(selectedLeaseUnitId) : null) ?? scanContext.unitId ?? null;
					goto(recordHref('leaseManagement', { id: leaseManagementId, unitId }));
				} else {
					goto('/leases');
				}
				return;
			}
			if (isApplication) {
				const applicationId = result.applicationId ?? result.entityId ?? null;
				toast.success('Applicant created');
				goto(applicationId
					? createdRecordHref('Application', applicationId, {
						unitId: result.unitId ?? scanContext.unitId ?? null
					})
					: '/applications');
				return;
			}
			// Loan drafts attach to a property (no standalone loan page) — go back to that property,
			// where PropertyLoansSection shows the new loan.
			if (isLoan) {
				toast.success(loanReviewMode === 'match' ? 'Loan payment matched and recorded' : 'Loan added');
				const propertyId = selectedPropertyId && selectedPropertyId !== NO_PROPERTY
					? Number(selectedPropertyId)
					: scanContext.propertyId;
				goto(scanContext.returnTo ?? (propertyId ? `/properties/${propertyId}` : '/scan'));
				return;
			}
			// Capture the resolved amount before refetch can mutate editedFields.
			const type = (result.entityType === 'Payment' || result.entityType === 'Expense' || result.entityType === 'WorkOrder')
				? result.entityType
				: isPayment ? 'Payment' : isWorkOrder ? 'WorkOrder' : 'Expense';
			confirmedRecord = {
				type,
				id: isPayment
					? result.receiptId ?? null
					: result.entityId ?? result.expenseId ?? result.workOrderId ?? null,
				amount: resolvedAmount,
				unitId: result.unitId ?? scanContext.unitId ?? null
			};
			if (isPayment) {
				toast.success('Payment recorded');
			} else if (isWorkOrder) {
				toast.success('Work order created');
			} else {
				toast.success('Expense created');
			}
			// Stay on the page and show a confirmation with a link to view it,
			// rather than silently navigating to /accounting.
		},
		onError: (err) => {
			toast.error(err instanceof Error ? err.message : 'Confirm failed');
		}
	}));

	// Reject mutation
	const rejectMutation = createMutation(() => ({
		mutationFn: (reason: string) => scan.reject(draftId, reason),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			toast.success('Scan rejected');
			goto('/scan');
		},
		onError: (err) => {
			toast.error(err instanceof Error ? err.message : 'Reject failed');
		}
	}));

	const retryMutation = createMutation(() => ({
		mutationFn: () => scan.retry(draftId),
		onSuccess: () => {
			resetLeaseReviewState();
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			queryClient.invalidateQueries({ queryKey: ['scan', draftId] });
			queryClient.invalidateQueries({ queryKey: ['scan-batches'] });
			toast.success('Scan queued again');
			draftQuery.refetch();
		},
		onError: (err) => {
			toast.error(err instanceof Error ? err.message : 'Retry failed');
		}
	}));

	function buildOverridesJson(): string {
		// Send edited scalar fields; line items are serialized separately (added below for expenses).
		// Map legacy snake_case names that the API still expects in camelCase;
		// all new expanded fields are sent as-is (API accepts snake_case override keys).
		const keyMap: Record<string, string> = {
			vendor_name: 'vendorName',
			amount: 'amount',
			transaction_date: 'transactionDate',
			category: 'category',
			notes: 'notes'
		};
		const overrides: Record<string, unknown> = {};
		for (const [name, value] of Object.entries(editedFields)) {
			if (name === LINE_ITEMS_FIELD) continue;
			overrides[keyMap[name] ?? name] = name === 'category'
				? scanCategoryValue(value, data?.targetEntityType)
				: value;
		}

		if (isApplication) {
			// Application drafts: the edited applicant scalar fields (first_name, last_name, email, phone,
			// date_of_birth, current_address, employer, monthly_income, applying_for, desired_move_in_date,
			// id_last4, co_signer_name) are already in `overrides` as snake_case keys, which the server's
			// ApplyApplicationOverrides accepts directly. Carry through the extracted property/unit link so
			// the applicant can be filed under the unit they applied for (server re-validates it in-portfolio).
			const propertyField = data?.fields.find((f) => f.name === 'property_id' || f.name === 'propertyId');
			const unitField = data?.fields.find((f) => f.name === 'unit_id' || f.name === 'unitId');
			const selectedApplicationPropertyId = selectedPropertyId !== NO_PROPERTY
				? Number(selectedPropertyId)
				: Number(propertyField?.value) || scanContext.propertyId || null;
			overrides['propertyId'] = selectedApplicationPropertyId;
			if (selectedApplicationPropertyId && selectedApplicationPropertyId !== scanContext.propertyId) {
				// Never carry a unit from the old inherited property into a newly selected destination.
				overrides['unitId'] = null;
			} else {
				overrides['unitId'] = Number(unitField?.value) || scanContext.unitId || null;
			}
			return JSON.stringify(overrides);
		}

		if (isLease) {
			// Lease drafts: pass through the edited term fields (lease_number, start_date,
			// end_date, monthly_rent, security_deposit, late_fee, rent_due_day — already in
			// `overrides`) plus the property/unit resolution and the optional tenant.
			// `tenantId` is omitted on "create new" so the server matches/creates by name.
			if (isCreatingLeaseProperty) {
				// Create-new path: propertyId=null forces the server's match-or-create to CREATE from the
				// (editable) extracted address. Send the address fields + the new unit number; omit unitId
				// so the unit is created under the new property.
				overrides['propertyId'] = null;
				overrides['unitId'] = null;
				overrides['rentalStructure'] = newPropertyRentalStructure;
				if (newPropertyName.trim()) overrides['propertyName'] = newPropertyName.trim();
				if (newPropertyAddress.trim()) overrides['propertyAddress'] = newPropertyAddress.trim();
				if (newPropertyCity.trim()) overrides['propertyCity'] = newPropertyCity.trim();
				if (newPropertyState.trim()) overrides['propertyState'] = newPropertyState.trim();
				if (newPropertyPostal.trim()) overrides['propertyPostalCode'] = newPropertyPostal.trim();
				if (newUnitNumber.trim()) overrides['unitNumber'] = newUnitNumber.trim();
			} else {
				overrides['propertyId'] = selectedLeasePropertyId ? Number(selectedLeasePropertyId) : null;
				overrides['unitId'] = selectedLeaseUnitId ? Number(selectedLeaseUnitId) : null;
			}
			if (selectedTenantId && selectedTenantId !== NO_TENANT) {
				overrides['tenantId'] = Number(selectedTenantId);
			}
			overrides['reviewDisposition'] = leaseReviewDisposition;
			if (leaseReviewDisposition === 'AlreadyFullySigned' && possessionGivenOn) {
				overrides['possessionGivenAtUtc'] = possessionGivenOn;
			}
			overrides['rentTrackingStartMode'] = rentTrackingStartMode;
			if (rentTrackingStartMode === 'CustomCutoffDate') {
				overrides['rentTrackingStartOn'] = rentTrackingStartOn;
			}
			if (leaseReviewDisposition === 'NeedsSignatures' && leaseDocumentTemplateId) {
				overrides['documentTemplateId'] = Number(leaseDocumentTemplateId);
			}
			return JSON.stringify(overrides);
		}

		if (isLoan) {
			// The edited loan scalar fields (lender, original_amount, current_balance,
			// annual_interest_rate_pct, term_months, start_date, day_of_month_due,
			// monthly_principal_interest, monthly_escrow, notes) and the statement-match fields
			// are already in `overrides` as snake_case keys, which the server's ApplyLoanOverrides accepts
			// directly. Add the required property and
			// the escrow-cover flags (booleans the checkboxes own, not in the string-keyed editedFields).
			if (selectedPropertyId && selectedPropertyId !== NO_PROPERTY) {
				overrides['propertyId'] = Number(selectedPropertyId);
			}
			if (loanReviewMode === 'match') {
				overrides['existingLoanId'] = Number(selectedExistingLoanId);
				overrides['existingLoanPaymentId'] = Number(selectedExistingLoanPaymentId);
			}
			overrides['escrowCoversTaxes'] = loanEscrowCoversTaxes;
			overrides['escrowCoversInsurance'] = loanEscrowCoversInsurance;
			applyScanContextOverrides(overrides, scanContext, 'Loan');
			return JSON.stringify(overrides);
		}

		// Explicitly send the user-edited amount as a clean number under `total`
		// (the server honors `total`/`amount`). Without this the edited value
		// could be dropped or mis-parsed when typed with a currency symbol/commas.
		if (resolvedAmount != null) {
			overrides['total'] = resolvedAmount;
		}

		if (isPayment) {
			// Payment drafts post directly to the selected continuous tenant account.
			overrides['tenantAccountId'] = selectedTenantAccountId
				? Number(selectedTenantAccountId)
				: null;
			applyScanContextOverrides(overrides, scanContext, 'Payment');
		} else if (isWorkOrder) {
			if (selectedPropertyId && selectedPropertyId !== NO_PROPERTY) {
				overrides['propertyId'] = Number(selectedPropertyId);
			}
			applyScanContextOverrides(overrides, scanContext, 'WorkOrder');
		} else {
			// Expense drafts: always include the paid/unpaid toggle decision
			overrides['is_paid'] = isPaid;
			// Optional property association (server honors `propertyId`).
			if (selectedPropertyId && selectedPropertyId !== NO_PROPERTY) {
				overrides['propertyId'] = Number(selectedPropertyId);
			}
			if (selectedExpenseUnitId && selectedExpenseUnitId !== NO_UNIT) {
				overrides['unitId'] = Number(selectedExpenseUnitId);
			}
			if (selectedExpenseWorkOrderId && selectedExpenseWorkOrderId !== NO_WORK_ORDER) {
				overrides['workOrderId'] = Number(selectedExpenseWorkOrderId);
			}
			applyScanContextOverrides(overrides, scanContext, 'Expense');
			// Include the (possibly edited) line items so corrections survive into the expense.
			// Numbers are parsed to clean values (null when blank); fully-empty rows are dropped so a
			// stray "Add line item" the user never filled doesn't persist a blank row. An empty array
			// is intentional and meaningful — it tells the server to keep NO line items.
			overrides[LINE_ITEMS_FIELD] = editedLineItems
				.map((li) => ({
					description: li.description.trim(),
					quantity: parseAmount(li.quantity),
					unit_price: parseAmount(li.unit_price),
					amount: parseAmount(li.amount)
				}))
				.filter(
					(li) =>
						li.description !== '' ||
						li.quantity != null ||
						li.unit_price != null ||
						li.amount != null
				);
		}
		return JSON.stringify(overrides);
	}

	// Reject dialog state
	let showRejectDialog = $state(false);
	let rejectReason = $state('');

	function handleReject() {
		rejectReason = '';
		showRejectDialog = true;
	}

	function confirmReject() {
		showRejectDialog = false;
		rejectMutation.mutate(rejectReason);
	}
</script>

<svelte:head>
	<title>Review Document - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="scan-review">
	{#if draftQuery.isLoading}
		<div class="flex h-48 items-center justify-center">
			<div class="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
		</div>
	{:else if draftQuery.isError}
		<!-- M5: clear error state with retry instead of a perpetual spinner -->
		<div class="mx-auto max-w-md py-12 text-center" data-testid="scan-load-error">
			<div class="mb-3 flex justify-center text-destructive">
				<AlertTriangle class="h-9 w-9" />
			</div>
			<h2 class="mb-1 text-lg font-semibold">Couldn't load this scan</h2>
			<p class="mb-4 text-sm text-muted-foreground">
				{draftQuery.error instanceof Error ? draftQuery.error.message : 'Something went wrong fetching the scan draft.'}
			</p>
			<div class="flex items-center justify-center gap-3">
				<Button data-testid="scan-load-retry" onclick={() => draftQuery.refetch()} disabled={draftQuery.isFetching}>
					{draftQuery.isFetching ? 'Retrying…' : 'Try again'}
				</Button>
				<Button variant="outline" href="/scan">Back to scans</Button>
			</div>
		</div>
	{:else if !data}
		<div class="mx-auto max-w-md py-12 text-center">
			<p class="mb-4 text-sm text-muted-foreground">Scan draft not found.</p>
			<Button variant="outline" href="/scan">Back to scans</Button>
		</div>
	{:else}
		<div class="mb-4">
			<PageBreadcrumb
				crumbs={[
					{ label: 'Scans', href: '/scan' },
					{ label: 'Review' },
				]}
			/>
		</div>
		<div class="mb-4 flex items-center gap-3">
			<h1 class="text-xl font-bold">Review scanned document</h1>
			<Badge variant="outline" class={statusBadgeClass(data.status)} data-testid="scan-status-badge" data-status={data.status}>
				{statusLabel(data.status)}
			</Badge>
		</div>
		{#if inheritedContextItems.length > 0 || scanContext.sourceLabel}
			<div class="mb-4 rounded-lg border bg-muted/35 px-4 py-3" data-testid="scan-inherited-context">
				<p class="text-sm font-medium">This document came from an existing rental record.</p>
				{#if scanContext.sourceLabel}
					<p class="mt-1 text-sm text-muted-foreground">{scanContext.sourceLabel}</p>
				{/if}
				<p class="mt-1 text-xs text-muted-foreground">Check where it will be saved below before you confirm.</p>
				{#if inheritedContextItems.length > 0}
					<details class="mt-2 text-xs text-muted-foreground">
						<summary class="cursor-pointer font-medium">Linked record references</summary>
						<div class="mt-2 flex flex-wrap gap-2">
							{#each inheritedContextItems as item}
								<Badge variant="secondary">{item}</Badge>
							{/each}
						</div>
					</details>
				{/if}
			</div>
		{/if}

		{#if confirmedRecord}
			<!-- design#11: keep context after confirm instead of dumping to /accounting -->
			<div
				class="mb-4 flex flex-col gap-3 rounded-lg border bg-[var(--m3c-success-container)] text-[var(--m3c-on-success-container)] border-[color-mix(in_srgb,var(--success)_45%,transparent)] px-4 py-4 text-sm sm:flex-row sm:items-center sm:justify-between"
				data-testid="scan-confirm-success"
			>
				<div class="flex items-center gap-3">
					<CheckCircle2 class="h-6 w-6 shrink-0" />
					<div>
						<p class="font-semibold">
							{confirmedRecord.type === 'Payment' ? 'Payment recorded' : confirmedRecord.type === 'WorkOrder' ? 'Work order created' : 'Expense created'}{confirmedRecord.type !== 'WorkOrder' && confirmedRecord.amount != null ? ` — ${formatUsd(confirmedRecord.amount)}` : ''}
						</p>
						<p class="text-xs opacity-80">{confirmedRecord.type === 'WorkOrder' ? 'It is saved to maintenance.' : 'It is saved to your books.'} You can view it or scan another document.</p>
					</div>
				</div>
				<div class="flex shrink-0 flex-wrap justify-end gap-2">
					<Button size="sm" href={linkedRecordHref} data-testid="scan-view-record">View/Edit Record</Button>
					{#if scanContext.returnTo}
						<Button size="sm" variant="outline" href={scanContext.returnTo} data-testid="scan-return-to-source">{returnToSourceLabel}</Button>
					{/if}
					<Button size="sm" variant="outline" href="/scan">Scan another</Button>
				</div>
			</div>
		{:else if alreadyConfirmed}
			<!-- design#16: a draft confirmed in a prior session is terminal. Front-and-center the link
			     to the created (editable) record instead of leaving a disabled Confirm button as the
			     only signpost. The draft stays an immutable, read-only summary below. -->
			<div
				class="mb-4 flex flex-col gap-3 rounded-lg border bg-[var(--m3c-success-container)] text-[var(--m3c-on-success-container)] border-[color-mix(in_srgb,var(--success)_45%,transparent)] px-4 py-4 text-sm sm:flex-row sm:items-center sm:justify-between"
				data-testid="scan-already-confirmed"
			>
				<div class="flex items-center gap-3">
					<CheckCircle2 class="h-6 w-6 shrink-0" />
					<div>
						<p class="font-semibold">This scan was confirmed</p>
						<p class="text-xs opacity-80">
							It created {createdRecordArticle(createdTypeLabel)} {createdTypeLabel}. This capture is read-only — make any edits on the record itself so your books stay the source of truth.
						</p>
					</div>
				</div>
				<div class="flex shrink-0 flex-wrap justify-end gap-2">
					<Button size="sm" href={linkedRecordHref} data-testid="scan-view-record">View/Edit {createdTypeLabel}</Button>
					{#if scanContext.returnTo}
						<Button size="sm" variant="outline" href={scanContext.returnTo} data-testid="scan-return-to-source">{returnToSourceLabel}</Button>
					{/if}
					<Button size="sm" variant="outline" href="/scan">Scan another</Button>
				</div>
			</div>
		{/if}

		{#if data.status === 'Failed'}
			<div class="mb-4 flex flex-col gap-3 rounded-lg px-4 py-3 text-sm m3-error-surface sm:flex-row sm:items-center sm:justify-between" data-testid="scan-failed-banner">
				<div>
					<p><strong>We couldn't read this document.</strong> The computer wasn't able to pull out the details automatically.</p>
					<p class="mt-1 text-xs opacity-90" data-testid="scan-failed-reason">
						{data.failureReason || 'No detailed reason was recorded.'}
					</p>
				</div>
				<div class="flex shrink-0 gap-2">
					<Button
						size="sm"
						onclick={() => retryMutation.mutate()}
						disabled={retryMutation.isPending}
						data-testid="scan-retry-extraction"
					>
						{retryMutation.isPending ? 'Queuing…' : 'Try extraction again'}
					</Button>
					<Button size="sm" variant="outline" onclick={handleReject} disabled={rejectMutation.isPending} data-testid="scan-failed-reject">
						Reject
					</Button>
				</div>
			</div>
		{/if}

		{#if isProcessing}
			<!-- design#10: prominent, readable "reading your document" state with progress -->
			<div
				class="mb-4 flex items-center gap-4 rounded-lg border border-accent/40 bg-accent/5 px-5 py-5"
				data-testid="scan-processing-banner"
				role="status"
				aria-live="polite"
			>
				<div class="h-9 w-9 shrink-0 animate-spin rounded-full border-[3px] border-accent border-t-transparent"></div>
				<div class="min-w-0">
					<p class="text-base font-semibold text-foreground">{processingCopy.title}</p>
					<p class="text-sm text-muted-foreground">
						{processingCopy.body}
					</p>
					<!-- Indeterminate progress bar -->
					<div class="mt-2 h-1.5 w-full max-w-sm overflow-hidden rounded-full bg-accent/20">
						<div class="h-full w-1/3 animate-pulse rounded-full bg-accent"></div>
					</div>
				</div>
			</div>
		{/if}

		{#if data.modelId === 'noop'}
			<div class="mb-4 rounded-lg px-4 py-3 text-sm m3-warning-surface" data-testid="scan-noop-banner">
				<strong>Automatic document reading is off.</strong> Enter the details manually below, or
				<a class="font-medium underline underline-offset-2" href="/settings/integrations/ai">connect an AI service in Settings</a>
				and scan the document again.
			</div>
		{/if}

		<div class="mb-4 grid gap-px overflow-hidden rounded-xl border bg-border md:grid-cols-[1.15fr_1fr_1fr]" data-testid="scan-proposed-command">
			<div class="bg-card px-4 py-3">
				<p class="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">What will happen</p>
				<p class="mt-1 text-sm font-semibold text-foreground">{proposedCommand}</p>
			</div>
			<div class="bg-card px-4 py-3">
				<p class="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Where it will be saved</p>
				<p class="mt-1 text-sm text-foreground">{proposedDestination}</p>
				<p class="mt-1 text-xs text-muted-foreground">You can change this choice before confirming.</p>
			</div>
			<div class="bg-card px-4 py-3">
				<p class="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Needs your review</p>
				<p class="mt-1 text-sm font-medium {extractionWarningCount > 0 ? 'text-[var(--warning)]' : 'text-[var(--success)]'}">
					{extractionWarningCount > 0 ? `${extractionWarningCount} item${extractionWarningCount === 1 ? '' : 's'} may need correction` : 'No unclear fields found'}
				</p>
				<p class="mt-1 text-xs text-muted-foreground">
					{lowConfidenceFields.length + mediumConfidenceFields.length} field{lowConfidenceFields.length + mediumConfidenceFields.length === 1 ? '' : 's'} may be hard to read. Rental Command checks for duplicates again when you save.
				</p>
			</div>
		</div>

		<details class="mb-4 rounded-lg border border-border bg-card text-xs text-muted-foreground" data-testid="scan-technical-details">
			<summary class="cursor-pointer px-4 py-3 font-medium text-foreground">Technical details</summary>
			<dl class="grid gap-3 border-t border-border px-4 py-3 sm:grid-cols-2 lg:grid-cols-4">
				<div><dt>Scan record</dt><dd class="mt-1 font-mono">#{data.id}</dd></div>
				{#if data.modelId}<div><dt>Reader</dt><dd class="mt-1 font-mono">{data.modelId}</dd></div>{/if}
				{#if data.tokensUsed != null}<div><dt>Reader usage</dt><dd class="mt-1 font-mono">{data.tokensUsed.toLocaleString()} tokens</dd></div>{/if}
				{#if data.costUsd != null}<div><dt>Estimated reader cost</dt><dd class="mt-1 font-mono">${data.costUsd.toFixed(4)}</dd></div>{/if}
				{#if data.sourceContentSha256}<div class="sm:col-span-2 lg:col-span-4"><dt>Source fingerprint</dt><dd class="mt-1 break-all font-mono">{data.sourceContentSha256}</dd></div>{/if}
			</dl>
		</details>

		<div class="grid items-start gap-4 xl:grid-cols-[minmax(20rem,0.82fr)_minmax(34rem,1.18fr)]" data-testid="scan-review-workspace">
				<!-- Left: document preview -->
				<Card.Root class="flex flex-col gap-0 py-0 xl:sticky xl:top-4">
					<Card.Header class="border-b border-border px-4 py-3 [.border-b]:pb-3">
						<div class="flex items-center justify-between gap-3">
							<Card.Title class="text-sm">Source document</Card.Title>
						</div>
						{#if scanContext.sourceLabel}<p class="text-xs text-muted-foreground">{scanContext.sourceLabel}</p>{/if}
					</Card.Header>
					<Card.Content class="flex flex-1 items-center justify-center overflow-hidden p-4">
						{#if isWorkOrder && fieldValue('transcript')}
							<div class="w-full rounded-md border bg-muted/30 p-4">
								<p class="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">Transcript</p>
								<p class="whitespace-pre-wrap text-sm leading-relaxed">{fieldValue('transcript')}</p>
							</div>
						{:else if fileUrl}
							<!-- Try img first; for PDFs it won't render but we also show an iframe/link -->
							<div class="w-full">
								<img
									src={fileUrl}
									alt="Scanned document"
									class="max-h-[60vh] w-full rounded object-contain"
									onerror={(e) => {
										// If image fails (it's a PDF), hide img and show iframe
										(e.currentTarget as HTMLImageElement).style.display = 'none';
										(e.currentTarget.nextElementSibling as HTMLElement | null)?.removeAttribute('hidden');
									}}
								/>
								<iframe
									hidden
									src={fileUrl}
									title="Scanned document"
									class="h-[60vh] w-full rounded border-0"
								></iframe>
								<div class="mt-2 text-center">
									<Button variant="link" href={fileUrlFull} target="_blank" rel="noopener noreferrer" class="h-auto p-0 text-xs" data-testid="scan-open-original">
										Open in new tab
									</Button>
								</div>
							</div>
						{:else}
							<p class="text-sm text-muted-foreground">No preview available.</p>
						{/if}
					</Card.Content>
				</Card.Root>

			<!-- Right: extracted fields form -->
			<Card.Root class="flex flex-col gap-0 py-0">
				<Card.Header class="border-b border-border px-4 py-3 [.border-b]:pb-3">
					<Card.Title class="text-sm">Review the details</Card.Title>
				</Card.Header>
				<Card.Content class="flex-1 overflow-y-auto p-4">
					{#if isProcessing}
						<p class="text-sm text-muted-foreground">
							Fields will appear once extraction completes.
						</p>
					{:else if data.status === 'Failed'}
						<p class="text-sm text-muted-foreground">
							No reviewed fields are available for this draft. Try extraction again or reject it.
						</p>
					{:else if isPayment}
						<!-- Canonical tenant-account selector — required for Payment drafts -->
						{#if showPaymentTenantAccountSelector}
							<div class="mb-5 space-y-2 rounded-md border border-border bg-muted/30 p-3">
								<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-tenant-account-select">
									{canRecoverPaymentDepositHandoff ? 'Which rental account should receive these deposit funds?' : 'Which rental account is this payment for?'} <span class="text-[var(--m3c-error)]">*</span>
								</label>
								<Input
									aria-label="Search rental accounts"
									placeholder="Search tenant, property, unit, or relationship number"
									value={tenantAccountSearch}
									disabled={disablePaymentTenantAccountSelector}
									oninput={(event) => {
										tenantAccountSearch = (event.currentTarget as HTMLInputElement).value;
										tenantAccountSkip = 0;
									}}
								/>
								<Select.Root type="single" value={selectedTenantAccountId} onValueChange={selectTenantAccount} disabled={disablePaymentTenantAccountSelector}>
									<Select.Trigger id="scan-tenant-account-select" data-testid="scan-tenant-account-select" class="w-full" disabled={disablePaymentTenantAccountSelector}>
										{selectedTenantAccountLabel}
									</Select.Trigger>
									<Select.Content>
										{#if tenantAccountOptionsQuery.data}
											{#each tenantAccountChoices as account (account.tenantAccountId)}
												<Select.Item value={String(account.tenantAccountId)} label={tenantAccountLabel(account)}>
													{tenantAccountLabel(account)}
												</Select.Item>
											{/each}
										{/if}
									</Select.Content>
								</Select.Root>
								{#if tenantAccountOptionsQuery.isLoading}
									<p class="mt-1 text-xs text-muted-foreground">Loading rental accounts…</p>
								{:else if tenantAccountOptionsQuery.data}
									<div class="flex items-center justify-between gap-2 text-xs text-muted-foreground">
										<span>
											{tenantAccountOptionsQuery.data.totalCount === 0
												? 'No matching rental accounts'
												: `${tenantAccountSkip + 1}–${Math.min(tenantAccountSkip + TENANT_ACCOUNT_PAGE_SIZE, tenantAccountOptionsQuery.data.totalCount)} of ${tenantAccountOptionsQuery.data.totalCount}`}
										</span>
										<div class="flex gap-1">
											<Button type="button" variant="outline" size="sm" disabled={tenantAccountSkip === 0} onclick={() => tenantAccountSkip = Math.max(0, tenantAccountSkip - TENANT_ACCOUNT_PAGE_SIZE)}>Previous</Button>
											<Button type="button" variant="outline" size="sm" disabled={tenantAccountSkip + TENANT_ACCOUNT_PAGE_SIZE >= tenantAccountOptionsQuery.data.totalCount} onclick={() => tenantAccountSkip += TENANT_ACCOUNT_PAGE_SIZE}>Next</Button>
										</div>
									</div>
								{/if}
							</div>
						{/if}
					{:else if isLease}
						<!-- Lease selectors — link an existing property/unit OR create them from the document. -->
						{#if !isTerminal}
							<div class="mb-5 space-y-3 rounded-md border border-border bg-muted/30 p-3" data-testid="scan-lease-selectors">
								<!-- "What this will do" summary from the import proposal, so the create-vs-link
								     outcome (incl. the empty-portfolio bootstrap) is visible up front. -->
								{#if leaseProposal}
									<div class="rounded-md border border-accent/40 bg-accent/5 p-2.5 text-xs" data-testid="scan-lease-proposal">
										<p class="mb-1 font-semibold text-foreground">When you confirm, this lease will:</p>
										<ul class="space-y-0.5 text-muted-foreground">
											<li data-testid="scan-lease-proposal-property">
												{#if isCreatingLeaseProperty || (canCreateLeaseProperty && leaseProposal.property.action === 'create')}
													<span class="font-medium text-[var(--success)]">Create</span> a new property{leaseProposal.property.label ? ` — ${leaseProposal.property.label}` : ''}
												{:else if leaseProposal.property.action === 'link'}
													<span class="font-medium">Link</span> to {leaseProposal.property.label ?? 'an existing property'}
												{:else}
													Need you to choose an authorized property
												{/if}
											</li>
											<li data-testid="scan-lease-proposal-unit">
												{#if isCreatingLeaseProperty || (canCreateLeaseProperty && leaseProposal.unit.action === 'create')}
													<span class="font-medium text-[var(--success)]">Create</span> {leaseProposal.unit.label ?? 'a unit'}
												{:else if leaseProposal.unit.action === 'link'}
													<span class="font-medium">Link</span> to {leaseProposal.unit.label ?? 'an existing unit'}
												{:else}
													Need you to choose a unit
												{/if}
											</li>
										</ul>
									</div>
								{/if}

								<div>
									<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-lease-property-select">
										Which property is this lease for? <span class="text-[var(--m3c-error)]">*</span>
									</label>
									<Input
										aria-label="Search properties"
										placeholder="Search properties by name or address"
										value={propertySearch}
										disabled={reviewControlsDisabled}
										oninput={(event) => {
											propertySearch = (event.currentTarget as HTMLInputElement).value;
											propertySkip = 0;
										}}
									/>
									<Select.Root type="single" bind:value={selectedLeasePropertyId} disabled={reviewControlsDisabled}>
										<Select.Trigger id="scan-lease-property-select" data-testid="scan-lease-property-select" class="w-full" disabled={reviewControlsDisabled}>
											{selectedLeasePropertyLabel}
										</Select.Trigger>
										<Select.Content>
											{#if canCreateLeaseProperty}
												<Select.Item value={CREATE_PROPERTY} label={newPropertyLabel}>{newPropertyLabel}</Select.Item>
											{/if}
									{#if propertiesQuery.data}
										{#each propertyChoices as prop (prop.id)}
													<Select.Item value={String(prop.id)} label={prop.name}>
														{prop.name}
													</Select.Item>
												{/each}
											{/if}
										</Select.Content>
									</Select.Root>
									{#if propertiesQuery.isLoading}
										<p class="mt-1 text-xs text-muted-foreground">Loading properties…</p>
									{:else if propertiesQuery.data}
										<div class="mt-2 flex items-center justify-between gap-2 text-xs text-muted-foreground">
											<span>{propertiesQuery.data.totalCount === 0 ? 'No matching properties' : `${propertySkip + 1}–${Math.min(propertySkip + CONTEXT_PAGE_SIZE, propertiesQuery.data.totalCount)} of ${propertiesQuery.data.totalCount}`}</span>
											<div class="flex gap-1">
												<Button type="button" variant="outline" size="sm" disabled={propertySkip === 0} onclick={() => (propertySkip = Math.max(0, propertySkip - CONTEXT_PAGE_SIZE))}>Previous</Button>
												<Button type="button" variant="outline" size="sm" disabled={propertySkip + CONTEXT_PAGE_SIZE >= propertiesQuery.data.totalCount} onclick={() => (propertySkip += CONTEXT_PAGE_SIZE)}>Next</Button>
											</div>
										</div>
									{/if}
								</div>

								{#if isCreatingLeaseProperty}
									<!-- Create-new property: editable address (seeded from the document) so the
									     reviewer can correct it before the property is created. -->
									<div class="space-y-2 rounded-md border border-dashed border-border p-2.5" data-testid="scan-lease-new-property">
										<p class="text-xs font-medium text-muted-foreground">New property details (from the document — edit if needed)</p>
										<fieldset>
											<legend class="mb-1 text-xs font-medium text-muted-foreground">
												How many rentals are at this address? <span class="text-[var(--m3c-error)]">*</span>
											</legend>
											<div class="grid gap-2 sm:grid-cols-2">
												<label class="cursor-pointer rounded-lg border p-3 transition-colors {newPropertyRentalStructure === 'SingleRental' ? 'border-primary bg-primary/10' : 'border-border hover:bg-muted/40'}">
													<input class="sr-only" type="radio" name="scan-new-property-rental-structure" value="SingleRental" bind:group={newPropertyRentalStructure} data-testid="scan-new-property-single-rental" disabled={reviewControlsDisabled} />
													<span class="block text-sm font-semibold">One rental</span>
													<span class="mt-1 block text-xs text-muted-foreground">One house, condo, townhome, or other rentable space.</span>
												</label>
												<label class="cursor-pointer rounded-lg border p-3 transition-colors {newPropertyRentalStructure === 'MultiRental' ? 'border-primary bg-primary/10' : 'border-border hover:bg-muted/40'}">
													<input class="sr-only" type="radio" name="scan-new-property-rental-structure" value="MultiRental" bind:group={newPropertyRentalStructure} data-testid="scan-new-property-multi-rental" disabled={reviewControlsDisabled} />
													<span class="block text-sm font-semibold">Multiple rentals</span>
													<span class="mt-1 block text-xs text-muted-foreground">A duplex, apartment building, or other address with separate rentals.</span>
												</label>
											</div>
										</fieldset>
										<Input data-testid="scan-new-property-name" placeholder="Property name (optional)" bind:value={newPropertyName} disabled={reviewControlsDisabled} />
										<Input data-testid="scan-new-property-address" placeholder="Street address" bind:value={newPropertyAddress} disabled={reviewControlsDisabled} />
										<div class="grid grid-cols-3 gap-2">
											<Input data-testid="scan-new-property-city" placeholder="City" bind:value={newPropertyCity} disabled={reviewControlsDisabled} />
											<Input data-testid="scan-new-property-state" placeholder="State" bind:value={newPropertyState} disabled={reviewControlsDisabled} />
											<Input data-testid="scan-new-property-postal" placeholder="ZIP" bind:value={newPropertyPostal} disabled={reviewControlsDisabled} />
										</div>
										<p class="text-[11px] text-muted-foreground">Enter a street address or a property name so the property can be created.</p>
									</div>

									<div>
										<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-lease-new-unit">
											Unit number <span class="text-[var(--m3c-error)]">*</span>
										</label>
										<Input id="scan-lease-new-unit" data-testid="scan-lease-new-unit" placeholder="e.g. 1 (single-family) or 2B" bind:value={newUnitNumber} disabled={reviewControlsDisabled} />
										<p class="mt-1 text-xs text-muted-foreground">A new unit with this number is created under the new property.</p>
									</div>
									<div class="grid grid-cols-3 gap-2" data-testid="scan-lease-new-unit-details">
										{#each LEASE_REVIEW_NEW_UNIT_DETAIL_FIELDS as unitField (unitField.name)}
											<div>
												<label class="mb-1 block text-xs font-medium text-muted-foreground" for="scan-{unitField.name}">
													{unitField.label}
												</label>
												<Input
													id="scan-{unitField.name}"
													data-testid="scan-field-{unitField.name}"
													type="number"
													inputmode="decimal"
													placeholder={unitField.placeholder}
													bind:value={editedFields[unitField.name]}
													disabled={reviewControlsDisabled}
												/>
											</div>
										{/each}
									</div>
								{:else}
									<div>
										<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-lease-unit-select">
											Which unit? <span class="text-[var(--m3c-error)]">*</span>
										</label>
										<Input
											aria-label="Search units"
											placeholder="Search units in this property"
											value={unitSearch}
											disabled={!selectedLeasePropertyId || reviewControlsDisabled}
											oninput={(event) => {
												unitSearch = (event.currentTarget as HTMLInputElement).value;
												unitSkip = 0;
											}}
										/>
										<Select.Root type="single" bind:value={selectedLeaseUnitId} disabled={!selectedLeasePropertyId || reviewControlsDisabled}>
											<Select.Trigger id="scan-lease-unit-select" data-testid="scan-lease-unit-select" class="w-full" disabled={!selectedLeasePropertyId || reviewControlsDisabled}>
												{selectedLeaseUnitLabel}
											</Select.Trigger>
											<Select.Content>
										{#if leaseUnitsQuery.data}
											{#each leaseUnitChoices as unit (unit.id)}
														<Select.Item value={String(unit.id)} label={`Unit ${unit.unitNumber}`}>
															Unit {unit.unitNumber} ({formatStatusLabel(unit.status)})
														</Select.Item>
													{/each}
												{/if}
											</Select.Content>
										</Select.Root>
										{#if !selectedLeasePropertyId}
											<p class="mt-1 text-xs text-muted-foreground">Pick a property first to see its units.</p>
										{:else if leaseUnitsQuery.isLoading}
											<p class="mt-1 text-xs text-muted-foreground">Loading units…</p>
										{:else if (leaseUnitsQuery.data?.items.length ?? 0) === 0}
											<p class="mt-1 text-xs text-[var(--warning)]">This property has no units yet — switch to "{newPropertyLabel}" above, or add a unit to this property first.</p>
										{/if}
										{#if leaseUnitsQuery.data}
											<div class="mt-2 flex items-center justify-between gap-2 text-xs text-muted-foreground">
												<span>{leaseUnitsQuery.data.totalCount === 0 ? 'No matching units' : `${unitSkip + 1}–${Math.min(unitSkip + CONTEXT_PAGE_SIZE, leaseUnitsQuery.data.totalCount)} of ${leaseUnitsQuery.data.totalCount}`}</span>
												<div class="flex gap-1">
													<Button type="button" variant="outline" size="sm" disabled={unitSkip === 0} onclick={() => (unitSkip = Math.max(0, unitSkip - CONTEXT_PAGE_SIZE))}>Previous</Button>
													<Button type="button" variant="outline" size="sm" disabled={unitSkip + CONTEXT_PAGE_SIZE >= leaseUnitsQuery.data.totalCount} onclick={() => (unitSkip += CONTEXT_PAGE_SIZE)}>Next</Button>
												</div>
											</div>
										{/if}
									</div>
								{/if}

								<div>
									<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-lease-tenant-select">
										Tenant <span class="font-normal text-muted-foreground">(optional)</span>
									</label>
									<Input
										aria-label="Search tenants"
										placeholder="Search tenants by name, email, or phone"
										value={tenantSearch}
										disabled={reviewControlsDisabled}
										oninput={(event) => {
											tenantSearch = (event.currentTarget as HTMLInputElement).value;
											tenantSkip = 0;
										}}
									/>
									<Select.Root type="single" bind:value={selectedTenantId} disabled={reviewControlsDisabled}>
										<Select.Trigger id="scan-lease-tenant-select" data-testid="scan-lease-tenant-select" class="w-full" disabled={reviewControlsDisabled}>
											{selectedTenantLabel}
										</Select.Trigger>
										<Select.Content>
											<Select.Item value={NO_TENANT} label={newTenantLabel}>{newTenantLabel}</Select.Item>
										{#if tenantsQuery.data}
											{#each tenantChoices as t (t.id)}
													<Select.Item value={String(t.id)} label={tenantLabel(t)}>
														{tenantLabel(t)}
													</Select.Item>
												{/each}
											{/if}
										</Select.Content>
									</Select.Root>
									<p class="mt-1 text-xs text-muted-foreground">
										Leave on "{newTenantLabel}" to use the name from the lease, or pick an existing tenant to match it.
									</p>
									{#if tenantsQuery.data}
										<div class="mt-2 flex items-center justify-between gap-2 text-xs text-muted-foreground">
											<span>{tenantsQuery.data.totalCount === 0 ? 'No matching tenants' : `${tenantSkip + 1}–${Math.min(tenantSkip + CONTEXT_PAGE_SIZE, tenantsQuery.data.totalCount)} of ${tenantsQuery.data.totalCount}`}</span>
											<div class="flex gap-1">
												<Button type="button" variant="outline" size="sm" disabled={tenantSkip === 0} onclick={() => (tenantSkip = Math.max(0, tenantSkip - CONTEXT_PAGE_SIZE))}>Previous</Button>
												<Button type="button" variant="outline" size="sm" disabled={tenantSkip + CONTEXT_PAGE_SIZE >= tenantsQuery.data.totalCount} onclick={() => (tenantSkip += CONTEXT_PAGE_SIZE)}>Next</Button>
											</div>
										</div>
									{/if}
								</div>
							</div>
							{/if}

							{#if !isTerminal}
								<div class="mb-5 rounded-md border border-border bg-muted/20 p-3">
									<LeaseScanSignatureChoice
										bind:reviewDisposition={leaseReviewDisposition}
										bind:documentTemplateId={leaseDocumentTemplateId}
										propertyId={isCreatingLeaseProperty ? 0 : Number(selectedLeasePropertyId) || 0}
										disabled={reviewControlsDisabled}
									/>
								</div>
								{#if leaseReviewDisposition === 'AlreadyFullySigned'}
									<div class="mb-5 rounded-md border border-border bg-muted/20 p-3">
										<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-possession-given-date">
											Possession given
										</label>
										<DatePicker
											id="scan-possession-given-date"
											bind:value={possessionGivenOn}
											min={editedFields['start_date'] || undefined}
											testid="scan-possession-given-date"
										/>
										<p class="mt-1 text-xs text-muted-foreground">
											Required when the signed lease term has started and the tenant already received possession.
										</p>
									</div>
								{/if}
								<div class="mb-5 grid gap-3 rounded-md border border-border bg-muted/20 p-3 sm:grid-cols-2">
									<div class={rentTrackingStartMode === 'CustomCutoffDate' ? '' : 'sm:col-span-2'}>
										<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-rent-tracking-mode">
											Begin rent charges
										</label>
										<Select.Root
											type="single"
											bind:value={rentTrackingStartMode}
											onValueChange={() => {
												if (rentTrackingStartMode !== 'CustomCutoffDate') rentTrackingStartOn = '';
											}}
											disabled={reviewControlsDisabled}
										>
											<Select.Trigger id="scan-rent-tracking-mode" data-testid="scan-rent-tracking-mode" class="w-full">
												{rentTrackingStartMode === 'ForwardOnly'
													? 'Start from the current date'
													: rentTrackingStartMode === 'BackfillFromLeaseStart'
														? 'Add past rent from the lease start'
														: 'Start from a custom date'}
											</Select.Trigger>
											<Select.Content>
												<Select.Item value="ForwardOnly" label="Start from the current date">Start from the current date</Select.Item>
												<Select.Item value="BackfillFromLeaseStart" label="Add past rent from the lease start">Add past rent from the lease start</Select.Item>
												<Select.Item value="CustomCutoffDate" label="Start from a custom date">Start from a custom date</Select.Item>
											</Select.Content>
										</Select.Root>
										<p class="mt-1 text-xs text-muted-foreground">
											This decides whether older rent periods are posted when the lease is imported.
										</p>
									</div>
									{#if rentTrackingStartMode === 'CustomCutoffDate'}
										<div>
											<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-rent-tracking-date">
												Custom start date
											</label>
											<DatePicker
												id="scan-rent-tracking-date"
												bind:value={rentTrackingStartOn}
												min={editedFields['start_date'] || undefined}
												testid="scan-rent-tracking-date"
											/>
										</div>
									{/if}
								</div>
							{/if}

						<!-- Lease terms — editable, mapped from the extracted fields -->
						<div class="mb-5" data-testid="scan-lease-terms">
							<h2 class="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">Lease terms</h2>
							<div class="grid grid-cols-2 gap-x-4 gap-y-3">
								{#each LEASE_TERM_FIELDS as term (term.name)}
									{@const field = data.fields.find((f) => f.name === term.name)}
									{@const level = field ? confidenceLevel(field.confidence) : 'high'}
									<div>
										<div class="mb-1 flex items-center justify-between">
											<label class="text-xs font-medium {level === 'medium' ? 'text-muted-foreground' : 'text-foreground'}" for="lease-term-{term.name}">
												{term.label}
											</label>
											{#if field && level !== 'high'}
												<span class="text-xs {level === 'low' ? 'text-[var(--m3c-error)]' : 'text-muted-foreground'}">
													{confidenceLabel(field.confidence)}
												</span>
											{/if}
										</div>
										{#if term.type === 'date'}
											<DatePicker
												id="lease-term-{term.name}"
												testid="scan-field-{term.name}"
												bind:value={editedFields[term.name]}
												bind:invalid={scanDateInvalid[term.name]}
												disabled={reviewControlsDisabled}
											/>
										{:else}
											<Input
												id="lease-term-{term.name}"
												data-testid="scan-field-{term.name}"
												type={term.type}
												bind:value={editedFields[term.name]}
												disabled={reviewControlsDisabled}
											/>
										{/if}
									</div>
								{/each}
							</div>
						</div>
					{:else if isApplication}
						<!-- Applicant review — a single editable applicant form (Payment/Expense model, NOT the
						     4-step guided lease flow). Confirming creates a RentalApplication on /applications. -->
						{#if !isTerminal}
							<div class="mb-5 rounded-md border border-border bg-muted/30 p-3" data-testid="scan-application-destination">
								<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-application-property-select">
									Which property is this application for? <span class="font-normal text-muted-foreground">(optional)</span>
								</label>
								<Input
									aria-label="Search application properties"
									placeholder="Search properties by name or address"
									value={propertySearch}
									disabled={reviewControlsDisabled}
									oninput={(event) => {
										propertySearch = (event.currentTarget as HTMLInputElement).value;
										propertySkip = 0;
									}}
								/>
								<Select.Root type="single" bind:value={selectedPropertyId} disabled={reviewControlsDisabled}>
									<Select.Trigger id="scan-application-property-select" class="w-full" disabled={reviewControlsDisabled}>
										{selectedPropertyLabel}
									</Select.Trigger>
									<Select.Content>
										<Select.Item value={NO_PROPERTY} label="— No property —">— No property —</Select.Item>
										{#each propertyChoices as prop (prop.id)}
											<Select.Item value={String(prop.id)} label={prop.name}>{prop.name}</Select.Item>
										{/each}
									</Select.Content>
								</Select.Root>
								{#if propertiesQuery.isLoading}
									<p class="mt-1 text-xs text-muted-foreground">Loading properties…</p>
								{:else if propertiesQuery.data}
									<div class="mt-2 flex items-center justify-between gap-2 text-xs text-muted-foreground">
										<span>{propertiesQuery.data.totalCount === 0 ? 'No matching properties' : `${propertySkip + 1}–${Math.min(propertySkip + CONTEXT_PAGE_SIZE, propertiesQuery.data.totalCount)} of ${propertiesQuery.data.totalCount}`}</span>
										<div class="flex gap-1">
											<Button type="button" variant="outline" size="sm" disabled={propertySkip === 0} onclick={() => (propertySkip = Math.max(0, propertySkip - CONTEXT_PAGE_SIZE))}>Previous</Button>
											<Button type="button" variant="outline" size="sm" disabled={propertySkip + CONTEXT_PAGE_SIZE >= propertiesQuery.data.totalCount} onclick={() => (propertySkip += CONTEXT_PAGE_SIZE)}>Next</Button>
										</div>
									</div>
								{/if}
								<p class="mt-2 text-xs text-muted-foreground">Changing the property clears any inherited unit from the previous property when saved.</p>
							</div>
						{/if}
						<div class="mb-5" data-testid="scan-application-fields">
							<h2 class="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">Applicant details</h2>
							<div class="grid grid-cols-2 gap-x-4 gap-y-3">
								{#each APPLICATION_FIELDS as appField (appField.name)}
									{@const field = data.fields.find((f) => f.name === appField.name)}
									{@const level = field ? confidenceLevel(field.confidence) : 'high'}
									{@const required = appField.name === 'first_name' || appField.name === 'last_name'}
									<div class="{appField.name === 'current_address' || appField.name === 'applying_for' ? 'col-span-2' : ''}">
										<div class="mb-1 flex items-center justify-between">
											<label class="text-xs font-medium {level === 'medium' ? 'text-muted-foreground' : 'text-foreground'}" for="application-{appField.name}">
												{appField.label}{#if required}<span class="text-[var(--m3c-error)]"> *</span>{/if}
											</label>
											{#if field && level !== 'high'}
												<span class="text-xs {level === 'low' ? 'text-[var(--m3c-error)]' : 'text-muted-foreground'}">
													{confidenceLabel(field.confidence)}
												</span>
											{/if}
										</div>
										{#if appField.type === 'date'}
											<DatePicker
												id="application-{appField.name}"
												testid="scan-field-{appField.name}"
												bind:value={editedFields[appField.name]}
												bind:invalid={scanDateInvalid[appField.name]}
												disabled={reviewControlsDisabled}
											/>
										{:else}
											<Input
												id="application-{appField.name}"
												data-testid="scan-field-{appField.name}"
												type={appField.type}
												bind:value={editedFields[appField.name]}
												disabled={reviewControlsDisabled}
											/>
										{/if}
									</div>
								{/each}
							</div>
						</div>
					{:else if isLoan}
						<!-- Mortgage documents can either match an existing scheduled payment or create a new loan.
						     Statement scans default to matching so confirming never silently duplicates a mortgage. -->
						{#if !isTerminal}
							<div class="mb-4 grid grid-cols-2 rounded-md border border-border bg-muted p-1" data-testid="scan-loan-review-mode">
								<button
									type="button"
									class="rounded px-3 py-2 text-sm font-medium {loanReviewMode === 'match' ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground'}"
									disabled={reviewControlsDisabled}
									onclick={() => {
										loanReviewMode = 'match';
									}}
								>
									Match existing payment
								</button>
								<button
									type="button"
									class="rounded px-3 py-2 text-sm font-medium {loanReviewMode === 'create' ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground'}"
									disabled={reviewControlsDisabled}
									onclick={() => {
										loanReviewMode = 'create';
										selectedExistingLoanId = '';
										selectedExistingLoanPaymentId = '';
									}}
								>
									Add new loan
								</button>
							</div>
							<div class="mb-5 rounded-md border border-border bg-muted/30 p-3">
								<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-loan-property-select">
									Which property is this loan for? <span class="text-[var(--m3c-error)]">*</span>
								</label>
								<Input
									aria-label="Search properties"
									placeholder="Search properties by name or address"
									value={propertySearch}
									disabled={reviewControlsDisabled}
									oninput={(event) => {
										propertySearch = (event.currentTarget as HTMLInputElement).value;
										propertySkip = 0;
									}}
								/>
								<Select.Root type="single" value={selectedPropertyId} onValueChange={selectLoanPropertyId} disabled={reviewControlsDisabled}>
									<Select.Trigger id="scan-loan-property-select" data-testid="scan-loan-property-select" class="w-full" disabled={reviewControlsDisabled}>
										{selectedPropertyLabel}
									</Select.Trigger>
									<Select.Content>
										<Select.Item value={NO_PROPERTY} label="— Select a property —">— Select a property —</Select.Item>
										{#if propertiesQuery.data}
											{#each propertyChoices as prop (prop.id)}
												<Select.Item value={String(prop.id)} label={prop.name}>
													{prop.name}
												</Select.Item>
											{/each}
										{/if}
									</Select.Content>
								</Select.Root>
								{#if propertiesQuery.isLoading}
									<p class="mt-1 text-xs text-muted-foreground">Loading properties…</p>
								{:else if selectedPropertyId === NO_PROPERTY}
									<p class="mt-1 text-xs text-[var(--warning)]">Select a property to continue.</p>
								{/if}
								{#if propertiesQuery.data}
									<div class="mt-2 flex items-center justify-between gap-2 text-xs text-muted-foreground">
										<span>{propertiesQuery.data.totalCount === 0 ? 'No matching properties' : `${propertySkip + 1}–${Math.min(propertySkip + CONTEXT_PAGE_SIZE, propertiesQuery.data.totalCount)} of ${propertiesQuery.data.totalCount}`}</span>
										<div class="flex gap-1">
											<Button type="button" variant="outline" size="sm" disabled={propertySkip === 0} onclick={() => (propertySkip = Math.max(0, propertySkip - CONTEXT_PAGE_SIZE))}>Previous</Button>
											<Button type="button" variant="outline" size="sm" disabled={propertySkip + CONTEXT_PAGE_SIZE >= propertiesQuery.data.totalCount} onclick={() => (propertySkip += CONTEXT_PAGE_SIZE)}>Next</Button>
										</div>
									</div>
								{/if}
							</div>
						{/if}
						{#if loanReviewMode === 'match' && !isTerminal}
							<div class="mb-5 space-y-4 rounded-md border border-border p-4" data-testid="scan-loan-payment-match">
								<div>
									<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-existing-loan-select">
										Existing loan <span class="text-[var(--m3c-error)]">*</span>
									</label>
									<Select.Root
										type="single"
										value={selectedExistingLoanId}
										onValueChange={selectExistingLoanId}
										disabled={reviewControlsDisabled || !selectedLoanPropertyIdNumber}
									>
										<Select.Trigger id="scan-existing-loan-select" data-testid="scan-existing-loan-select" class="w-full">
											{selectedExistingLoan ? `${selectedExistingLoan.lender} · ${formatUsd(selectedExistingLoan.currentBalance)} balance` : '— Select an existing loan —'}
										</Select.Trigger>
										<Select.Content>
											{#each existingLoanChoices as loan (loan.id)}
												<Select.Item value={String(loan.id)} label={loan.lender}>
													{loan.lender} · {formatUsd(loan.currentBalance)} balance
												</Select.Item>
											{/each}
										</Select.Content>
									</Select.Root>
									{#if existingLoansQuery.isLoading}
										<p class="mt-1 text-xs text-muted-foreground">Loading loans…</p>
									{:else if selectedLoanPropertyIdNumber && existingLoansQuery.data?.totalCount === 0}
										<p class="mt-1 text-xs text-[var(--warning)]">No loans exist for this property. Choose “Add new loan” for an origination document.</p>
									{/if}
								</div>
								<div>
									<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-existing-loan-payment-select">
										Scheduled payment <span class="text-[var(--m3c-error)]">*</span>
									</label>
									<Select.Root
										type="single"
										bind:value={selectedExistingLoanPaymentId}
										disabled={reviewControlsDisabled || !selectedExistingLoanId}
									>
										<Select.Trigger id="scan-existing-loan-payment-select" data-testid="scan-existing-loan-payment-select" class="w-full">
											{selectedExistingLoanPayment ? loanPaymentLabel(selectedExistingLoanPayment) : '— Select the statement period —'}
										</Select.Trigger>
										<Select.Content>
											{#each existingLoanPaymentChoices as payment (payment.id)}
												<Select.Item value={String(payment.id)} label={loanPaymentLabel(payment)}>
													{loanPaymentLabel(payment)}
												</Select.Item>
											{/each}
										</Select.Content>
									</Select.Root>
									{#if existingLoanPaymentsQuery.isLoading}
										<p class="mt-1 text-xs text-muted-foreground">Loading scheduled payments…</p>
									{:else if selectedExistingLoanId && existingLoanPaymentChoices.length === 0}
										<p class="mt-1 text-xs text-[var(--warning)]">This loan has no scheduled payments available to match.</p>
									{/if}
								</div>
								{#if selectedExistingLoanPayment}
									<div class="space-y-2 rounded bg-muted/50 p-3 text-sm">
										<p class="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Scheduled values</p>
										<div class="grid grid-cols-2 gap-2 sm:grid-cols-5">
											<span>Principal {formatUsd(selectedExistingLoanPayment.principalAmount)}</span>
											<span>Interest {formatUsd(selectedExistingLoanPayment.interestAmount)}</span>
											<span>Escrow {formatUsd(selectedExistingLoanPayment.escrowAmount)}</span>
											<span>Total {formatUsd(selectedExistingLoanPayment.totalAmount)}</span>
											<span>Balance {formatUsd(selectedExistingLoanPayment.balanceAfter)}</span>
										</div>
									</div>
								{/if}
								<div>
									<h2 class="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">Statement values</h2>
									<div class="grid grid-cols-2 gap-x-4 gap-y-3">
										{#each LOAN_STATEMENT_FIELDS as statementField (statementField.name)}
											{@const field = data.fields.find((f) => f.name === statementField.name)}
											{@const level = field ? confidenceLevel(field.confidence) : 'high'}
											<div class="{statementField.name === 'current_balance' || statementField.name === 'statement_effective_date' ? 'col-span-2 sm:col-span-1' : ''}">
												<div class="mb-1 flex items-center justify-between">
													<label class="text-xs font-medium {level === 'medium' ? 'text-muted-foreground' : 'text-foreground'}" for="loan-statement-{statementField.name}">
														{statementField.label}{#if statementField.required}<span class="text-[var(--m3c-error)]"> *</span>{/if}
													</label>
													{#if field && level !== 'high'}
														<span class="text-xs {level === 'low' ? 'text-[var(--m3c-error)]' : 'text-muted-foreground'}">
															{confidenceLabel(field.confidence)}
														</span>
													{/if}
												</div>
												{#if statementField.type === 'date'}
													<DatePicker
														id="loan-statement-{statementField.name}"
														testid="scan-field-{statementField.name}"
														bind:value={editedFields[statementField.name]}
														bind:invalid={scanDateInvalid[statementField.name]}
														disabled={reviewControlsDisabled}
													/>
												{:else}
													<Input
														id="loan-statement-{statementField.name}"
														data-testid="scan-field-{statementField.name}"
														type={statementField.type}
														bind:value={editedFields[statementField.name]}
														disabled={reviewControlsDisabled}
													/>
												{/if}
											</div>
										{/each}
									</div>
								</div>
							</div>
						{:else}
						<div class="mb-5" data-testid="scan-loan-fields">
							<h2 class="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">Loan details</h2>
							<div class="grid grid-cols-2 gap-x-4 gap-y-3">
								{#each LOAN_FIELDS as loanField (loanField.name)}
									{@const field = data.fields.find((f) => f.name === loanField.name)}
									{@const level = field ? confidenceLevel(field.confidence) : 'high'}
									{@const required = loanField.name === 'lender'}
									<div class="{loanField.name === 'lender' ? 'col-span-2' : ''}">
										<div class="mb-1 flex items-center justify-between">
											<label class="text-xs font-medium {level === 'medium' ? 'text-muted-foreground' : 'text-foreground'}" for="loan-{loanField.name}">
												{loanField.label}{#if required}<span class="text-[var(--m3c-error)]"> *</span>{/if}
											</label>
											{#if field && level !== 'high'}
												<span class="text-xs {level === 'low' ? 'text-[var(--m3c-error)]' : 'text-muted-foreground'}">
													{confidenceLabel(field.confidence)}
												</span>
											{/if}
										</div>
										{#if loanField.type === 'date'}
											<DatePicker
												id="loan-{loanField.name}"
												testid="scan-field-{loanField.name}"
												bind:value={editedFields[loanField.name]}
												bind:invalid={scanDateInvalid[loanField.name]}
												disabled={reviewControlsDisabled}
											/>
										{:else}
											<Input
												id="loan-{loanField.name}"
												data-testid="scan-field-{loanField.name}"
												type={loanField.type}
												bind:value={editedFields[loanField.name]}
												disabled={reviewControlsDisabled}
											/>
										{/if}
									</div>
								{/each}
							</div>
							<div class="mt-3 space-y-2">
								<label class="flex items-center gap-2 text-sm">
									<input type="checkbox" bind:checked={loanEscrowCoversTaxes} data-testid="scan-loan-escrow-taxes" disabled={reviewControlsDisabled} />
									Escrow covers property taxes
								</label>
								<label class="flex items-center gap-2 text-sm">
									<input type="checkbox" bind:checked={loanEscrowCoversInsurance} data-testid="scan-loan-escrow-insurance" disabled={reviewControlsDisabled} />
									Escrow covers insurance
								</label>
							</div>
							<div class="mt-3">
								<label class="mb-1 block text-xs font-medium text-muted-foreground" for="loan-notes">Notes</label>
								<Input id="loan-notes" data-testid="scan-field-notes" type="text" bind:value={editedFields['notes']} disabled={reviewControlsDisabled} />
							</div>
						</div>
						{/if}
					{:else}
						<!-- Property selector for Expense/WorkOrder drafts (sends propertyId override) -->
						{#if !isTerminal}
							<div class="mb-5 rounded-md border border-border bg-muted/30 p-3">
								<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-property-select">
									Which property is this for? {#if isWorkOrder}<span class="text-[var(--m3c-error)]">*</span>{:else}<span class="font-normal text-muted-foreground">(optional)</span>{/if}
								</label>
								<Input
									aria-label="Search properties"
									placeholder="Search properties by name or address"
									value={propertySearch}
									disabled={reviewControlsDisabled}
									oninput={(event) => {
										propertySearch = (event.currentTarget as HTMLInputElement).value;
										propertySkip = 0;
									}}
								/>
								<Select.Root type="single" value={selectedPropertyId} onValueChange={selectContextPropertyId} disabled={reviewControlsDisabled}>
									<Select.Trigger id="scan-property-select" data-testid="scan-property-select" class="w-full" disabled={reviewControlsDisabled}>
										{selectedPropertyLabel}
									</Select.Trigger>
									<Select.Content>
										<Select.Item value={NO_PROPERTY} label="— No property —">— No property —</Select.Item>
										{#if propertiesQuery.data}
											{#each propertyChoices as prop (prop.id)}
												<Select.Item value={String(prop.id)} label={prop.name}>
													{prop.name}
												</Select.Item>
											{/each}
										{/if}
									</Select.Content>
								</Select.Root>
								{#if propertiesQuery.isLoading}
									<p class="mt-1 text-xs text-muted-foreground">Loading properties…</p>
								{/if}
								{#if isWorkOrder && selectedPropertyId === NO_PROPERTY}
									<p class="mt-1 text-xs text-[var(--warning)]">Select a property to create this work order.</p>
								{/if}
								{#if propertiesQuery.data}
									<div class="mt-2 flex items-center justify-between gap-2 text-xs text-muted-foreground">
										<span>{propertiesQuery.data.totalCount === 0 ? 'No matching properties' : `${propertySkip + 1}–${Math.min(propertySkip + CONTEXT_PAGE_SIZE, propertiesQuery.data.totalCount)} of ${propertiesQuery.data.totalCount}`}</span>
										<div class="flex gap-1">
											<Button type="button" variant="outline" size="sm" disabled={propertySkip === 0} onclick={() => (propertySkip = Math.max(0, propertySkip - CONTEXT_PAGE_SIZE))}>Previous</Button>
											<Button type="button" variant="outline" size="sm" disabled={propertySkip + CONTEXT_PAGE_SIZE >= propertiesQuery.data.totalCount} onclick={() => (propertySkip += CONTEXT_PAGE_SIZE)}>Next</Button>
										</div>
									</div>
								{/if}
							</div>
							{#if isExpense}
								<div class="mb-5 grid gap-3 rounded-md border border-border bg-muted/30 p-3 md:grid-cols-2" data-testid="scan-expense-work-order-scope">
									<div>
										<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-expense-unit-select">
											Unit <span class="font-normal text-muted-foreground">(optional)</span>
										</label>
										<Input
											aria-label="Search units for expense"
											placeholder={selectedPropertyId === NO_PROPERTY ? 'Select a property first' : 'Search units in this property'}
											value={unitSearch}
											disabled={selectedPropertyId === NO_PROPERTY || reviewControlsDisabled}
											oninput={(event) => {
												unitSearch = (event.currentTarget as HTMLInputElement).value;
												unitSkip = 0;
											}}
										/>
										<Select.Root
											type="single"
											value={selectedExpenseUnitId}
											onValueChange={selectExpenseUnitId}
											disabled={selectedPropertyId === NO_PROPERTY || reviewControlsDisabled}
										>
											<Select.Trigger id="scan-expense-unit-select" data-testid="scan-expense-unit-select" class="w-full" disabled={selectedPropertyId === NO_PROPERTY || reviewControlsDisabled}>
												{selectedExpenseUnitLabel}
											</Select.Trigger>
											<Select.Content>
												<Select.Item value={NO_UNIT} label="— No unit —">— No unit —</Select.Item>
												{#if expenseUnitsQuery.data}
													{#each expenseUnitChoices as unit (unit.id)}
														<Select.Item value={String(unit.id)} label={`Unit ${unit.unitNumber}`}>
															Unit {unit.unitNumber}
														</Select.Item>
													{/each}
												{/if}
											</Select.Content>
										</Select.Root>
										{#if selectedPropertyId === NO_PROPERTY}
											<p class="mt-1 text-xs text-muted-foreground">Pick a property to narrow units and work orders.</p>
										{:else if expenseUnitsQuery.isLoading}
											<p class="mt-1 text-xs text-muted-foreground">Loading units…</p>
										{:else if expenseUnitsQuery.data}
											<div class="mt-2 flex items-center justify-between gap-2 text-xs text-muted-foreground">
												<span>{expenseUnitsQuery.data.totalCount === 0 ? 'No matching units' : `${unitSkip + 1}–${Math.min(unitSkip + CONTEXT_PAGE_SIZE, expenseUnitsQuery.data.totalCount)} of ${expenseUnitsQuery.data.totalCount}`}</span>
												<div class="flex gap-1">
													<Button type="button" variant="outline" size="sm" disabled={unitSkip === 0} onclick={() => (unitSkip = Math.max(0, unitSkip - CONTEXT_PAGE_SIZE))}>Previous</Button>
													<Button type="button" variant="outline" size="sm" disabled={unitSkip + CONTEXT_PAGE_SIZE >= expenseUnitsQuery.data.totalCount} onclick={() => (unitSkip += CONTEXT_PAGE_SIZE)}>Next</Button>
												</div>
											</div>
										{/if}
									</div>

									<div>
										<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-expense-work-order-select">
											Work order <span class="font-normal text-muted-foreground">(optional)</span>
										</label>
										<Input
											aria-label="Search work orders for expense"
											placeholder="Search work orders"
											value={workOrderSearch}
											disabled={reviewControlsDisabled}
											oninput={(event) => {
												workOrderSearch = (event.currentTarget as HTMLInputElement).value;
												workOrderSkip = 0;
											}}
										/>
										<Select.Root
											type="single"
											value={selectedExpenseWorkOrderId}
											onValueChange={selectExpenseWorkOrder}
											disabled={reviewControlsDisabled}
										>
											<Select.Trigger id="scan-expense-work-order-select" data-testid="scan-expense-work-order-select" class="w-full" disabled={reviewControlsDisabled}>
												{selectedExpenseWorkOrderLabel}
											</Select.Trigger>
											<Select.Content>
												<Select.Item value={NO_WORK_ORDER} label="— No work order —">— No work order —</Select.Item>
												{#if expenseWorkOrdersQuery.data}
													{#each expenseWorkOrderChoices as workOrder (workOrder.id)}
														<Select.Item value={String(workOrder.id)} label={expenseWorkOrderLabel(workOrder)}>
															{expenseWorkOrderLabel(workOrder)}
														</Select.Item>
													{/each}
												{/if}
											</Select.Content>
										</Select.Root>
										<p class="mt-1 text-xs text-muted-foreground">
											Choosing a work order also chooses its property{selectedExpenseUnitId !== NO_UNIT ? ' and unit' : ''}.
										</p>
										{#if expenseWorkOrdersQuery.isLoading}
											<p class="mt-1 text-xs text-muted-foreground">Loading work orders…</p>
										{:else if expenseWorkOrdersQuery.data}
											<div class="mt-2 flex items-center justify-between gap-2 text-xs text-muted-foreground">
												<span>{expenseWorkOrdersQuery.data.totalCount === 0 ? 'No matching work orders' : `${workOrderSkip + 1}–${Math.min(workOrderSkip + CONTEXT_PAGE_SIZE, expenseWorkOrdersQuery.data.totalCount)} of ${expenseWorkOrdersQuery.data.totalCount}`}</span>
												<div class="flex gap-1">
													<Button type="button" variant="outline" size="sm" disabled={workOrderSkip === 0} onclick={() => (workOrderSkip = Math.max(0, workOrderSkip - CONTEXT_PAGE_SIZE))}>Previous</Button>
													<Button type="button" variant="outline" size="sm" disabled={workOrderSkip + CONTEXT_PAGE_SIZE >= expenseWorkOrdersQuery.data.totalCount} onclick={() => (workOrderSkip += CONTEXT_PAGE_SIZE)}>Next</Button>
												</div>
											</div>
										{/if}
									</div>
								</div>
							{/if}
							{/if}
					{/if}
					{#if isLease || isApplication || isLoan}
						<!-- Lease terms / applicant / loan details are rendered above; the generic extracted-field
						     groups are suppressed so those fields aren't duplicated. -->
					{:else if data.fields.length === 0}
						<p class="text-sm text-muted-foreground">
							{#if isProcessing}
								Fields will appear once extraction completes.
							{:else}
								No fields extracted. Enter values manually below.
							{/if}
						</p>
						<!-- Manual entry fallback: provide common fields -->
						<div class="mt-4 space-y-3">
							<!-- Suppressed while processing so fields don't appear then get replaced by the full extracted set -->
							{#each (isProcessing ? [] : ['vendor_name', 'total', 'subtotal', 'tax', 'transaction_date', 'category', 'payment_method', 'notes']) as fieldName}
								<div>
									<label class="mb-1 block text-xs font-medium text-muted-foreground" for="field-{fieldName}">
										{fieldDisplayLabel(fieldName)}
									</label>
									{#if fieldName === 'category'}
										<!-- Category dropdown — friendly labels, enum value submitted -->
										<Select.Root type="single" bind:value={editedFields[fieldName]} disabled={reviewControlsDisabled}>
											<Select.Trigger id="field-{fieldName}" data-testid="scan-field-{fieldName}" class="w-full" disabled={reviewControlsDisabled}>
												{scanCategoryLabel(editedFields[fieldName], data.targetEntityType)}
											</Select.Trigger>
											<Select.Content>
												{#each scanCategoryOptionsForTarget(data.targetEntityType) as cat}
													<Select.Item value={cat.value} label={cat.label}>{cat.label}</Select.Item>
												{/each}
											</Select.Content>
										</Select.Root>
									{:else if isScanDateField(fieldName)}
										<DatePicker
											id="field-{fieldName}"
											testid="scan-field-{fieldName}"
											bind:value={editedFields[fieldName]}
											bind:invalid={scanDateInvalid[fieldName]}
											disabled={reviewControlsDisabled}
										/>
									{:else}
										<Input
											id="field-{fieldName}"
											data-testid="scan-field-{fieldName}"
											type="text"
											bind:value={editedFields[fieldName]}
											disabled={reviewControlsDisabled}
										/>
									{/if}
								</div>
							{/each}
						</div>
					{:else}
						{@const groups = buildScanReviewFieldGroups(data.fields, data.targetEntityType)}
						<div class="space-y-6">
							{#each groups as group}
								<section>
									<h2 class="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">{group.label}</h2>
									<div class="grid grid-cols-2 gap-x-4 gap-y-3">
										{#each group.fields as field (field.name)}
											{@const level = confidenceLevel(field.confidence)}
											<div class="{field.name === 'vendor_address' || field.name === 'notes' ? 'col-span-2' : ''}">
												<div class="mb-1 flex items-center justify-between">
													<label
														class="text-xs font-medium {level === 'medium' ? 'text-muted-foreground' : 'text-foreground'}"
														for="field-{field.name}"
													>
														{fieldDisplayLabel(field.name)}
													</label>
													{#if level !== 'high'}
														<span class="text-xs {level === 'low' ? 'text-[var(--m3c-error)]' : 'text-muted-foreground'}">
															{confidenceLabel(field.confidence)}
														</span>
													{/if}
												</div>
												{#if field.name === 'category'}
													<!-- Category dropdown — friendly labels, enum value submitted -->
													<Select.Root type="single" bind:value={editedFields[field.name]} disabled={reviewControlsDisabled}>
														<Select.Trigger id="field-{field.name}" data-testid="scan-field-{field.name}" class="w-full" disabled={reviewControlsDisabled}>
															{scanCategoryLabel(editedFields[field.name], data.targetEntityType)}
														</Select.Trigger>
														<Select.Content>
															{#each scanCategoryOptionsForTarget(data.targetEntityType) as cat}
																<Select.Item value={cat.value} label={cat.label}>{cat.label}</Select.Item>
															{/each}
														</Select.Content>
													</Select.Root>
												{:else if isScanDateField(field.name)}
													<DatePicker
														id="field-{field.name}"
														testid="scan-field-{field.name}"
														bind:value={editedFields[field.name]}
														bind:invalid={scanDateInvalid[field.name]}
														disabled={reviewControlsDisabled}
													/>
												{:else}
													<!-- Raw <input> needed here to support the use:focusFirstLow action (actions cannot be placed on components) -->
													<input
														id="field-{field.name}"
														data-testid="scan-field-{field.name}"
														type="text"
														bind:value={editedFields[field.name]}
														disabled={reviewControlsDisabled}
														class="border-input bg-background selection:bg-primary dark:bg-input/30 selection:text-primary-foreground ring-offset-background placeholder:text-muted-foreground flex h-9 w-full min-w-0 rounded-md border px-3 py-1 text-base shadow-xs transition-[color,box-shadow] outline-none disabled:cursor-not-allowed disabled:opacity-50 md:text-sm focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px] {fieldInputClass(field)}"
														use:focusFirstLow={!reviewControlsDisabled && level === 'low' && isFirstLowField(data.fields, field)}
													/>
												{/if}
											</div>
										{/each}
									</div>
								</section>
							{/each}

							<!-- Line items — editable so the reviewer can fix a wrong description / qty /
							     price, or add & remove rows, before the expense is created. Scoped to
							     expense drafts because only the expense confirm path persists line items. -->
							{#if isExpense}
								<section data-testid="scan-line-items">
									<div class="mb-2 flex items-center justify-between">
										<h2 class="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Line Items</h2>
										<Button
											type="button"
											variant="ghost"
											size="sm"
											class="h-7 gap-1 px-2 text-xs"
											data-testid="scan-line-item-add"
											onclick={addLineItem}
											disabled={reviewControlsDisabled}
										>
											+ Add line item
										</Button>
									</div>
									{#if editedLineItems.length === 0}
										<p class="text-sm text-muted-foreground">
											No line items.
											<button type="button" class="text-accent underline-offset-2 hover:underline disabled:pointer-events-none disabled:opacity-50" onclick={addLineItem} disabled={reviewControlsDisabled}>Add one</button>
											if the receipt itemizes charges.
										</p>
									{:else}
										<div class="overflow-hidden rounded-md border border-border">
											<Table.Root>
												<Table.Header>
													<Table.Row class="bg-muted/40">
														<Table.Head class="px-3 py-2 text-xs">Description</Table.Head>
														<Table.Head class="w-16 px-2 py-2 text-right text-xs">Qty</Table.Head>
														<Table.Head class="w-24 px-2 py-2 text-right text-xs">Unit Price</Table.Head>
														<Table.Head class="w-24 px-2 py-2 text-right text-xs">Amount</Table.Head>
														<Table.Head class="w-9 px-1 py-2"><span class="sr-only">Remove</span></Table.Head>
													</Table.Row>
												</Table.Header>
												<Table.Body>
													{#each editedLineItems as item, i (item.key)}
														<Table.Row class={i % 2 === 1 ? 'bg-muted/20' : ''}>
															<Table.Cell class="px-2 py-1.5">
																<Input
																	type="text"
																	placeholder="Description"
																	bind:value={item.description}
																	data-testid="scan-line-item-description-{i}"
																	class="h-8 text-sm"
																	disabled={reviewControlsDisabled}
																/>
															</Table.Cell>
															<Table.Cell class="px-2 py-1.5">
																<Input
																	type="text"
																	inputmode="decimal"
																	bind:value={item.quantity}
																	data-testid="scan-line-item-quantity-{i}"
																	class="h-8 text-right font-mono text-sm tabular-nums"
																	disabled={reviewControlsDisabled}
																/>
															</Table.Cell>
															<Table.Cell class="px-2 py-1.5">
																<Input
																	type="text"
																	inputmode="decimal"
																	bind:value={item.unit_price}
																	data-testid="scan-line-item-unit-price-{i}"
																	class="h-8 text-right font-mono text-sm tabular-nums"
																	disabled={reviewControlsDisabled}
																/>
															</Table.Cell>
															<Table.Cell class="px-2 py-1.5">
																<Input
																	type="text"
																	inputmode="decimal"
																	bind:value={item.amount}
																	data-testid="scan-line-item-amount-{i}"
																	class="h-8 text-right font-mono text-sm tabular-nums"
																	disabled={reviewControlsDisabled}
																/>
															</Table.Cell>
															<Table.Cell class="px-1 py-1.5 text-center">
																<button
																	type="button"
																	onclick={() => removeLineItem(item.key)}
																	data-testid="scan-line-item-remove-{i}"
																	class="text-muted-foreground transition-colors hover:text-destructive disabled:cursor-not-allowed disabled:opacity-50"
																	disabled={reviewControlsDisabled}
																	aria-label="Remove line item {i + 1}"
																>
																	<X class="h-4 w-4" />
																</button>
															</Table.Cell>
														</Table.Row>
													{/each}
												</Table.Body>
											</Table.Root>
										</div>
										<!-- Running total echo + a soft, non-blocking reconcile hint vs the subtotal. -->
										<div class="mt-2 flex items-center justify-between px-1 text-xs">
											<span class="text-muted-foreground">Line items total</span>
											<span
												class="font-mono tabular-nums {lineItemsMismatch ? 'text-[var(--warning)]' : 'text-foreground'}"
												data-testid="scan-line-items-total"
											>${formatMoney(lineItemsTotal)}</span>
										</div>
										{#if lineItemsMismatch}
											<p class="mt-1 px-1 text-xs text-[var(--warning)]" data-testid="scan-line-items-mismatch">
												These rows add up to ${formatMoney(lineItemsTotal)}, but the subtotal is ${formatMoney(subtotalAmount)}.
												That can be fine (tax, tip, fees) — just double-check before confirming.
											</p>
										{/if}
									{/if}
								</section>
							{/if}
						</div>
					{/if}
				</Card.Content>

				<!-- Paid / Unpaid toggle — hidden for Payment, WorkOrder, Lease, Application, and Loan drafts -->
				{#if !isPayment && !isWorkOrder && !isLease && !isApplication && !isLoan && !isTerminal}
				<div class="border-t border-border px-4 py-3" data-testid="scan-paid-toggle">
					<span class="mb-1.5 block text-xs font-medium text-muted-foreground">Payment status</span>
					<!-- Segmented control: a single bordered track with two equal segments -->
					<div class="inline-flex w-full rounded-md border border-border bg-muted/40 p-0.5" role="group" aria-label="Payment status">
						<button
							type="button"
							data-testid="scan-paid-yes"
							aria-pressed={isPaid}
							onclick={() => { isPaid = true; }}
							disabled={reviewControlsDisabled}
							class="flex-1 rounded-[5px] px-3 py-1.5 text-sm font-medium transition-colors {isPaid
								? 'bg-background text-foreground shadow-sm'
								: 'text-muted-foreground hover:text-foreground'}"
						>
							Already paid (receipt)
						</button>
						<button
							type="button"
							data-testid="scan-paid-no"
							aria-pressed={!isPaid}
							onclick={() => { isPaid = false; }}
							disabled={reviewControlsDisabled}
							class="flex-1 rounded-[5px] px-3 py-1.5 text-sm font-medium transition-colors {!isPaid
								? 'bg-background text-foreground shadow-sm'
								: 'text-muted-foreground hover:text-foreground'}"
						>
							Unpaid bill{editedFields['due_date'] ? ` — due ${editedFields['due_date']}` : ''}
						</button>
					</div>
				</div>
				{/if}

				<!-- Action buttons -->
				<Card.Footer class="border-t border-border px-4 py-3 [.border-t]:pt-3">
					<div class="flex w-full flex-col gap-2">
						<div class="flex gap-3">
							<Button
								data-testid="scan-confirm"
								onclick={() => confirmMutation.mutate()}
								disabled={confirmMutation.isPending || isProcessing || data.status === 'Failed' || isTerminal || scanDatePickerInvalid || (isPayment && !selectedTenantAccountId) || (isWorkOrder && selectedPropertyId === NO_PROPERTY) || leaseSelectionInvalid || leaseSignatureChoiceInvalid || leaseRentTrackingInvalid || applicationInvalid || loanInvalid || amountInvalid}
								class="flex-1"
							>
								{confirmMutation.isPending ? 'Confirming…' : isPayment ? 'Create Payment' : isWorkOrder ? 'Create Work Order' : isLease ? leaseReviewDisposition === 'NeedsSignatures' ? 'Create Agreement Draft' : leaseReviewDisposition === 'AlreadyFullySigned' ? 'Import Signed Lease' : 'Create Lease' : isApplication ? 'Create Applicant' : isLoan ? loanReviewMode === 'match' ? 'Match & Record Payment' : 'Add Loan' : 'Confirm & Create Expense'}
							</Button>
							<Button
								data-testid="scan-reject"
								variant="outline"
								onclick={handleReject}
								disabled={rejectMutation.isPending || isProcessing || isTerminal}
								class="hover:text-destructive"
							>
								{rejectMutation.isPending ? 'Rejecting…' : 'Reject'}
							</Button>
						</div>
						{#if depositFundingHref && !confirmedRecord && data.status !== 'Confirmed'}
							<Button
								data-testid="scan-record-security-deposit"
								variant="secondary"
								onclick={recordScanAsDepositFunds}
								disabled={depositFundingRedirectPending || isProcessing}
							>
								{depositFundingRedirectPending ? 'Opening deposit…' : 'Record as deposit funds'}
							</Button>
						{/if}
						{#if amountInvalid && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--m3c-error)]" data-testid="scan-amount-error">
								Enter an amount greater than $0 (under "total") before confirming.
							</p>
						{/if}
						{#if isPayment && !selectedTenantAccountId && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--warning)]">Select a rental account above to enable payment creation.</p>
						{/if}
						{#if canRecoverPaymentDepositHandoff && !selectedTenantAccountId && !isProcessing}
							<p class="text-center text-xs text-[var(--warning)]">Select a rental account above to record these deposit funds.</p>
						{/if}
						{#if isWorkOrder && selectedPropertyId === NO_PROPERTY && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--warning)]">Select a property above to enable work order creation.</p>
						{/if}
						{#if leaseSelectionInvalid && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--warning)]" data-testid="scan-lease-selection-error">
								{#if isCreatingLeaseProperty}
									Choose one or multiple rentals, then enter a property address (or name) and unit number above to create this lease.
								{:else}
									Pick a property and a unit above to create this lease.
								{/if}
							</p>
						{/if}
						{#if leaseSignatureChoiceInvalid && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--warning)]" data-testid="scan-lease-signature-choice-error">
								Tell us whether everyone has already signed this lease.
							</p>
						{/if}
						{#if applicationInvalid && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--warning)]" data-testid="scan-application-error">
								Enter the applicant's first and last name above to create the applicant.
							</p>
						{/if}
						{#if loanInvalid && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--warning)]" data-testid="scan-loan-error">
								{loanReviewMode === 'match'
									? 'Pick the loan, scheduled payment, and reviewed statement amounts above to match this statement.'
									: 'Enter a lender and pick a property above to add this loan.'}
							</p>
						{/if}
						{#if data.status === 'Failed' && !isTerminal}
							<p class="text-center text-xs text-muted-foreground">Couldn't read this document — try extraction again above, or reject it.</p>
						{/if}
						{#if data.status === 'Confirmed' && !confirmedRecord && !alreadyConfirmed}
							<!-- Confirmed but we don't know which record to link to (no createdEntityId) — note only. -->
							<p class="text-center text-xs text-[var(--success)]">
								This scan has already been confirmed.
							</p>
						{/if}
						{#if data.status === 'Rejected'}
							<p class="text-center text-xs text-muted-foreground">
								This scan has been rejected{data.failureReason ? `: ${data.failureReason}` : '.'}
							</p>
						{/if}
					</div>
				</Card.Footer>
			</Card.Root>
		</div>
	{/if}
</div>

<Dialog.Root
	open={showRejectDialog}
	onOpenChange={(v) => { if (!v) showRejectDialog = false; }}
>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>Reject scan</Dialog.Title>
			<Dialog.Description>Optionally provide a reason. The scan will be marked as rejected.</Dialog.Description>
		</Dialog.Header>
		<div class="py-2">
			<textarea
				bind:value={rejectReason}
				rows={3}
				placeholder="Reason for rejection (optional)"
				class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 placeholder:text-muted-foreground resize-none"
			></textarea>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={() => { showRejectDialog = false; }}>Cancel</Button>
			<Button variant="destructive" onclick={confirmReject} disabled={rejectMutation.isPending}>
				{rejectMutation.isPending ? 'Rejecting…' : 'Reject'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
