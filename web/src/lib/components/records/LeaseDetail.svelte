<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { untrack } from 'svelte';
	import { goto } from '$app/navigation';
	import { recordHref } from '$lib/navigation/record-href';
	import { leases, type LeaseSignatureQueueItemResponse } from '$lib/api/endpoints/leases';
	import { documentTemplates } from '$lib/api/endpoints/document-templates';
	import {
		canSetLeaseActive,
		leaseStatusOptionsForCurrentStatus,
		quickLeaseStatusActionsForStatus,
		resolveLeaseDetailTab,
		scannedLeaseDocumentLinkLabel,
		tabForLeaseEdit,
		type LeaseQuickStatusAction,
	} from '$lib/leases/lease-detail-state';
	import { ensureSelectedOption } from '$lib/leases/lease-edit-options';
	import { getLeaseDeleteState } from '$lib/leases/lease-delete-state';
	import {
		canShowLeaseSignatureSendAction,
		hasAgreementAfterSignatureSend,
		leaseSignatureQueuedMessage,
		signableStateMessage,
		visibleLeaseStatus,
	} from '$lib/leases/lease-esign';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { payments } from '$lib/api/endpoints/payments';
	import {
		openingBalances,
		type OpeningBalanceResponse,
	} from '$lib/api/endpoints/opening-balances';
	import type { Lease, Payment } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { leaseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import { addCalendarYear } from '$lib/utils/parse-date';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { paymentTypeLabel } from '$lib/utils/payment-labels';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import TenantMultiSelect from '$lib/components/forms/TenantMultiSelect.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import HeroCard, { type HeroTone } from '$lib/components/shared/HeroCard.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import TenantNoticeDialog from '$lib/components/notices/TenantNoticeDialog.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import * as Tabs from '$lib/components/ui/tabs';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Pencil, Save, Trash2, X, FileText, Download, PenLine, DollarSign, Users, StickyNote, CalendarRange, Library, ExternalLink, Mail, RefreshCw, Copy, BellRing } from '@lucide/svelte';
	import { ApiError } from '$lib/api/client';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';
	import HelpTooltip from '$lib/components/ui/HelpTooltip.svelte';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import * as Tooltip from '$lib/components/ui/tooltip';
	import { isMismatchedUnitSelection } from '$lib/unit/unit-membership-guard';

	let {
		leaseId,
		onDeleted,
		initialTab,
		expectedUnitId,
		onUnitMismatch,
	}: {
		leaseId: number;
		onDeleted: () => void;
		initialTab?: string;
		expectedUnitId?: number;
		onUnitMismatch?: () => void;
	} = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const LEASE_STATUSES = ['Draft', 'PendingSignature', 'Active', 'NoticeGiven', 'Expired', 'Terminated'];

	const leaseQuery = createQuery(() => ({
		queryKey: ['lease', leaseId],
		queryFn: () => leases.get(leaseId),
		enabled: leaseId > 0,
	}));

	const leaseTemplatesQuery = createQuery(() => ({
		queryKey: ['document-templates', 'lease', 'agreement-preview', portfolioId],
		queryFn: () =>
			documentTemplates.listPage({
				kind: 'Lease',
				sort: '-updatedAt',
				take: 3,
			}),
		enabled: portfolioId > 0,
	}));

	const lease = $derived(leaseQuery.data);

	$effect(() => {
		if (isMismatchedUnitSelection(lease, expectedUnitId)) onUnitMismatch?.();
	});

	const leaseTemplateCount = $derived(leaseTemplatesQuery.data?.totalCount ?? 0);
	const leaseTemplatePreview = $derived(leaseTemplatesQuery.data?.items ?? []);

	// Payments for this lease
	const paymentsQuery = createQuery(() => ({
		queryKey: ['payments', portfolioId, leaseId],
		queryFn: () => payments.list(portfolioId, { leaseId, take: 200 }),
		enabled: portfolioId > 0 && leaseId > 0,
	}));

	// Plain-English account history (charges, payments, balance) with a "why" per entry.
	const ledgerQuery = createQuery(() => ({
		queryKey: ['lease-ledger', leaseId],
		queryFn: () => leases.ledger(leaseId),
		enabled: leaseId > 0,
	}));
	const ledger = $derived(ledgerQuery.data);
	const balanceLine = $derived.by(() => {
		if (!ledger) return '';
		if (ledger.balance > 0.005) return `${ledger.tenantName ?? 'This tenant'} still owes ${formatCurrency(ledger.balance)}.`;
		if (ledger.balance < -0.005) return `Paid ahead by ${formatCurrency(Math.abs(ledger.balance))} (credit on the account).`;
		return 'All caught up — nothing owed.';
	});

	// --- Opening balance (carried-over tenant balance from before Rental Command) ---
	const openingBalanceQuery = createQuery(() => ({
		queryKey: ['opening-balance', leaseId],
		queryFn: () => openingBalances.list(leaseId),
		enabled: leaseId > 0,
	}));
	const openingBalance = $derived<OpeningBalanceResponse | undefined>(openingBalanceQuery.data?.[0]);

	function invalidateOpeningBalance() {
		queryClient.invalidateQueries({ queryKey: ['opening-balance', leaseId] });
		// Refresh the Account History so the auto-rendered "Opening" entry updates.
		queryClient.invalidateQueries({ queryKey: ['lease-ledger', leaseId] });
	}

	// Dialog state. We keep the UI sign-positive (always a non-negative amount the
	// user types) and capture direction with a select: "owed" → +, "credit" → -.
	let showOpeningDialog = $state(false);
	let openingDirection = $state<'owed' | 'credit'>('owed');
	let openingAmount = $state('');
	let openingAsOfDate = $state('');
	let openingNote = $state('');
	let openingErrors = $state<{ amount?: string; asOfDate?: string }>({});
	let showOpeningDeleteConfirm = $state(false);

	const directionOptions = [
		{ value: 'owed', label: 'Tenant owed' },
		{ value: 'credit', label: 'Tenant credit' },
	];
	const openingDirectionLabel = $derived(
		directionOptions.find((o) => o.value === openingDirection)?.label ?? 'Tenant owed'
	);

	function openOpeningDialog() {
		if (openingBalance) {
			openingDirection = openingBalance.amount < 0 ? 'credit' : 'owed';
			openingAmount = String(Math.abs(openingBalance.amount));
			openingAsOfDate = openingBalance.asOfDate?.slice(0, 10) ?? '';
			openingNote = openingBalance.note ?? '';
		} else {
			openingDirection = 'owed';
			openingAmount = '';
			// Default the as-of date to the lease start, falling back to today.
			openingAsOfDate = lease?.startDate?.slice(0, 10) ?? new Date().toISOString().slice(0, 10);
			openingNote = '';
		}
		openingErrors = {};
		showOpeningDialog = true;
	}
	function closeOpeningDialog() {
		openingErrors = {};
		showOpeningDialog = false;
	}

	const saveOpeningMutation = createMutation(() => ({
		mutationFn: (body: { amount: number; asOfDate: string; note?: string }) =>
			openingBalance
				? openingBalances.update(openingBalance.id, body)
				: openingBalances.create({ leaseId, ...body }),
		onSuccess: () => {
			showSuccess(openingBalance ? 'Opening balance updated.' : 'Opening balance saved.');
			closeOpeningDialog();
			invalidateOpeningBalance();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteOpeningMutation = createMutation(() => ({
		mutationFn: (id: number) => openingBalances.remove(id),
		onSuccess: () => {
			showSuccess('Opening balance removed.');
			showOpeningDeleteConfirm = false;
			invalidateOpeningBalance();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitOpeningBalance() {
		const errors: { amount?: string; asOfDate?: string } = {};
		const rawAmount = String(openingAmount ?? '').trim();
		const magnitude = Number(rawAmount);
		if (!rawAmount || Number.isNaN(magnitude) || magnitude < 0) {
			errors.amount = 'Enter an amount of 0 or more.';
		}
		if (!openingAsOfDate) {
			errors.asOfDate = 'Pick an as-of date.';
		}
		if (errors.amount || errors.asOfDate) {
			openingErrors = errors;
			return;
		}
		openingErrors = {};
		const signed = openingDirection === 'credit' ? -Math.abs(magnitude) : Math.abs(magnitude);
		const body: { amount: number; asOfDate: string; note?: string } = {
			amount: signed,
			asOfDate: openingAsOfDate,
		};
		const note = openingNote.trim();
		if (note) body.note = note;
		saveOpeningMutation.mutate(body);
	}

	// Form (edit dialog) state
	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
	}));
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId, { take: 200 }),
	}));

	let formPropertyId = $state('');
	const unitsForPropertyQuery = createQuery(() => ({
		queryKey: ['units-for-lease', formPropertyId],
		enabled: !!formPropertyId,
		queryFn: () => properties.listUnits(Number(formPropertyId)),
	}));

	const empty = {
		leaseNumber: '', propertyId: '', unitId: '', tenantId: '', tenantIds: [] as string[], startDate: '', endDate: '', moveInDate: '',
		monthlyRent: '', securityDeposit: '', lateFeeAmount: '75', rentDueDay: '1', status: 'Draft', notes: '',
	};
	let editing = $state(false);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);
	let autoDefaultedEndDate = $state('');

	$effect(() => {
		if (form.propertyId !== formPropertyId) {
			formPropertyId = form.propertyId;
		}
	});

	function invalidateLease(updatedLease?: Lease | null) {
		const unitId = updatedLease?.unitId ?? lease?.unitId;

		queryClient.invalidateQueries({ queryKey: ['lease', leaseId] });
		queryClient.invalidateQueries({ queryKey: ['leases', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['dashboard', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['units'] });
		queryClient.invalidateQueries({ queryKey: ['units-for-lease'] });

		if (unitId) {
			queryClient.invalidateQueries({ queryKey: ['unit-dashboard', unitId] });
			queryClient.invalidateQueries({ queryKey: ['unit-leases', portfolioId, unitId] });
			queryClient.invalidateQueries({ queryKey: ['unit-timeline', unitId] });
		}
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => leases.update(leaseId, data),
		onSuccess: () => {
			showSuccess('Lease updated.');
			editing = false;
			formErrors = {};
			invalidateLease();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: (payload: Record<string, unknown>) => leases.update(leaseId, payload),
		onSuccess: (updatedLease) => {
			queryClient.setQueryData(['lease', leaseId], updatedLease);
			showSuccess('Lease status updated.');
			showSetActiveConfirm = false;
			pendingStatusAction = null;
			invalidateSignatureStatus(updatedLease);
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// --- Lifecycle confirms (F11) — Set Active is no longer a one-click write. ---
	let showSetActiveConfirm = $state(false);
	let pendingStatusAction = $state<LeaseQuickStatusAction | null>(null);
	let showNoticeDialog = $state(false);

	const pendingStatusActionTitle = $derived.by(() => {
		switch (pendingStatusAction?.targetStatus) {
			case 'Expired':
				return 'Mark lease expired?';
			case 'Terminated':
				return 'Terminate lease?';
			default:
				return 'Update lease status?';
		}
	});
	const pendingStatusActionMessage = $derived.by(() => {
		const leaseLabel = lease?.leaseNumber ? `lease ${lease.leaseNumber}` : 'this lease';

		switch (pendingStatusAction?.targetStatus) {
			case 'Expired':
				return `Mark ${leaseLabel} as Expired? Use this when the lease term has ended. This does not regenerate or modify the original lease agreement.`;
			case 'Terminated':
				return `Mark ${leaseLabel} as Terminated? Use this for an early end or legal termination. This changes the lifecycle status but keeps the original lease agreement intact.`;
			default:
				return '';
		}
	});

	function openNoticeDialog() {
		showNoticeDialog = true;
	}
	function confirmSetActive() {
		statusMutation.mutate({ status: 'Active' });
	}
	function confirmPendingStatusAction() {
		if (!pendingStatusAction) return;
		statusMutation.mutate({ status: pendingStatusAction.targetStatus });
	}

	const deleteMutation = createMutation(() => ({
		mutationFn: () => leases.delete(leaseId),
		onSuccess: () => {
			showSuccess('Lease deleted.');
			onDeleted();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const markPaidMutation = createMutation(() => ({
		mutationFn: (paymentId: number) => payments.markPaid(paymentId, {}),
		onSuccess: () => {
			showSuccess('Payment marked as paid.');
			queryClient.invalidateQueries({ queryKey: ['payments', portfolioId, leaseId] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// --- Lease agreement PDF (generate + authed blob download) ---
	// Whether a generated agreement already exists. Status is checked without
	// streaming the PDF so a missing agreement does not create a browser 404.
	let hasDocument = $state(false);
	let documentChecked = $state(false);
	let downloadingDocument = $state(false);

	$effect(() => {
		if (leaseId > 0 && !documentChecked) {
			documentChecked = true;
			leases
				.documentStatus(leaseId)
				.then((status) => {
					hasDocument = status.hasDocument;
				})
				.catch(() => {
					// Status failure is non-fatal — leave the Generate button available.
				});
		}
	});

	const generateDocMutation = createMutation(() => ({
		mutationFn: () => leases.generateDocument(leaseId),
		onSuccess: () => {
			hasDocument = true;
			showSuccess('Lease agreement created. You can download it now.');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	async function handleDocumentDownload() {
		downloadingDocument = true;
		try {
			const ok = await leases.downloadDocument(leaseId);
			if (!ok) {
				hasDocument = false;
				showError('No lease agreement has been generated yet.');
			}
		} catch {
			showError('Could not download the lease agreement. Please try again.');
		} finally {
			downloadingDocument = false;
		}
	}

	// --- E-signature (gated: provider stays dormant until keys are set) ---
	// Whether the e-sign provider replied "not configured" (HTTP 503). When true we
	// show a calm, unobtrusive note instead of a scary error.
	let esignNotConfigured = $state(false);
	let downloadingSignedDocument = $state(false);
	let copiedSigningUrlItemId = $state<number | null>(null);

	const signatureStatusQuery = createQuery(() => ({
		queryKey: ['lease-signature-status', leaseId],
		queryFn: () => leases.signatureStatus(leaseId),
		enabled: leaseId > 0,
		// While we're waiting on a signature, poll so the card flips to "Signed"
		// automatically once the tenant signs. Stop polling otherwise.
		refetchInterval: (query) => (query.state.data?.esignStatus === 'Sent' ? 5000 : false),
	}));
	const signature = $derived(signatureStatusQuery.data);

	const signatureQueueQuery = createQuery(() => ({
		queryKey: ['lease-signature-queue', leaseId],
		queryFn: () => leases.signatureQueue(leaseId),
		enabled: leaseId > 0,
		refetchInterval: (query) =>
			(query.state.data?.items ?? []).some((item) => item.status === 'Queued' || item.status === 'Retrying')
				? 5000
				: false,
	}));
	const signatureQueueItems = $derived(signatureQueueQuery.data?.items ?? []);
	const latestSignatureQueueItem = $derived(signatureQueueItems[0] ?? null);
	const latestSignatureEmailStatus = $derived(latestSignatureQueueItem?.status ?? null);
	const visibleStatus = $derived(visibleLeaseStatus(lease?.status, signature));
	const canActivateLease = $derived(canSetLeaseActive(visibleStatus ?? lease?.status));
	const terminalStatusActions = $derived(
		quickLeaseStatusActionsForStatus(visibleStatus ?? lease?.status).filter(
			(action) => action.targetStatus !== 'Active'
		)
	);
	const canSendForSignature = $derived(canShowLeaseSignatureSendAction(lease?.status, signature));
	const visibleLease = $derived(lease && visibleStatus ? { ...lease, status: visibleStatus } : lease);
	const leaseDeleteState = $derived(getLeaseDeleteState(visibleLease));

	$effect(() => {
		if (lease?.status && signature?.leaseStatus && lease.status !== signature.leaseStatus) {
			invalidateLease();
		}
	});

	const esignStatusLabel = $derived.by(() => {
		switch (signature?.esignStatus) {
			case 'Sent':
				return 'Sent — waiting for signature';
			case 'Signed':
				return 'Signed';
			case 'Declined':
				return 'Declined';
			default:
				return 'Not sent';
		}
	});

	function invalidateSignatureStatus(updatedLease?: Lease | null) {
		queryClient.invalidateQueries({ queryKey: ['lease-signature-status', leaseId] });
		// Status changes flip the lease status too (e.g. PendingSignature).
		invalidateLease(updatedLease);
	}

	function invalidateSignatureQueue() {
		queryClient.invalidateQueries({ queryKey: ['lease-signature-queue', leaseId] });
	}

	const sendForSignatureMutation = createMutation(() => ({
		mutationFn: () => leases.sendForSignature(leaseId),
		onSuccess: (result) => {
			esignNotConfigured = false;
			hasDocument = hasAgreementAfterSignatureSend(hasDocument, result);
			showSuccess(leaseSignatureQueuedMessage(lease?.tenantName));
			invalidateSignatureStatus();
			invalidateSignatureQueue();
		},
		onError: (err) => {
			// 503 = no e-sign provider configured. Stay calm: show an inline note, not a toast.
			if (err instanceof ApiError && err.status === 503) {
				esignNotConfigured = true;
				return;
			}
			showError(apiErrorMessage(err));
		},
	}));

	async function handleSignedDocumentDownload() {
		downloadingSignedDocument = true;
		try {
			const ok = await leases.downloadSignedDocument(leaseId);
			if (!ok) {
				showError('The signed lease isn’t available yet.');
				invalidateSignatureStatus();
			}
		} catch {
			showError('Could not download the signed lease. Please try again.');
		} finally {
			downloadingSignedDocument = false;
		}
	}

	async function copySigningUrl(item: LeaseSignatureQueueItemResponse) {
		if (!item.signingUrl) return;
		try {
			await navigator.clipboard.writeText(item.signingUrl);
			copiedSigningUrlItemId = item.id;
			showSuccess('Signing link copied.');
			setTimeout(() => {
				if (copiedSigningUrlItemId === item.id) copiedSigningUrlItemId = null;
			}, 2000);
		} catch {
			showError('Could not copy the signing link.');
		}
	}

	function startEditing() {
		if (!lease) return;
		const tenantIds = (lease.tenantIds?.length
			? lease.tenantIds
			: lease.tenants?.length
				? lease.tenants.map((tenant) => tenant.id)
				: [lease.tenantId]).map((id) => String(id));
		form = {
			leaseNumber: lease.leaseNumber,
			propertyId: String(lease.propertyId),
			unitId: String(lease.unitId),
			tenantId: tenantIds[0] ?? String(lease.tenantId),
			tenantIds,
			startDate: lease.startDate?.slice(0, 10) ?? '',
			endDate: lease.endDate?.slice(0, 10) ?? '',
			moveInDate: lease.moveInDate?.slice(0, 10) ?? '',
			monthlyRent: String(lease.monthlyRent),
			securityDeposit: String(lease.securityDeposit),
			lateFeeAmount: String(lease.lateFeeAmount),
			rentDueDay: String(lease.rentDueDay),
			status: lease.status,
			notes: lease.notes ?? '',
		};
		formPropertyId = String(lease.propertyId);
		formErrors = {};
		autoDefaultedEndDate = '';
		setTab(tabForLeaseEdit(activeTab));
		editing = true;
	}
	function cancelEditing() {
		editing = false;
		formErrors = {};
		autoDefaultedEndDate = '';
	}

	function handleLeaseStartDateChange(iso: string) {
		form.startDate = iso;
		if (!iso) return;
		const defaultEndDate = addCalendarYear(iso);
		if (!defaultEndDate) return;
		if (!form.endDate || form.endDate === autoDefaultedEndDate) {
			form.endDate = defaultEndDate;
			autoDefaultedEndDate = defaultEndDate;
		}
	}

	function handleLeaseEndDateChange(iso: string) {
		form.endDate = iso;
		if (iso !== autoDefaultedEndDate) autoDefaultedEndDate = '';
	}

	function submit() {
		// Property is fixed at lease creation (UpdateLeaseRequest has no PropertyId),
		// so propertyId is excluded from the payload — and must also be omitted from validation, or
		// the required propertyId in leaseSchema fails on the dropped key and Save silently bails.
		const selectedTenantIds = form.tenantIds
			.map((id) => Number(id))
			.filter((id) => Number.isInteger(id) && id > 0);
		const tenantId = String(selectedTenantIds[0] ?? '');
		form.tenantId = tenantId;
		const { propertyId: _p, ...rest } = form;
		const result = parseForm(leaseSchema.omit({ propertyId: true }), { ...rest, tenantId });
		const tenantErrors: Record<string, string> =
			selectedTenantIds.length === 0 ? { tenantId: 'Select at least one tenant' } : {};
		if (result.errors || Object.keys(tenantErrors).length > 0) {
			formErrors = { ...(result.errors ?? {}), ...tenantErrors };
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data, tenantId: selectedTenantIds[0], tenantIds: selectedTenantIds });
	}

	// Select options for inline FK/enum fields
	const statusOptions = $derived(
		leaseStatusOptionsForCurrentStatus(visibleStatus ?? lease?.status, LEASE_STATUSES).map((value) => ({
			value,
			label: formatStatusLabel(value),
		}))
	);
	const propertyOptions = $derived(
		ensureSelectedOption(
			[
				{ value: '', label: 'Select property' },
				...(propertiesQuery.data ?? []).map((p) => ({ value: String(p.id), label: p.name })),
			],
			form.propertyId,
			lease?.propertyName ?? ''
		)
	);
	const unitOptions = $derived(
		ensureSelectedOption(
			[
				{ value: '', label: 'Select unit' },
				...(unitsForPropertyQuery.data ?? []).map((u) => ({
					value: String(u.id),
					label: `Unit ${u.unitNumber} (${u.status})`,
				})),
			],
			form.unitId,
			lease?.unitNumber ? `Unit ${lease.unitNumber}` : ''
		)
	);
	// Payments DataGrid columns
	const paymentColumns: ColumnDef<Payment>[] = [
		{
			key: 'dueDate',
			title: 'Due Date',
			format: 'date',
			sortable: true,
			mobileRole: 'title',
		},
		{
			key: 'type',
			title: 'Type',
			mobileRole: 'subtitle',
			// Payment serializes the field as `paymentType` (not `type`); without this accessor the
			// column rendered blank for every row. Humanize the enum for display (M-13).
			accessor: (p) => paymentTypeLabel(p.paymentType),
		},
		{
			key: 'amount',
			title: 'Amount',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: paymentStatusCell,
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			align: 'right',
			cell: paymentActionsCell,
		},
	];

	function formatCurrency(val: number): string {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(val);
	}

	function formatDate(val: string | undefined): string {
		if (!val) return '—';
		// Date-only fields (start/end/move-in/move-out, ledger dates) are stored as
		// UTC-midnight; format UTC-pinned so they don't slip back a day. See formatDateOnly.
		return formatDateOnly(val) || val;
	}

	function formatDateTime(val: string | null | undefined): string {
		if (!val) return '—';
		const date = new Date(val);
		if (Number.isNaN(date.getTime())) return val;
		return new Intl.DateTimeFormat('en-US', {
			month: 'short',
			day: 'numeric',
			hour: 'numeric',
			minute: '2-digit',
		}).format(date);
	}

	function queueStatusLabel(status: string): string {
		if (status === 'DeliveryDisabled') return 'Not delivered';
		if (status === 'Retrying') return 'Retrying';
		if (status === 'Failed') return 'Failed';
		if (status === 'Sent') return 'Sent';
		return 'Queued';
	}

	function queueStatusClass(status: string): string {
		const base = 'inline-flex shrink-0 items-center rounded-full border px-2 py-0.5 text-[11px] font-medium';
		if (status === 'Sent') return `${base} border-success/40 bg-success/10 text-success`;
		if (status === 'Failed') return `${base} border-destructive/40 bg-destructive/10 text-destructive`;
		if (status === 'DeliveryDisabled') return `${base} border-warning/40 bg-warning/10 text-warning`;
		if (status === 'Retrying') return `${base} border-warning/40 bg-warning/10 text-warning`;
		return `${base} border-border bg-muted/40 text-muted-foreground`;
	}

	function queueErrorClass(status: string): string {
		return status === 'DeliveryDisabled'
			? 'mt-1 line-clamp-2 text-xs text-warning'
			: 'mt-1 line-clamp-2 text-xs text-destructive';
	}

	function queueWhenLabel(item: LeaseSignatureQueueItemResponse): string {
		const label =
			item.status === 'Sent'
				? 'Sent'
				: item.status === 'Failed'
					? 'Failed'
					: item.status === 'DeliveryDisabled'
						? 'Not delivered'
						: item.status === 'Retrying'
							? 'Retrying since'
							: 'Queued';
		const parts = [`${label} ${formatDateTime(item.statusAt)}`];
		if (item.retryCount > 0 && item.status !== 'Sent') {
			parts.push(`${item.retryCount} ${item.retryCount === 1 ? 'attempt' : 'attempts'}`);
		}
		return parts.join(' · ');
	}

	// Active tab for the detail page. Kept as LOCAL state (not a URL param) so the inner
	// tabs never collide with the unit Command Center's own ?tab= when this component is
	// folded into the Lease tab. The generic /leases/[id] route seeds the initial inner tab
	// via the `initialTab` prop (its ?tab= deep-link); switching tabs here does NOT touch the URL.
	// Seed once from the prop (the generic route's ?tab= deep-link); thereafter purely local.
	let activeTab = $state(untrack(() => resolveLeaseDetailTab(initialTab)));
	const tabs = [
		{ value: 'overview', label: 'Overview' },
		{ value: 'agreement', label: 'Agreement & Signing' },
		{ value: 'ledger', label: 'Ledger' },
		{ value: 'history', label: 'History' },
	];

	function setTab(tab: string) {
		activeTab = resolveLeaseDetailTab(tab);
	}

	// Context tone for the hero wash — status-keyed (see HeroCard for the recipe).
	const heroTone = $derived.by<HeroTone>(() => {
		switch (visibleStatus) {
			case 'Active': return 'success';
			case 'Expired':
			case 'Terminated': return 'destructive';
			case 'NoticeGiven': return 'warning';
			default: return 'primary';
		}
	});
</script>

{#snippet paymentStatusCell(payment: Payment)}
	<StatusBadge status={payment.status} />
{/snippet}

{#snippet paymentActionsCell(payment: Payment)}
	{#if payment.status !== 'Paid' && payment.status !== 'Waived'}
		<Button
			variant="outline"
			size="sm"
			onclick={(e) => { e.stopPropagation(); markPaidMutation.mutate(payment.id); }}
			disabled={markPaidMutation.isPending}
		>
			Mark paid
		</Button>
	{/if}
{/snippet}

<svelte:head>
	<title>{lease ? `Lease ${lease.leaseNumber}` : 'Lease'} - Rental Command</title>
</svelte:head>

<!-- Inline date field: mirrors InlineField's edit/display structure (same testids) but
     uses the shared DatePicker when editing. value is bound `yyyy-MM-dd`. -->
{#snippet dateField(opts: {
	label: string;
	value: string;
	setValue: (v: string) => void;
	display: string;
	testid: string;
	error?: string;
	min?: string;
	max?: string;
})}
	<div data-testid={`${opts.testid}-field`}>
		{#if editing}
			<label class="mb-1 block text-xs font-medium text-muted-foreground" for={`${opts.testid}-input`}>{opts.label}</label>
			<DatePicker
				id={`${opts.testid}-input`}
				testid={`${opts.testid}-input`}
				value={opts.value}
				onchange={opts.setValue}
				placeholder={opts.label}
				min={opts.min}
				max={opts.max}
			/>
			{#if opts.error}<p class="mt-1 text-xs text-destructive" data-testid={`${opts.testid}-error`}>{opts.error}</p>{/if}
		{:else}
			<div class="m3-readonly-field flex flex-col justify-center" data-testid={`${opts.testid}-value`}>
				<span class="m3-readonly-field__label">{opts.label}</span>
				<span class="m3-readonly-field__value mt-1">{opts.display === '' ? '-' : opts.display}</span>
			</div>
		{/if}
	</div>
{/snippet}

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="lease-detail-page">
	{#if leaseQuery.isLoading}
		<div class="flex h-48 items-center justify-center" data-testid="lease-detail-loading">
			<div class="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
		</div>
	{:else if leaseQuery.isError || !lease}
		<div class="flex h-48 flex-col items-center justify-center gap-3 text-center" data-testid="lease-detail-not-found">
			<p class="text-sm font-medium">Lease not found.</p>
		</div>
	{:else}
		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start gap-3">
			<div class="flex-1">
				<div class="flex flex-wrap items-center gap-2">
					<h1 class="text-2xl font-bold" data-testid="lease-detail-number">{lease.leaseNumber}</h1>
					<StatusBadge status={visibleStatus ?? lease.status} />
				</div>
				<p class="mt-1 flex flex-wrap items-center gap-x-1.5 gap-y-1 text-sm text-muted-foreground" data-testid="lease-detail-context-links">
					{#if lease.propertyName}
						<a href="/properties/{lease.propertyId}" class="font-medium text-foreground underline-offset-4 hover:underline" data-testid="lease-detail-header-property-link">{lease.propertyName}</a>
					{/if}
					{#if lease.unitNumber}
						{#if lease.propertyName}<span aria-hidden="true">·</span>{/if}
						<a href="/units/{lease.unitId}?tab=lease" class="font-medium text-foreground underline-offset-4 hover:underline" data-testid="lease-detail-header-unit-link">Unit {lease.unitNumber}</a>
					{/if}
					{#if lease.tenants?.length || lease.tenantName}
						{#if lease.propertyName || lease.unitNumber}<span aria-hidden="true">·</span>{/if}
						{#if lease.tenants?.length}
							<span class="inline-flex flex-wrap items-center gap-x-1.5 gap-y-1">
								{#each lease.tenants as tenant, index}
									<a href="/tenants/{tenant.id}" class="font-medium text-foreground underline-offset-4 hover:underline" data-testid="lease-detail-header-tenant-link">{tenant.name}</a>
									{#if index < (lease.tenants?.length ?? 0) - 1}
										<span aria-hidden="true">+</span>
									{/if}
								{/each}
							</span>
						{:else}
							<a href="/tenants/{lease.tenantId}" class="font-medium text-foreground underline-offset-4 hover:underline" data-testid="lease-detail-header-tenant-link">{lease.tenantName}</a>
						{/if}
					{/if}
				</p>
			</div>
			<div class="flex flex-wrap items-center gap-2">
				{#if editing}
					<Button data-testid="lease-detail-cancel" variant="outline" size="sm" class="gap-1.5" onclick={cancelEditing} disabled={saveMutation.isPending}>
						<X class="h-4 w-4" />
						Cancel
					</Button>
					<Button data-testid="lease-detail-save" size="sm" class="gap-1.5" onclick={submit} disabled={saveMutation.isPending}>
						<Save class="h-4 w-4" />
						{saveMutation.isPending ? 'Saving…' : 'Save'}
					</Button>
				{:else}
					{#if canActivateLease}
						<Button data-testid="lease-set-active" variant="outline" size="sm" onclick={() => (showSetActiveConfirm = true)} disabled={statusMutation.isPending}>
							Set Active
						</Button>
					{:else if visibleStatus === 'Active'}
						<Button data-testid="lease-give-notice" variant="outline" size="sm" class="gap-1.5" onclick={openNoticeDialog}>
							<BellRing class="h-4 w-4" />
							Create / Send notice
						</Button>
					{/if}
					{#each terminalStatusActions as action (action.targetStatus)}
						<Button
							data-testid="lease-set-{action.targetStatus.toLowerCase()}"
							variant="outline"
							size="sm"
							class="gap-1.5 {action.targetStatus === 'Terminated' ? 'hover:text-destructive' : ''}"
							onclick={() => (pendingStatusAction = action)}
							disabled={statusMutation.isPending}
						>
							{#if action.targetStatus === 'Expired'}
								<CalendarRange class="h-4 w-4" />
							{:else}
								<X class="h-4 w-4" />
							{/if}
							{action.label}
						</Button>
					{/each}
					<Button data-testid="lease-edit" variant="outline" size="sm" class="gap-1.5" onclick={startEditing}>
						<Pencil class="h-4 w-4" />
						Edit
					</Button>
					<Button data-testid="lease-delete" variant="outline" size="sm" class="gap-1.5 hover:text-destructive" onclick={() => (showDeleteConfirm = true)}>
						<Trash2 class="h-4 w-4" />
						Delete
					</Button>
				{/if}
			</div>
		</div>

		<Tabs.Root value={activeTab} onValueChange={setTab} class="w-full">
			<Tabs.List class="mb-6" data-testid="lease-detail-tabs">
				{#each tabs as t}
					<Tabs.Trigger value={t.value} data-testid="lease-tab-{t.value}">{t.label}</Tabs.Trigger>
				{/each}
			</Tabs.List>

			<!-- ───────────────────────── OVERVIEW ───────────────────────── -->
			<Tabs.Content value="overview" class="space-y-6">
				<!-- Hero: the few things that matter most + a state-aware primary CTA -->
				<HeroCard tone={heroTone} testid="lease-hero" contentClass="flex flex-wrap items-end justify-between gap-6">
						<div class="min-w-0">
							<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Monthly Rent</p>
							<p class="mt-1 font-mono text-4xl font-bold tabular-nums tracking-tight" data-testid="lease-hero-rent">
								{formatCurrency(lease.monthlyRent)}
							</p>
							<div class="mt-3 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted-foreground">
								<StatusBadge status={visibleStatus ?? lease.status} />
								{#if lease.tenants?.length}
									{#each lease.tenants as tenant, index}
										<a href="/tenants/{tenant.id}" class="font-medium text-foreground underline-offset-4 hover:underline">{tenant.name}</a>
										{#if index < (lease.tenants?.length ?? 0) - 1}
											<span aria-hidden="true">+</span>
										{/if}
									{/each}
								{:else if lease.tenantName}
									<a href="/tenants/{lease.tenantId}" class="font-medium text-foreground underline-offset-4 hover:underline">{lease.tenantName}</a>
								{/if}
								{#if lease.propertyName}
									<span aria-hidden="true">·</span>
									<span><a href="/properties/{lease.propertyId}" class="underline-offset-4 hover:underline">{lease.propertyName}</a>{#if lease.unitNumber}, <a href="/units/{lease.unitId}?tab=lease" class="underline-offset-4 hover:underline" data-testid="lease-detail-unit-link">Unit {lease.unitNumber}</a>{/if}</span>
								{/if}
							</div>
						</div>
						<div class="flex flex-col items-stretch gap-2">
							{#if ledger && Math.abs(ledger.balance) > 0.005}
								<div class="rounded-lg border border-border bg-background/70 px-4 py-2 text-right">
									<p class="text-[11px] uppercase tracking-wide text-muted-foreground">Balance</p>
									<p class="font-mono text-lg font-bold tabular-nums {ledger.balance > 0.005 ? 'text-warning' : 'text-success'}">{formatCurrency(ledger.balance)}</p>
								</div>
							{/if}
							{#if !editing}
								{#if visibleStatus === 'Active' || !canActivateLease}
									<Button data-testid="lease-hero-cta" size="sm" class="gap-1.5" onclick={() => setTab('ledger')}>
										<DollarSign class="h-4 w-4" />
										View ledger
									</Button>
								{:else}
									<Button data-testid="lease-hero-cta" size="sm" class="gap-1.5" onclick={() => (showSetActiveConfirm = true)} disabled={statusMutation.isPending}>
										Set lease active
									</Button>
								{/if}
							{/if}
						</div>
				</HeroCard>

				<!-- Grouped detail cards. Editing is an explicit, page-level state
				     (the Edit button) — values are calm, non-clickable rows otherwise. -->
				<div class="grid gap-6 lg:grid-cols-2">
					<DetailCard title="Term" icon={CalendarRange} accent="primary" testid="lease-card-term" contentClass="grid grid-cols-2 gap-x-6 gap-y-4">
						{@render dateField({ label: 'Start Date', value: form.startDate, setValue: handleLeaseStartDateChange, display: formatDate(lease.startDate), error: formErrors.startDate, max: form.endDate || undefined, testid: 'lease-detail-start' })}
						{@render dateField({ label: 'End Date', value: form.endDate, setValue: handleLeaseEndDateChange, display: formatDate(lease.endDate), error: formErrors.endDate, min: form.startDate || undefined, testid: 'lease-detail-end' })}
						<!-- Move-in is distinct from lease start (tenant may take possession on a different day)
						     and is optional/clearable, so it gets its own editable date field. -->
						{@render dateField({ label: 'Move-In', value: form.moveInDate, setValue: (v) => (form.moveInDate = v), display: formatDate(lease.moveInDate), error: formErrors.moveInDate, testid: 'lease-detail-move-in' })}
						<InlineField label="Rent Due Day" bind:value={form.rentDueDay} display={`Day ${lease.rentDueDay}`} {editing} type="number" maxlength={2} error={formErrors.rentDueDay} testid="lease-detail-due-day" />
						<InlineField label="Status" bind:value={form.status} display={formatStatusLabel(visibleStatus ?? lease.status)} {editing} type="select" options={statusOptions} error={formErrors.status} testid="lease-detail-status" />
						{#if !editing && lease.moveOutDate}
							<div>
								<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Move-Out</dt>
								<dd class="mt-0.5 text-sm">{formatDate(lease.moveOutDate)}</dd>
							</div>
						{/if}
					</DetailCard>

					<DetailCard title="Financials" icon={DollarSign} accent="success" testid="lease-card-financials" contentClass="grid grid-cols-2 gap-x-6 gap-y-4">
						<InlineField label="Monthly Rent" bind:value={form.monthlyRent} display={formatCurrency(lease.monthlyRent)} {editing} type="number" error={formErrors.monthlyRent} testid="lease-detail-rent" />
						<InlineField label="Security Deposit" bind:value={form.securityDeposit} display={formatCurrency(lease.securityDeposit)} {editing} type="number" error={formErrors.securityDeposit} testid="lease-detail-deposit" />
						<InlineField label="Late Fee" bind:value={form.lateFeeAmount} display={formatCurrency(lease.lateFeeAmount)} {editing} type="number" error={formErrors.lateFeeAmount} testid="lease-detail-late-fee" />
						<InlineField label="Lease Number" bind:value={form.leaseNumber} display={lease.leaseNumber} {editing} error={formErrors.leaseNumber} testid="lease-detail-number-field" />
					</DetailCard>

					<DetailCard title="Parties" icon={Users} accent="primary" testid="lease-card-parties" contentClass="grid grid-cols-2 gap-x-6 gap-y-4">
						<InlineField label="Property" bind:value={form.propertyId} display={lease.propertyName} {editing} type="select" options={propertyOptions} testid="lease-detail-property" />
						<InlineField label="Unit" bind:value={form.unitId} display={lease.unitNumber ? `Unit ${lease.unitNumber}` : ''} {editing} type="select" options={unitOptions} error={formErrors.unitId} testid="lease-detail-unit" />
						{#if editing}
							<div class="col-span-2">
								<TenantMultiSelect
									label="Tenants"
									tenants={tenantsQuery.data ?? []}
									bind:selectedIds={form.tenantIds}
									error={formErrors.tenantId}
									testid="lease-detail-tenants"
								/>
							</div>
						{:else}
							<div class="col-span-2">
								<div class="m3-readonly-field flex flex-col justify-center" data-testid="lease-detail-tenants-value">
									<span class="m3-readonly-field__label">Tenants</span>
									<span class="m3-readonly-field__value mt-1">{lease.tenantName || '-'}</span>
								</div>
							</div>
						{/if}
					</DetailCard>

					<DetailCard title="Notes" icon={StickyNote} accent="muted" testid="lease-card-notes">
						<InlineField label="Notes" bind:value={form.notes} display={lease.notes} {editing} type="textarea" error={formErrors.notes} testid="lease-detail-notes" />
					</DetailCard>

					<!-- Original scanned document this lease was created from, served through the
					     same-origin /lease-file proxy (distinct from the generated/e-signed agreement
					     in the Agreement tab). Renders only when a scanned source file exists. -->
					{#if lease.hasScan}
						<DetailCard title="Scanned document" icon={FileText} accent="muted" testid="lease-card-scanned-document" class="lg:col-span-2">
							<div class="flex flex-col items-start gap-2">
								<p class="text-xs text-muted-foreground">The original document this lease was created from.</p>
								{#if lease.scanIsImage}
									<a
										href="/lease-file/{lease.id}"
										target="_blank"
										rel="noopener noreferrer"
										data-testid="lease-detail-scanned-document-link"
										aria-label="View scanned document full size"
										class="group inline-block"
									>
										<img
											src="/lease-file/{lease.id}?thumb=true"
											alt="Scanned document preview"
											class="max-h-80 w-auto rounded-md border border-border object-contain transition group-hover:ring-2 group-hover:ring-primary"
											loading="lazy"
										/>
										<span class="mt-1 block text-xs text-primary underline underline-offset-2 group-hover:text-primary/80">{scannedLeaseDocumentLinkLabel(true)}</span>
									</a>
								{:else}
									<a
										href="/lease-file/{lease.id}"
										target="_blank"
										rel="noopener noreferrer"
										data-testid="lease-detail-scanned-document-link"
										class="inline-flex items-center gap-2 rounded-md border border-border bg-background px-4 py-3 text-sm font-medium text-foreground transition hover:border-primary hover:text-primary"
									>
										<FileText class="h-5 w-5 shrink-0" />
										<span>{scannedLeaseDocumentLinkLabel(false)}</span>
									</a>
								{/if}
							</div>
						</DetailCard>
					{/if}
				</div>
			</Tabs.Content>

			<!-- ───────────────────── AGREEMENT & SIGNING ───────────────────── -->
			<Tabs.Content value="agreement" class="space-y-6">
				<Card.Root data-testid="lease-template-availability-card">
					<Card.Header>
						<div class="flex flex-wrap items-start justify-between gap-3">
							<div>
								<Card.Title class="flex items-center gap-2 text-base">
									<Library class="h-4 w-4 text-primary" />
									Custom lease templates
								</Card.Title>
								<Card.Description>
									Use landlord-uploaded PDFs once their dynamic fields have been placed.
								</Card.Description>
							</div>
							<Button href="/lease-templates" variant="outline" size="sm" class="gap-1.5" data-testid="lease-template-library-link">
								<ExternalLink class="h-4 w-4" />
								Open library
							</Button>
						</div>
					</Card.Header>
					<Card.Content>
						{#if leaseTemplatesQuery.isLoading}
							<div class="grid gap-2 sm:grid-cols-3" data-testid="lease-template-availability-loading">
								{#each Array.from({ length: 3 }) as _}
									<div class="h-16 animate-pulse rounded-md bg-muted/35"></div>
								{/each}
							</div>
						{:else if leaseTemplatesQuery.isError}
							<p class="text-sm text-muted-foreground" data-testid="lease-template-availability-error">
								Template library is unavailable right now.
							</p>
						{:else if leaseTemplateCount === 0}
							<p class="text-sm text-muted-foreground" data-testid="lease-template-availability-empty">
								No custom lease template has been uploaded yet.
							</p>
						{:else}
							<div class="space-y-3" data-testid="lease-template-availability-list">
								<div class="flex flex-wrap items-center gap-2 text-sm">
									<span class="font-medium">{leaseTemplateCount} custom lease {leaseTemplateCount === 1 ? 'template' : 'templates'} available</span>
									<span class="text-muted-foreground">The current generated agreement remains available below.</span>
								</div>
								<div class="grid gap-2 sm:grid-cols-3">
									{#each leaseTemplatePreview as template (template.id)}
										<div class="rounded-md border border-border bg-muted/20 px-3 py-2">
											<p class="truncate text-sm font-medium">{template.name}</p>
											<p class="mt-0.5 text-xs text-muted-foreground">
												{template.fieldCount === 0
													? 'No fields placed yet'
													: `${template.fieldCount} field${template.fieldCount === 1 ? '' : 's'} placed`}
											</p>
										</div>
									{/each}
								</div>
							</div>
						{/if}
					</Card.Content>
				</Card.Root>

				{#if lease.hasScan}
					<DetailCard title="Original source lease agreement" icon={FileText} accent="warning" testid="lease-original-source-document-card">
						<div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
							<div class="space-y-1">
								<p class="text-sm font-medium text-foreground">Uploaded/scanned original</p>
								<p class="max-w-2xl text-xs leading-relaxed text-muted-foreground">
									This is the original source file kept as the legal record. It is not the generated agreement, the e-signed copy, or a regenerated PDF.
								</p>
							</div>
							<div class="flex flex-wrap items-center gap-2">
								<Button
									href="/lease-file/{lease.id}"
									target="_blank"
									rel="noopener noreferrer"
									size="sm"
									class="gap-1.5"
									data-testid="lease-original-source-document-open"
								>
									<ExternalLink class="h-4 w-4" />
									Open original
								</Button>
								<Button
									href="/lease-file/{lease.id}"
									download
									variant="outline"
									size="sm"
									class="gap-1.5"
									data-testid="lease-original-source-document-download"
								>
									<Download class="h-4 w-4" />
									Download original
								</Button>
							</div>
						</div>
					</DetailCard>
				{/if}

		<!-- Lease agreement PDF — generate, then download an authed blob -->
		<Card.Root data-testid="lease-agreement-card">
			<Card.Header>
				<Card.Title class="text-base">Lease Agreement</Card.Title>
				<Card.Description>Create a printable lease-agreement PDF from this lease's details, download it, or send it to the tenant to sign.</Card.Description>
			</Card.Header>
			<Card.Content>
				<div class="flex flex-wrap items-center gap-2">
					<Button
						data-testid="lease-generate-document"
						variant={hasDocument ? 'outline' : 'default'}
						size="sm"
						class="gap-1.5"
						onclick={() => generateDocMutation.mutate()}
						disabled={generateDocMutation.isPending}
					>
						<FileText class="h-4 w-4" />
						{generateDocMutation.isPending
							? 'Generating…'
							: hasDocument
								? 'Regenerate lease agreement (PDF)'
								: 'Generate lease agreement (PDF)'}
					</Button>
					{#if hasDocument}
						<Button
							data-testid="lease-download-document"
							size="sm"
							class="gap-1.5"
							onclick={handleDocumentDownload}
							disabled={downloadingDocument}
						>
							<Download class="h-4 w-4" />
							{downloadingDocument ? 'Preparing…' : 'Download agreement'}
						</Button>
					{/if}
				</div>
				{#if !hasDocument && !generateDocMutation.isPending}
					<p class="mt-2 text-xs text-muted-foreground">No agreement has been generated yet.</p>
				{/if}

				<!-- E-signature: send the lease to the tenant to sign, track status, download the signed copy -->
				<div class="mt-4 border-t border-border pt-4" data-testid="lease-esign">
					<div class="flex flex-wrap items-center justify-between gap-2">
						<div class="flex items-center gap-2">
							<p class="text-sm font-medium">E-signature</p>
							<HelpPopover
								title="Send for signature"
								summary="Queues the tenant a secure link to sign this lease online — no printing or scanning."
								detail="The email queue below shows whether the signing email was delivered, blocked, or failed. Once they sign, the lease flips to signed and the executed copy is saved here for download."
								learnMoreUrl={undefined}
							/>
							<span
								class="inline-flex items-center rounded-full border border-border bg-muted/40 px-2 py-0.5 text-xs font-medium text-muted-foreground"
								data-testid="lease-esign-status"
							>
								{esignStatusLabel}
							</span>
							{#if signature?.leaseStatus === 'PendingSignature'}
								<span
									class="inline-flex items-center rounded-full border border-warning/40 bg-warning/10 px-2 py-0.5 text-xs font-medium text-warning"
									data-testid="lease-esign-pending"
								>
									Lease pending signature
								</span>
							{/if}
						</div>
						<div class="flex flex-wrap items-center gap-2">
							{#if canSendForSignature}
								<Button
									data-testid="lease-send-for-signature"
									variant="outline"
									size="sm"
									class="gap-1.5"
									onclick={() => sendForSignatureMutation.mutate()}
									disabled={sendForSignatureMutation.isPending}
								>
									<PenLine class="h-4 w-4" />
									{sendForSignatureMutation.isPending
										? 'Sending…'
										: signature?.esignStatus === 'Sent'
											? 'Resend for signature'
											: 'Send for signature'}
								</Button>
							{:else}
								<Button
									data-testid="lease-send-for-signature-disabled"
									variant="outline"
									size="sm"
									class="gap-1.5"
									disabled
									title={signableStateMessage(visibleStatus)}
								>
									<PenLine class="h-4 w-4" />
									Send unavailable
								</Button>
							{/if}
							{#if signature?.hasSignedDocument}
								<Button
									data-testid="lease-download-signed-document"
									size="sm"
									class="gap-1.5"
									onclick={handleSignedDocumentDownload}
									disabled={downloadingSignedDocument}
								>
									<Download class="h-4 w-4" />
									{downloadingSignedDocument ? 'Preparing…' : 'Download signed lease'}
								</Button>
							{/if}
						</div>
					</div>

					{#if esignNotConfigured}
						<p class="mt-2 text-xs text-muted-foreground" data-testid="lease-esign-not-configured">
							E-signature isn’t set up yet (no e-sign provider configured).
						</p>
					{:else if signature?.esignStatus === 'Sent' && latestSignatureEmailStatus === 'DeliveryDisabled'}
						<p class="mt-2 text-xs text-warning" data-testid="lease-esign-delivery-disabled-note">
							Signing request created, but that email attempt was not delivered. Resend for signature to try the configured email provider again, or use the signing link from the queue below.
						</p>
					{:else if signature?.esignStatus === 'Sent' && latestSignatureEmailStatus === 'Failed'}
						<p class="mt-2 text-xs text-destructive" data-testid="lease-esign-delivery-failed-note">
							Signing request created, but the latest email delivery failed. Review the queue reason below, then resend for signature.
						</p>
					{:else if signature?.esignStatus === 'Sent'}
						<p class="mt-2 text-xs text-muted-foreground" data-testid="lease-esign-sent-note">
							Signing request is waiting for {lease?.tenantName ?? 'the tenant'} to sign. The email queue below shows delivery status.
						</p>
					{:else if signature?.esignStatus === 'Signed'}
						<p class="mt-2 text-xs text-success" data-testid="lease-esign-signed-note">
							{signature?.hasSignedDocument
								? 'Signed. You can download the signed lease above.'
								: 'Signed. The executed copy is being finalized — the download will appear here shortly.'}
						</p>
					{:else if signature?.esignStatus === 'Declined'}
						<p class="mt-2 text-xs text-muted-foreground" data-testid="lease-esign-declined-note">
							The signer declined. You can send it again when you’re ready.
						</p>
					{:else if !canSendForSignature}
						<p class="mt-2 text-xs text-muted-foreground" data-testid="lease-esign-not-signable-note">
							{signableStateMessage(visibleStatus)}
						</p>
					{:else}
						<p class="mt-2 text-xs text-muted-foreground">
							Send this lease to {lease?.tenantName ?? 'the tenant'} to sign electronically. We’ll generate the agreement if needed.
						</p>
					{/if}

					<div class="mt-4 rounded-md border border-border bg-muted/20 p-3" data-testid="lease-esign-queue">
						<div class="flex items-center justify-between gap-2">
							<div class="flex min-w-0 items-center gap-2">
								<Mail class="h-4 w-4 shrink-0 text-muted-foreground" />
								<p class="truncate text-sm font-medium">Email queue</p>
							</div>
							<Button
								variant="ghost"
								size="icon"
								class="h-8 w-8"
								aria-label="Refresh e-sign queue"
								data-testid="lease-esign-queue-refresh"
								onclick={invalidateSignatureQueue}
							>
								<RefreshCw class="h-4 w-4 {signatureQueueQuery.isFetching ? 'animate-spin' : ''}" />
							</Button>
						</div>

						{#if signatureQueueQuery.isLoading}
							<div class="mt-3 space-y-2" data-testid="lease-esign-queue-loading">
								<div class="h-10 animate-pulse rounded bg-muted/60"></div>
								<div class="h-10 animate-pulse rounded bg-muted/40"></div>
							</div>
						{:else if signatureQueueQuery.isError}
							<p class="mt-2 text-xs text-muted-foreground" data-testid="lease-esign-queue-error">
								Email queue is unavailable right now.
							</p>
						{:else if signatureQueueItems.length === 0}
							<p class="mt-2 text-xs text-muted-foreground" data-testid="lease-esign-queue-empty">
								No signing emails have been queued yet.
							</p>
						{:else}
							<div class="mt-3 divide-y divide-border/70" data-testid="lease-esign-queue-list">
								{#each signatureQueueItems as item (item.id)}
									<div class="py-2 first:pt-0 last:pb-0">
										<div class="flex items-start justify-between gap-3">
											<div class="min-w-0">
												{#if item.recipientEmail && item.tenantId}
													<a
														href="/tenants/{item.tenantId}"
														class="block truncate text-sm font-medium text-foreground underline-offset-4 hover:underline"
														data-testid="lease-esign-tenant-link-{item.id}"
													>
														{item.recipientEmail}
													</a>
												{:else}
													<p class="truncate text-sm font-medium">{item.recipientEmail || 'No recipient email'}</p>
												{/if}
												<p class="mt-0.5 truncate text-xs text-muted-foreground">{item.subject || 'Lease signing email'}</p>
											</div>
											<span class={queueStatusClass(item.status)}>{queueStatusLabel(item.status)}</span>
										</div>
										<p class="mt-1 text-xs text-muted-foreground">{queueWhenLabel(item)}</p>
										{#if item.error}
											<p class={queueErrorClass(item.status)}>{item.error}</p>
										{/if}
										{#if item.status === 'DeliveryDisabled' && item.signingUrl}
											<div class="mt-2 flex flex-wrap items-center gap-2">
												<Button
													variant="outline"
													size="sm"
													class="h-8 gap-1.5"
													href={item.signingUrl}
													target="_blank"
													rel="noreferrer"
													data-testid="lease-esign-open-signing-link-{item.id}"
												>
													<ExternalLink class="h-3.5 w-3.5" />
													Open signing link
												</Button>
												<Button
													variant="ghost"
													size="sm"
													class="h-8 gap-1.5"
													onclick={() => copySigningUrl(item)}
													data-testid="lease-esign-copy-signing-link-{item.id}"
												>
													<Copy class="h-3.5 w-3.5" />
													{copiedSigningUrlItemId === item.id ? 'Copied' : 'Copy link'}
												</Button>
											</div>
										{/if}
									</div>
								{/each}
							</div>
						{/if}
					</div>
				</div>
			</Card.Content>
		</Card.Root>
			</Tabs.Content>

			<!-- ───────────────────────── LEDGER ───────────────────────── -->
			<Tabs.Content value="ledger" class="space-y-6">
		<!-- Account history (plain-English ledger with a "why" per line) -->
		<Card.Root data-testid="lease-ledger-card">
			<Card.Header>
				<Card.Title class="text-base">Account History</Card.Title>
				<Card.Description>Every charge and payment on this lease, in plain English.</Card.Description>
			</Card.Header>
			<Card.Content>
				{#if ledgerQuery.isLoading}
					<div class="space-y-2">
						{#each [0, 1, 2] as _}
							<div class="h-12 w-full animate-pulse rounded border border-border bg-muted"></div>
						{/each}
					</div>
				{:else if ledgerQuery.isError || !ledger}
					<p class="text-sm text-muted-foreground">Account history is unavailable right now.</p>
				{:else}
					<div class="mb-4 grid grid-cols-3 gap-3">
						<div class="rounded-md border border-border bg-background p-3" data-testid="lease-ledger-charged">
							<p class="text-xs text-muted-foreground">Charged</p>
							<p class="mt-0.5 font-mono tabular-nums text-lg font-bold">{formatCurrency(ledger.totalCharged)}</p>
						</div>
						<div class="rounded-md border border-success/30 bg-success/5 p-3" data-testid="lease-ledger-paid">
							<p class="text-xs text-muted-foreground">Paid</p>
							<p class="mt-0.5 font-mono tabular-nums text-lg font-bold text-success">{formatCurrency(ledger.totalPaid)}</p>
						</div>
						<div class="rounded-md border border-border bg-background p-3" data-testid="lease-ledger-balance">
							<p class="text-xs text-muted-foreground">Balance</p>
							<p class="mt-0.5 font-mono tabular-nums text-lg font-bold {ledger.balance > 0.005 ? 'text-warning' : 'text-success'}">{formatCurrency(ledger.balance)}</p>
						</div>
					</div>
					<p class="mb-4 text-sm font-medium" data-testid="lease-ledger-balance-line">{balanceLine}</p>

					<!-- Opening balance control (carried-over balance from before Rental Command) -->
					<div class="mb-4 rounded-md border border-dashed border-border bg-muted/20 p-3" data-testid="lease-opening-balance">
						{#if openingBalanceQuery.isLoading}
							<div class="h-9 w-full animate-pulse rounded bg-muted"></div>
						{:else if openingBalance}
							<div class="flex flex-wrap items-center justify-between gap-3">
								<div class="min-w-0">
									<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Opening balance</p>
									<p class="mt-0.5 text-sm font-medium" data-testid="lease-opening-balance-value">
										{#if openingBalance.amount < -0.005}
											Tenant credit of <span class="font-mono tabular-nums">{formatCurrency(Math.abs(openingBalance.amount))}</span>
										{:else if openingBalance.amount > 0.005}
											Tenant owed <span class="font-mono tabular-nums">{formatCurrency(openingBalance.amount)}</span>
										{:else}
											<span class="font-mono tabular-nums">{formatCurrency(0)}</span>
										{/if}
										<span class="text-muted-foreground"> · as of {formatDate(openingBalance.asOfDate)}</span>
									</p>
									{#if openingBalance.note}
										<p class="mt-0.5 text-xs text-muted-foreground">{openingBalance.note}</p>
									{/if}
								</div>
								<div class="flex items-center gap-2">
									<Button variant="outline" size="sm" class="gap-1.5" onclick={openOpeningDialog} data-testid="lease-opening-balance-edit">
										<Pencil class="h-4 w-4" />
										Edit
									</Button>
									<Button variant="outline" size="sm" class="gap-1.5 hover:text-destructive" onclick={() => (showOpeningDeleteConfirm = true)} data-testid="lease-opening-balance-remove">
										<Trash2 class="h-4 w-4" />
										Remove
									</Button>
								</div>
							</div>
						{:else}
							<div class="flex flex-wrap items-center justify-between gap-3">
								<p class="min-w-0 text-xs text-muted-foreground">
									Carrying a balance from before Rental Command? Set the tenant's starting balance so the ledger is accurate.
								</p>
								<Button variant="outline" size="sm" onclick={openOpeningDialog} data-testid="lease-opening-balance-set">
									Set opening balance
								</Button>
							</div>
						{/if}
					</div>

					{#if ledger.entries.length === 0}
						<p class="text-sm text-muted-foreground">No charges or payments recorded yet.</p>
					{:else}
						<Tooltip.Provider delayDuration={150}>
							<ul class="space-y-2" data-testid="lease-ledger-entries">
								{#each ledger.entries as entry}
									<li class="rounded-md border border-border bg-background p-3" data-testid="lease-ledger-entry">
										<div class="flex items-start justify-between gap-3">
											<div class="min-w-0">
												<div class="flex items-center gap-1.5">
													<p class="truncate text-sm font-medium">{entry.description}</p>
													{#if entry.explanation}
														<HelpTooltip text={entry.explanation} label="Why this is here" />
													{/if}
												</div>
												<p class="mt-0.5 text-xs text-muted-foreground">{formatDate(entry.date)} · {entry.type}{entry.status ? ` · ${entry.status}` : ''}</p>
												{#if entry.explanation}
													<p class="mt-1 text-xs leading-snug text-muted-foreground" data-testid="lease-ledger-entry-explanation">{entry.explanation}</p>
												{/if}
											</div>
											<p class="shrink-0 font-mono tabular-nums text-sm font-semibold {entry.type === 'Payment' ? 'text-success' : ''}">{formatCurrency(entry.amount)}</p>
										</div>
									</li>
								{/each}
							</ul>
						</Tooltip.Provider>
					{/if}
				{/if}
			</Card.Content>
		</Card.Root>

		<!-- Payments section -->
		<div>
			<div class="mb-2 flex items-center justify-between">
				<h2 class="text-lg font-semibold">Payments</h2>
				<a href="/deposits" class="text-xs text-muted-foreground underline-offset-4 hover:underline">View deposits</a>
			</div>
			<DataGrid
				data={paymentsQuery.data ?? []}
				columns={paymentColumns}
				loading={paymentsQuery.isLoading}
				emptyMessage="No payments recorded for this lease."
				getRowKey={(p) => p.id}
				onRowClick={(p) => goto(recordHref('payment', p))}
				data-testid="lease-payments-grid"
			/>
		</div>

		<!-- Documents section -->
		<div data-testid="lease-detail-documents">
			<DocumentsPanel entityType="Lease" entityId={leaseId} />
		</div>
			</Tabs.Content>

			<Tabs.Content value="history" class="space-y-6">
				<Card.Root data-testid="lease-history-card">
					<Card.Header>
						<Card.Title class="text-base">History</Card.Title>
						<Card.Description>Every recorded change to this lease — who, what, and when.</Card.Description>
					</Card.Header>
					<Card.Content>
						<RecordHistory entityType="Lease" entityId={leaseId} />
					</Card.Content>
				</Card.Root>
			</Tabs.Content>
		</Tabs.Root>
	{/if}
</div>

<ConfirmDialog
	open={showDeleteConfirm}
	title={leaseDeleteState.title}
	message={leaseDeleteState.message}
	confirmLabel={leaseDeleteState.confirmLabel}
	busy={deleteMutation.isPending}
	testid="lease-delete-confirm"
	onconfirm={() => deleteMutation.mutate()}
	oncancel={() => (showDeleteConfirm = false)}
/>

<!-- Set Active — lifecycle confirm (F11). No longer a one-click write. -->
<ConfirmDialog
	open={showSetActiveConfirm}
	title="Set lease active?"
	message={lease ? `Mark lease ${lease.leaseNumber} as Active?` : ''}
	confirmLabel="Set active"
	busy={statusMutation.isPending}
	testid="lease-set-active-confirm"
	onconfirm={confirmSetActive}
	oncancel={() => (showSetActiveConfirm = false)}
/>

<ConfirmDialog
	open={pendingStatusAction !== null}
	title={pendingStatusActionTitle}
	message={pendingStatusActionMessage}
	confirmLabel={pendingStatusAction?.confirmLabel ?? 'Update status'}
	busy={statusMutation.isPending}
	testid="lease-status-action-confirm"
	onconfirm={confirmPendingStatusAction}
	oncancel={() => (pendingStatusAction = null)}
/>

{#if lease}
	<TenantNoticeDialog
		bind:open={showNoticeDialog}
		tenantId={lease.tenantId}
		tenantName={lease.tenantName}
		activeLeaseCount={visibleStatus === 'Active' ? 1 : 0}
	/>
{/if}

<!-- Opening balance dialog (set / edit) -->
<Dialog.Root open={showOpeningDialog} onOpenChange={(v) => { if (!v) closeOpeningDialog(); }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>{openingBalance ? 'Edit opening balance' : 'Set opening balance'}</Dialog.Title>
			<Dialog.Description>
				Did this tenant already owe you (or have a credit) before you started using Rental Command?
				Enter the starting balance so the ledger is accurate.
			</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-3" data-testid="lease-opening-balance-form">
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Direction</span>
				<Select.Root type="single" bind:value={openingDirection}>
					<Select.Trigger class="w-full" data-testid="lease-opening-balance-direction">
						{openingDirectionLabel}
					</Select.Trigger>
					<Select.Content>
						{#each directionOptions as option}
							<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				<p class="mt-1 text-xs text-muted-foreground">
					"Tenant owed" means they were behind; "Tenant credit" means they were paid ahead.
				</p>
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Amount</span>
				<Input
					data-testid="lease-opening-balance-amount"
					bind:value={openingAmount}
					type="text"
					inputmode="decimal"
					mask="currency"
					placeholder="0.00"
				/>
				{#if openingErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="lease-opening-balance-amount-error">{openingErrors.amount}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">As of date</span>
				<DatePicker
					testid="lease-opening-balance-date"
					bind:value={openingAsOfDate}
					placeholder="As of date"
				/>
				{#if openingErrors.asOfDate}<p class="mt-1 text-xs text-destructive" data-testid="lease-opening-balance-date-error">{openingErrors.asOfDate}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Note (optional)</span>
				<textarea
					data-testid="lease-opening-balance-note"
					bind:value={openingNote}
					rows="2"
					class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
				></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={closeOpeningDialog} data-testid="lease-opening-balance-cancel">Cancel</Button>
			<Button onclick={submitOpeningBalance} disabled={saveOpeningMutation.isPending} data-testid="lease-opening-balance-save">
				{saveOpeningMutation.isPending ? 'Saving…' : openingBalance ? 'Save changes' : 'Save opening balance'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={showOpeningDeleteConfirm}
	title="Remove opening balance"
	message="Remove the opening balance for this lease? The ledger will no longer show the carried-over starting balance."
	confirmLabel="Remove"
	busy={deleteOpeningMutation.isPending}
	testid="lease-opening-balance-delete-confirm"
	onconfirm={() => { if (openingBalance) deleteOpeningMutation.mutate(openingBalance.id); }}
	oncancel={() => (showOpeningDeleteConfirm = false)}
/>
