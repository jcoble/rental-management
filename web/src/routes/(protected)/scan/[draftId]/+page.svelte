<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { toast } from 'svelte-sonner';
	import { scan, type ScanFieldDto } from '$lib/api/scan';
	import { leases } from '$lib/api/endpoints/leases';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { seedLeaseUnitId, shouldSeedLeaseReviewState } from '$lib/scans/lease-review-state';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Badge } from '$lib/components/ui/badge';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Table from '$lib/components/ui/table';
	import * as Select from '$lib/components/ui/select';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import * as Dialog from '$lib/components/ui/dialog';

	// ScheduleECategory enum values (mirrors RentalCommand.Core.Enums.ScheduleECategory).
	// The submitted value stays the enum name; only the label shown to the landlord is friendly.
	const SCHEDULE_E_CATEGORIES: { value: string; label: string }[] = [
		{ value: 'Advertising', label: 'Advertising' },
		{ value: 'AutoTravel', label: 'Auto & travel' },
		{ value: 'CleaningMaintenance', label: 'Cleaning & maintenance' },
		{ value: 'Commissions', label: 'Commissions' },
		{ value: 'Insurance', label: 'Insurance' },
		{ value: 'LegalProfessional', label: 'Legal & professional fees' },
		{ value: 'ManagementFees', label: 'Management fees' },
		{ value: 'MortgageInterest', label: 'Mortgage interest' },
		{ value: 'Repairs', label: 'Repairs & maintenance' },
		{ value: 'Supplies', label: 'Supplies' },
		{ value: 'Taxes', label: 'Taxes' },
		{ value: 'Utilities', label: 'Utilities' },
		{ value: 'Depreciation', label: 'Depreciation' },
		{ value: 'Other', label: 'Other' }
	];

	// Map enum name → friendly label (falls back to the raw value if unknown).
	const CATEGORY_LABELS: Record<string, string> = Object.fromEntries(
		SCHEDULE_E_CATEGORIES.map((c) => [c.value, c.label])
	);

	function categoryLabel(value: string | undefined | null): string {
		if (!value) return 'Select category';
		return CATEGORY_LABELS[value] ?? value;
	}

	// Grouped scalar field definitions (in display order within each group)
	const FIELD_GROUPS: { label: string; fields: string[] }[] = [
		{
			label: 'Vendor',
			fields: ['vendor_name', 'vendor_address', 'vendor_phone', 'vendor_website', 'vendor_tax_id', 'receipt_number']
		},
		{
			label: 'Amounts',
			fields: ['subtotal', 'tax', 'tax_rate', 'tip', 'discount', 'shipping', 'total', 'payment_method', 'card_last4', 'due_date']
		},
		{
			label: 'Details',
			fields: ['document_kind', 'category', 'notes']
		}
	];

	// All known scalar field names (excluding line_items)
	const KNOWN_SCALAR_FIELDS = new Set(FIELD_GROUPS.flatMap((g) => g.fields));

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

	const draftId = $derived(parseInt(page.params.draftId ?? '0', 10));

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
		}
	}));

	const data = $derived(draftQuery.data);

	// Whether this draft targets a Payment (rent check) rather than an Expense
	const isPayment = $derived(data?.targetEntityType === 'Payment');
	const isWorkOrder = $derived(data?.targetEntityType === 'WorkOrder');
	const isLease = $derived(data?.targetEntityType === 'Lease');
	// A scanned completed paper rental application → creates an applicant / RentalApplication.
	const isApplication = $derived(data?.targetEntityType === 'Application');
	// Line items are an Expense concept — only the expense confirm path persists them — so the
	// editable line-items table and its override are scoped to expense drafts.
	const isExpense = $derived(!isPayment && !isWorkOrder && !isLease && !isApplication);

	// The worker is still reading the document while Pending or Processing.
	const isProcessing = $derived(data?.status === 'Pending' || data?.status === 'Processing');

	// Lease selector state (only used when isPayment).
	// String-backed for the shadcn Select; converted to a number at confirm time.
	let selectedLeaseId = $state<string>('');

	// Optional property selector for Expense drafts. Sends `propertyId` override.
	// 'none' is the sentinel for "no property" (empty string conflicts with the
	// Select's "nothing selected" state in bits-ui).
	const NO_PROPERTY = 'none';
	let selectedPropertyId = $state<string>(NO_PROPERTY);

	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', getCurrentPortfolioId()],
		queryFn: () => leases.list(getCurrentPortfolioId()),
		enabled: isPayment
	}));

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', getCurrentPortfolioId()],
		queryFn: () => properties.list(getCurrentPortfolioId(), { take: 200 }),
		enabled: !isPayment
	}));

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
	let newUnitNumber = $state('');
	let newPropertyFieldsSeeded = $state(false);

	function resetLeaseReviewState() {
		selectedLeasePropertyId = '';
		selectedLeaseUnitId = '';
		selectedTenantId = NO_TENANT;
		leasePropertySeeded = false;
		newPropertyName = '';
		newPropertyAddress = '';
		newPropertyCity = '';
		newPropertyState = '';
		newPropertyPostal = '';
		newUnitNumber = '';
		newPropertyFieldsSeeded = false;
	}

	// Units scoped to the chosen EXISTING property (required pick). Skipped in create-new mode (the
	// CREATE_PROPERTY sentinel isn't a real id — the unit is created from a free-text number instead).
	const leaseUnitsQuery = createQuery(() => ({
		queryKey: ['units-for-lease-scan', selectedLeasePropertyId],
		queryFn: () => properties.listUnits(Number(selectedLeasePropertyId)),
		enabled: isLease && !!selectedLeasePropertyId && selectedLeasePropertyId !== CREATE_PROPERTY
	}));

	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', getCurrentPortfolioId()],
		queryFn: () => tenants.list(getCurrentPortfolioId(), { take: 200 }),
		enabled: isLease
	}));

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
		const sel = propertiesQuery.data?.find((p) => String(p.id) === selectedLeasePropertyId);
		return sel ? sel.name : '— Select a property —';
	});

	const selectedLeaseUnitLabel = $derived.by(() => {
		if (!selectedLeaseUnitId) return '— Select a unit —';
		const sel = leaseUnitsQuery.data?.find((u) => String(u.id) === selectedLeaseUnitId);
		return sel ? `Unit ${sel.unitNumber}` : '— Select a unit —';
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
		const sel = tenantsQuery.data?.find((t) => String(t.id) === selectedTenantId);
		return sel ? tenantLabel(sel) : newTenantLabel;
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

	// Reset the unit pick whenever the property changes (units are property-scoped).
	$effect(() => {
		const _ = selectedLeasePropertyId;
		// Create-new mode has no existing-unit pick — drop any stale id so it can't leak.
		if (isCreatingLeaseProperty) {
			if (selectedLeaseUnitId) selectedLeaseUnitId = '';
			return;
		}
		// Clear a stale unit selection if it no longer belongs to the chosen property.
		if (selectedLeaseUnitId && leaseUnitsQuery.data && !leaseUnitsQuery.data.some((u) => String(u.id) === selectedLeaseUnitId)) {
			selectedLeaseUnitId = '';
		}
	});

	const selectedPropertyLabel = $derived.by(() => {
		if (!selectedPropertyId || selectedPropertyId === NO_PROPERTY) return '— No property —';
		const sel = propertiesQuery.data?.find((p) => String(p.id) === selectedPropertyId);
		return sel ? sel.name : '— No property —';
	});

	function leaseLabel(lease: { leaseNumber: string; tenantName?: string | null; unitNumber?: string | null }): string {
		return `#${lease.leaseNumber}${lease.tenantName ? ` — ${lease.tenantName}` : ''}${lease.unitNumber ? ` · Unit ${lease.unitNumber}` : ''}`;
	}

	const selectedLeaseLabel = $derived.by(() => {
		if (!selectedLeaseId) return '— Select a lease —';
		const sel = leasesQuery.data?.find((l) => String(l.id) === selectedLeaseId);
		return sel ? leaseLabel(sel) : '— Select a lease —';
	});

	// Editable field values (keyed by field name, scalars only)
	let editedFields = $state<Record<string, string>>({});

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
			const hasPropertyAnchor = !!(newPropertyAddress.trim() || newPropertyName.trim());
			return !hasPropertyAnchor || !newUnitNumber.trim();
		}
		return !selectedLeasePropertyId || !selectedLeaseUnitId;
	});

	// Paid / Unpaid toggle — true = already paid (receipt), false = unpaid bill
	let isPaid = $state(true);
	let isPaidInitialized = $state(false);

	function defaultIsPaidFromKind(kind: string | undefined): boolean {
		if (!kind) return true;
		return !['Bill', 'Invoice', 'UtilityBill', 'PropertyTax'].includes(kind);
	}

	// Initialize editable fields when data arrives
	$effect(() => {
		if (data?.fields) {
			const initial: Record<string, string> = {};
			for (const f of data.fields) {
				if (f.name === LINE_ITEMS_FIELD) continue;
				if (!(f.name in editedFields)) {
					initial[f.name] = f.value;
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
			if ((data.targetEntityType === 'WorkOrder' || data.targetEntityType === 'Expense') && selectedPropertyId === NO_PROPERTY) {
				const propertyField = data.fields.find((f) => f.name === 'property_id' || f.name === 'propertyId');
				if (propertyField?.value) selectedPropertyId = propertyField.value;
			}
			// Lease drafts: seed the property/unit pickers + the create-new fields. Tenant stays on
			// "create new" unless the user picks.
			if (data.targetEntityType === 'Lease' && shouldSeedLeaseReviewState(data.status, data.fields.length)) {
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
					if (extractedId && propertiesQuery.data?.some((p) => String(p.id) === extractedId)) {
						selectedLeasePropertyId = extractedId;
					} else if (leaseProposal?.property.action === 'link' && leaseProposal.property.existingId != null) {
						selectedLeasePropertyId = String(leaseProposal.property.existingId);
					} else if (leaseProposal && (leaseProposal.property.action === 'create' || leaseProposal.property.action === 'select')) {
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
			default: return s;
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

	// Build grouped sections from the extracted fields
	function buildGroups(fields: ScanFieldDto[]): { label: string; fields: ScanFieldDto[] }[] {
		const fieldMap = new Map(fields.filter((f) => f.name !== LINE_ITEMS_FIELD).map((f) => [f.name, f]));
		const result: { label: string; fields: ScanFieldDto[] }[] = [];

		for (const group of FIELD_GROUPS) {
			const present = group.fields.map((name) => fieldMap.get(name)).filter((f): f is ScanFieldDto => !!f);
			if (present.length > 0) {
				result.push({ label: group.label, fields: present });
			}
		}

		// "Other" group: any scalar fields not in KNOWN_SCALAR_FIELDS
		const otherFields = [...fieldMap.values()].filter((f) => !KNOWN_SCALAR_FIELDS.has(f.name));
		if (otherFields.length > 0) {
			result.push({ label: 'Other', fields: otherFields });
		}

		return result;
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
	// /file endpoint needs a JWT bearer an <img>/<iframe> can't send).
	const fileUrl = $derived(data ? `/scan-file/${data.id}` : '');

	// Success state — what was just created, so the landlord keeps context
	// instead of being dumped onto /accounting. (Lease drafts navigate straight to
	// the new lease instead, so they don't use this card.)
	let confirmedRecord = $state<{ type: 'Expense' | 'Payment' | 'WorkOrder'; id: number | null; amount: number | null } | null>(null);
	const linkedRecordHref = $derived((() => {
		const type = confirmedRecord?.type ?? data?.createdEntityType;
		const id = confirmedRecord?.id ?? data?.createdEntityId;
		if (!type || !id) return '/accounting';
		if (type === 'Payment') return `/accounting/payments/${id}`;
		if (type === 'WorkOrder') return `/maintenance/${id}`;
		if (type === 'Lease') return `/leases/${id}`;
		return `/accounting/expenses/${id}`;
	})());

	function formatUsd(val: number | null): string {
		if (val == null) return '';
		return val.toLocaleString('en-US', { style: 'currency', currency: 'USD' });
	}

	// A draft that was already confirmed in a PRIOR session (status Confirmed, no fresh in-session
	// confirmedRecord, and not a Lease — leases navigate away on confirm). For these we show a
	// prominent "view the created record" card instead of leaving a disabled Confirm button as the
	// only signpost. Only show when we actually know what record to link to.
	const alreadyConfirmed = $derived(
		data?.status === 'Confirmed' && !confirmedRecord && !isLease && !!data?.createdEntityId
	);

	// Friendly label for the created record's type (drives the "View/Edit the Payment" button).
	const createdTypeLabel = $derived.by(() => {
		const type = confirmedRecord?.type ?? data?.createdEntityType;
		switch (type) {
			case 'Payment': return 'Payment';
			case 'WorkOrder': return 'Work Order';
			case 'Lease': return 'Lease';
			case 'Expense': return 'Expense';
			default: return 'Record';
		}
	});

	// Confirm mutation
	const confirmMutation = createMutation(() => ({
		mutationFn: () => {
			const overrides = buildOverridesJson();
			return scan.confirm(draftId, overrides);
		},
		onSuccess: (result) => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			queryClient.invalidateQueries({ queryKey: ['scan', draftId] });
			// Lease drafts go straight to the new lease detail page.
			if (isLease) {
				const leaseId = result.leaseId ?? result.entityId ?? null;
				queryClient.invalidateQueries({ queryKey: ['leases'] });
				toast.success('Lease created');
				if (leaseId) {
					goto(`/leases/${leaseId}`);
					return;
				}
			}
			// Application drafts land on the applications list, where the new applicant appears.
			if (isApplication) {
				queryClient.invalidateQueries({ queryKey: ['applications'] });
				toast.success('Applicant created');
				goto('/applications');
				return;
			}
			// Capture the resolved amount before refetch can mutate editedFields.
			const type = (result.entityType === 'Payment' || result.entityType === 'Expense' || result.entityType === 'WorkOrder')
				? result.entityType
				: isPayment ? 'Payment' : isWorkOrder ? 'WorkOrder' : 'Expense';
			confirmedRecord = {
				type,
				id: result.entityId ?? result.paymentId ?? result.expenseId ?? result.workOrderId ?? null,
				amount: resolvedAmount
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
			overrides[keyMap[name] ?? name] = value;
		}

		if (isApplication) {
			// Application drafts: the edited applicant scalar fields (first_name, last_name, email, phone,
			// date_of_birth, current_address, employer, monthly_income, applying_for, desired_move_in_date,
			// id_last4, co_signer_name) are already in `overrides` as snake_case keys, which the server's
			// ApplyApplicationOverrides accepts directly. Carry through the extracted property/unit link so
			// the applicant can be filed under the unit they applied for (server re-validates it in-portfolio).
			const propertyField = data?.fields.find((f) => f.name === 'property_id' || f.name === 'propertyId');
			const unitField = data?.fields.find((f) => f.name === 'unit_id' || f.name === 'unitId');
			if (propertyField?.value) overrides['propertyId'] = Number(propertyField.value) || null;
			if (unitField?.value) overrides['unitId'] = Number(unitField.value) || null;
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
			return JSON.stringify(overrides);
		}

		// Explicitly send the user-edited amount as a clean number under `total`
		// (the server honors `total`/`amount`). Without this the edited value
		// could be dropped or mis-parsed when typed with a currency symbol/commas.
		if (resolvedAmount != null) {
			overrides['total'] = resolvedAmount;
		}

		if (isPayment) {
			// Payment drafts require leaseId; omit the paid/unpaid toggle (a received check is always paid)
			overrides['leaseId'] = selectedLeaseId ? Number(selectedLeaseId) : null;
		} else if (isWorkOrder) {
			if (selectedPropertyId && selectedPropertyId !== NO_PROPERTY) {
				overrides['propertyId'] = Number(selectedPropertyId);
			}
		} else {
			// Expense drafts: always include the paid/unpaid toggle decision
			overrides['is_paid'] = isPaid;
			// Optional property association (server honors `propertyId`).
			if (selectedPropertyId && selectedPropertyId !== NO_PROPERTY) {
				overrides['propertyId'] = Number(selectedPropertyId);
			}
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
	<title>Review Scan - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="scan-review">
	{#if draftQuery.isLoading}
		<div class="flex h-48 items-center justify-center">
			<div class="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
		</div>
	{:else if draftQuery.isError}
		<!-- M5: clear error state with retry instead of a perpetual spinner -->
		<div class="mx-auto max-w-md py-12 text-center" data-testid="scan-load-error">
			<div class="mb-3 text-3xl">⚠️</div>
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
			<h1 class="text-xl font-bold">Review Scan #{data.id}</h1>
			<Badge variant="outline" class={statusBadgeClass(data.status)} data-testid="scan-status-badge" data-status={data.status}>
				{statusLabel(data.status)}
			</Badge>
		</div>

		{#if confirmedRecord}
			<!-- design#11: keep context after confirm instead of dumping to /accounting -->
			<div
				class="mb-4 flex flex-col gap-3 rounded-lg border bg-[var(--m3c-success-container)] text-[var(--m3c-on-success-container)] border-[color-mix(in_srgb,var(--success)_45%,transparent)] px-4 py-4 text-sm sm:flex-row sm:items-center sm:justify-between"
				data-testid="scan-confirm-success"
			>
				<div class="flex items-center gap-3">
					<span class="text-2xl leading-none">✓</span>
					<div>
						<p class="font-semibold">
							{confirmedRecord.type === 'Payment' ? 'Payment recorded' : confirmedRecord.type === 'WorkOrder' ? 'Work order created' : 'Expense created'}{confirmedRecord.type !== 'WorkOrder' && confirmedRecord.amount != null ? ` — ${formatUsd(confirmedRecord.amount)}` : ''}
						</p>
						<p class="text-xs opacity-80">{confirmedRecord.type === 'WorkOrder' ? 'It is saved to maintenance.' : 'It is saved to your books.'} You can view it or scan another document.</p>
					</div>
				</div>
				<div class="flex shrink-0 gap-2">
					<Button size="sm" href={linkedRecordHref} data-testid="scan-view-record">View/Edit Record</Button>
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
					<span class="text-2xl leading-none">✓</span>
					<div>
						<p class="font-semibold">This scan was confirmed</p>
						<p class="text-xs opacity-80">
							It created a {createdTypeLabel}. This capture is read-only — make any edits on the record itself so your books stay the source of truth.
						</p>
					</div>
				</div>
				<div class="flex shrink-0 gap-2">
					<Button size="sm" href={linkedRecordHref} data-testid="scan-view-record">View/Edit {createdTypeLabel}</Button>
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
					<p class="text-base font-semibold text-foreground">Reading your document…</p>
					<p class="text-sm text-muted-foreground">
						The computer is pulling out the vendor, amounts, and dates for you. This usually takes just a few seconds.
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
				<strong>AI extraction is off.</strong> No OpenAI API key is configured, so this document's fields
				weren't filled in automatically. Enter them manually below, or set
				<code class="rounded bg-[var(--m3c-warning-container)] px-1">Assistant:ApiKey</code> and re-scan.
			</div>
		{/if}

		<div class="grid gap-6 lg:grid-cols-2">
			<!-- Left: document preview -->
			<Card.Root class="flex flex-col gap-0 py-0">
				<Card.Header class="border-b border-border px-4 py-3 [.border-b]:pb-3">
					<Card.Title class="text-sm">Document Preview</Card.Title>
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
								<Button variant="link" href={fileUrl} target="_blank" rel="noopener noreferrer" class="h-auto p-0 text-xs">
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
					<div class="flex items-baseline gap-1 flex-wrap">
						<Card.Title class="text-sm">Extracted Fields</Card.Title>
						{#if data.modelId}
							<span class="text-xs text-muted-foreground">via {data.modelId}</span>
						{/if}
						{#if data.tokensUsed != null}
							<span class="text-xs text-muted-foreground">· {data.tokensUsed.toLocaleString()} tokens</span>
						{/if}
						{#if data.costUsd != null}
							<span class="text-xs text-muted-foreground">· ~${data.costUsd.toFixed(4)}</span>
						{/if}
					</div>
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
						<!-- Lease selector — required for Payment drafts -->
						<div class="mb-5 rounded-md border border-border bg-muted/30 p-3">
							<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-lease-select">
								Which lease is this payment for? <span class="text-[var(--m3c-error)]">*</span>
							</label>
							<Select.Root type="single" bind:value={selectedLeaseId}>
								<Select.Trigger id="scan-lease-select" data-testid="scan-lease-select" class="w-full">
									{selectedLeaseLabel}
								</Select.Trigger>
								<Select.Content>
									{#if leasesQuery.data}
										{#each leasesQuery.data as lease (lease.id)}
											<Select.Item value={String(lease.id)} label={leaseLabel(lease)}>
												{leaseLabel(lease)}
											</Select.Item>
										{/each}
									{/if}
								</Select.Content>
							</Select.Root>
							{#if leasesQuery.isLoading}
								<p class="mt-1 text-xs text-muted-foreground">Loading leases…</p>
							{/if}
						</div>
					{:else if isLease}
						<!-- Lease selectors — link an existing property/unit OR create them from the document. -->
						<div class="mb-5 space-y-3 rounded-md border border-border bg-muted/30 p-3" data-testid="scan-lease-selectors">
							<!-- "What this will do" summary from the import proposal, so the create-vs-link
							     outcome (incl. the empty-portfolio bootstrap) is visible up front. -->
							{#if leaseProposal}
								<div class="rounded-md border border-accent/40 bg-accent/5 p-2.5 text-xs" data-testid="scan-lease-proposal">
									<p class="mb-1 font-semibold text-foreground">When you confirm, this lease will:</p>
									<ul class="space-y-0.5 text-muted-foreground">
										<li data-testid="scan-lease-proposal-property">
											{#if isCreatingLeaseProperty || leaseProposal.property.action === 'create'}
												<span class="font-medium text-[var(--success)]">Create</span> a new property{leaseProposal.property.label ? ` — ${leaseProposal.property.label}` : ''}
											{:else if leaseProposal.property.action === 'link'}
												<span class="font-medium">Link</span> to {leaseProposal.property.label ?? 'an existing property'}
											{:else}
												Need you to choose a property
											{/if}
										</li>
										<li data-testid="scan-lease-proposal-unit">
											{#if isCreatingLeaseProperty || leaseProposal.unit.action === 'create'}
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
								<Select.Root type="single" bind:value={selectedLeasePropertyId}>
									<Select.Trigger id="scan-lease-property-select" data-testid="scan-lease-property-select" class="w-full">
										{selectedLeasePropertyLabel}
									</Select.Trigger>
									<Select.Content>
										<Select.Item value={CREATE_PROPERTY} label={newPropertyLabel}>{newPropertyLabel}</Select.Item>
										{#if propertiesQuery.data}
											{#each propertiesQuery.data as prop (prop.id)}
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
							</div>

							{#if isCreatingLeaseProperty}
								<!-- Create-new property: editable address (seeded from the document) so the
								     reviewer can correct it before the property is created. -->
								<div class="space-y-2 rounded-md border border-dashed border-border p-2.5" data-testid="scan-lease-new-property">
									<p class="text-xs font-medium text-muted-foreground">New property details (from the document — edit if needed)</p>
									<Input data-testid="scan-new-property-name" placeholder="Property name (optional)" bind:value={newPropertyName} />
									<Input data-testid="scan-new-property-address" placeholder="Street address" bind:value={newPropertyAddress} />
									<div class="grid grid-cols-3 gap-2">
										<Input data-testid="scan-new-property-city" placeholder="City" bind:value={newPropertyCity} />
										<Input data-testid="scan-new-property-state" placeholder="State" bind:value={newPropertyState} />
										<Input data-testid="scan-new-property-postal" placeholder="ZIP" bind:value={newPropertyPostal} />
									</div>
									<p class="text-[11px] text-muted-foreground">Enter a street address or a property name so the property can be created.</p>
								</div>

								<div>
									<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-lease-new-unit">
										Unit number <span class="text-[var(--m3c-error)]">*</span>
									</label>
									<Input id="scan-lease-new-unit" data-testid="scan-lease-new-unit" placeholder="e.g. 1 (single-family) or 2B" bind:value={newUnitNumber} />
									<p class="mt-1 text-xs text-muted-foreground">A new unit with this number is created under the new property.</p>
								</div>
							{:else}
								<div>
									<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-lease-unit-select">
										Which unit? <span class="text-[var(--m3c-error)]">*</span>
									</label>
									<Select.Root type="single" bind:value={selectedLeaseUnitId} disabled={!selectedLeasePropertyId}>
										<Select.Trigger id="scan-lease-unit-select" data-testid="scan-lease-unit-select" class="w-full">
											{selectedLeaseUnitLabel}
										</Select.Trigger>
										<Select.Content>
											{#if leaseUnitsQuery.data}
												{#each leaseUnitsQuery.data as unit (unit.id)}
													<Select.Item value={String(unit.id)} label={`Unit ${unit.unitNumber}`}>
														Unit {unit.unitNumber} ({unit.status})
													</Select.Item>
												{/each}
											{/if}
										</Select.Content>
									</Select.Root>
									{#if !selectedLeasePropertyId}
										<p class="mt-1 text-xs text-muted-foreground">Pick a property first to see its units.</p>
									{:else if leaseUnitsQuery.isLoading}
										<p class="mt-1 text-xs text-muted-foreground">Loading units…</p>
									{:else if (leaseUnitsQuery.data?.length ?? 0) === 0}
										<p class="mt-1 text-xs text-[var(--warning)]">This property has no units yet — switch to "{newPropertyLabel}" above, or add a unit to this property first.</p>
									{/if}
								</div>
							{/if}

							<div>
								<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-lease-tenant-select">
									Tenant <span class="font-normal text-muted-foreground">(optional)</span>
								</label>
								<Select.Root type="single" bind:value={selectedTenantId}>
									<Select.Trigger id="scan-lease-tenant-select" data-testid="scan-lease-tenant-select" class="w-full">
										{selectedTenantLabel}
									</Select.Trigger>
									<Select.Content>
										<Select.Item value={NO_TENANT} label={newTenantLabel}>{newTenantLabel}</Select.Item>
										{#if tenantsQuery.data}
											{#each tenantsQuery.data as t (t.id)}
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
							</div>
						</div>

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
										<Input
											id="lease-term-{term.name}"
											data-testid="scan-field-{term.name}"
											type={term.type}
											bind:value={editedFields[term.name]}
										/>
									</div>
								{/each}
							</div>
						</div>
					{:else if isApplication}
						<!-- Applicant review — a single editable applicant form (Payment/Expense model, NOT the
						     4-step guided lease flow). Confirming creates a RentalApplication on /applications. -->
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
										<Input
											id="application-{appField.name}"
											data-testid="scan-field-{appField.name}"
											type={appField.type}
											bind:value={editedFields[appField.name]}
										/>
									</div>
								{/each}
							</div>
						</div>
					{:else}
						<!-- Property selector for Expense/WorkOrder drafts (sends propertyId override) -->
						<div class="mb-5 rounded-md border border-border bg-muted/30 p-3">
							<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-property-select">
								Which property is this for? {#if isWorkOrder}<span class="text-[var(--m3c-error)]">*</span>{:else}<span class="font-normal text-muted-foreground">(optional)</span>{/if}
							</label>
							<Select.Root type="single" bind:value={selectedPropertyId}>
								<Select.Trigger id="scan-property-select" data-testid="scan-property-select" class="w-full">
									{selectedPropertyLabel}
								</Select.Trigger>
								<Select.Content>
									<Select.Item value={NO_PROPERTY} label="— No property —">— No property —</Select.Item>
									{#if propertiesQuery.data}
										{#each propertiesQuery.data as prop (prop.id)}
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
						</div>
					{/if}
					{#if isLease || isApplication}
						<!-- Lease terms / applicant details are rendered above; the generic extracted-field
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
									<label class="mb-1 block text-xs font-medium text-muted-foreground capitalize" for="field-{fieldName}">
										{fieldName.replace(/_/g, ' ')}
									</label>
									{#if fieldName === 'category'}
										<!-- Category dropdown — friendly labels, enum value submitted -->
										<Select.Root type="single" bind:value={editedFields[fieldName]}>
											<Select.Trigger id="field-{fieldName}" data-testid="scan-field-{fieldName}" class="w-full">
												{categoryLabel(editedFields[fieldName])}
											</Select.Trigger>
											<Select.Content>
												{#each SCHEDULE_E_CATEGORIES as cat}
													<Select.Item value={cat.value} label={cat.label}>{cat.label}</Select.Item>
												{/each}
											</Select.Content>
										</Select.Root>
									{:else}
										<Input
											id="field-{fieldName}"
											data-testid="scan-field-{fieldName}"
											type="text"
											bind:value={editedFields[fieldName]}
										/>
									{/if}
								</div>
							{/each}
						</div>
					{:else}
						{@const groups = buildGroups(data.fields)}
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
														class="text-xs font-medium capitalize {level === 'medium' ? 'text-muted-foreground' : 'text-foreground'}"
														for="field-{field.name}"
													>
														{field.name.replace(/_/g, ' ')}
													</label>
													{#if level !== 'high'}
														<span class="text-xs {level === 'low' ? 'text-[var(--m3c-error)]' : 'text-muted-foreground'}">
															{confidenceLabel(field.confidence)}
														</span>
													{/if}
												</div>
												{#if field.name === 'category'}
													<!-- Category dropdown — friendly labels, enum value submitted -->
													<Select.Root type="single" bind:value={editedFields[field.name]}>
														<Select.Trigger id="field-{field.name}" data-testid="scan-field-{field.name}" class="w-full">
															{categoryLabel(editedFields[field.name])}
														</Select.Trigger>
														<Select.Content>
															{#each SCHEDULE_E_CATEGORIES as cat}
																<Select.Item value={cat.value} label={cat.label}>{cat.label}</Select.Item>
															{/each}
														</Select.Content>
													</Select.Root>
												{:else}
													<!-- Raw <input> needed here to support the use:focusFirstLow action (actions cannot be placed on components) -->
													<input
														id="field-{field.name}"
														data-testid="scan-field-{field.name}"
														type="text"
														bind:value={editedFields[field.name]}
														class="border-input bg-background selection:bg-primary dark:bg-input/30 selection:text-primary-foreground ring-offset-background placeholder:text-muted-foreground flex h-9 w-full min-w-0 rounded-md border px-3 py-1 text-base shadow-xs transition-[color,box-shadow] outline-none disabled:cursor-not-allowed disabled:opacity-50 md:text-sm focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px] {fieldInputClass(field)}"
														use:focusFirstLow={level === 'low' && isFirstLowField(data.fields, field)}
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
										>
											+ Add line item
										</Button>
									</div>
									{#if editedLineItems.length === 0}
										<p class="text-sm text-muted-foreground">
											No line items.
											<button type="button" class="text-accent underline-offset-2 hover:underline" onclick={addLineItem}>Add one</button>
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
																/>
															</Table.Cell>
															<Table.Cell class="px-2 py-1.5">
																<Input
																	type="text"
																	inputmode="decimal"
																	bind:value={item.quantity}
																	data-testid="scan-line-item-quantity-{i}"
																	class="h-8 text-right font-mono text-sm tabular-nums"
																/>
															</Table.Cell>
															<Table.Cell class="px-2 py-1.5">
																<Input
																	type="text"
																	inputmode="decimal"
																	bind:value={item.unit_price}
																	data-testid="scan-line-item-unit-price-{i}"
																	class="h-8 text-right font-mono text-sm tabular-nums"
																/>
															</Table.Cell>
															<Table.Cell class="px-2 py-1.5">
																<Input
																	type="text"
																	inputmode="decimal"
																	bind:value={item.amount}
																	data-testid="scan-line-item-amount-{i}"
																	class="h-8 text-right font-mono text-sm tabular-nums"
																/>
															</Table.Cell>
															<Table.Cell class="px-1 py-1.5 text-center">
																<button
																	type="button"
																	onclick={() => removeLineItem(item.key)}
																	data-testid="scan-line-item-remove-{i}"
																	class="text-muted-foreground transition-colors hover:text-destructive"
																	aria-label="Remove line item {i + 1}"
																>✕</button>
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

				<!-- Paid / Unpaid toggle — hidden for Payment, WorkOrder, Lease, and Application drafts -->
				{#if !isPayment && !isWorkOrder && !isLease && !isApplication}
				<div class="border-t border-border px-4 py-3" data-testid="scan-paid-toggle">
					<span class="mb-1.5 block text-xs font-medium text-muted-foreground">Payment status</span>
					<!-- Segmented control: a single bordered track with two equal segments -->
					<div class="inline-flex w-full rounded-md border border-border bg-muted/40 p-0.5" role="group" aria-label="Payment status">
						<button
							type="button"
							data-testid="scan-paid-yes"
							aria-pressed={isPaid}
							onclick={() => { isPaid = true; }}
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
					{@const isTerminal = data.status === 'Rejected' || data.status === 'Confirmed' || !!confirmedRecord}
					<div class="flex w-full flex-col gap-2">
						<div class="flex gap-3">
							<Button
								data-testid="scan-confirm"
								onclick={() => confirmMutation.mutate()}
								disabled={confirmMutation.isPending || isProcessing || data.status === 'Failed' || isTerminal || (isPayment && !selectedLeaseId) || (isWorkOrder && selectedPropertyId === NO_PROPERTY) || leaseSelectionInvalid || applicationInvalid || amountInvalid}
								class="flex-1"
							>
								{confirmMutation.isPending ? 'Confirming…' : isPayment ? 'Create Payment' : isWorkOrder ? 'Create Work Order' : isLease ? 'Create Lease' : isApplication ? 'Create Applicant' : 'Confirm & Create Expense'}
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
						{#if amountInvalid && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--m3c-error)]" data-testid="scan-amount-error">
								Enter an amount greater than $0 (under "total") before confirming.
							</p>
						{/if}
						{#if isPayment && !selectedLeaseId && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--warning)]">Select a lease above to enable payment creation.</p>
						{/if}
						{#if isWorkOrder && selectedPropertyId === NO_PROPERTY && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--warning)]">Select a property above to enable work order creation.</p>
						{/if}
						{#if leaseSelectionInvalid && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--warning)]" data-testid="scan-lease-selection-error">
								{#if isCreatingLeaseProperty}
									Enter a property address (or name) and a unit number above to create this lease.
								{:else}
									Pick a property and a unit above to create this lease.
								{/if}
							</p>
						{/if}
						{#if applicationInvalid && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-[var(--warning)]" data-testid="scan-application-error">
								Enter the applicant's first and last name above to create the applicant.
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
							<p class="text-center text-xs text-muted-foreground">This scan has been rejected.</p>
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
