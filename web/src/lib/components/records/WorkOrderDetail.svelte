<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { technician } from '$lib/api/endpoints/technician';
	import { properties } from '$lib/api/endpoints/properties';
	import { vendors } from '$lib/api/endpoints/vendors';
	import type { Vendor } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { workOrderDetailSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatDateOnly, localInputToOffsetIso } from '$lib/utils/date';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import DateTimePicker from '$lib/components/shared/DateTimePicker.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import StarRating from '$lib/components/shared/StarRating.svelte';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { History, Pencil, Save, Trash2, X, MessageSquare, Star, Check, Wrench, Coins, Phone, Mail } from '@lucide/svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';
	import WorkOrderTimeline from '$lib/components/shared/WorkOrderTimeline.svelte';
	import {
		canDispatchToVendor,
		dispatchVendorBlockReason,
		formatStatusTransitionCopy,
		shouldShowActiveDispatchHint,
	} from '$lib/maintenance/work-order-dispatch';
	import { isMismatchedUnitSelection } from '$lib/unit/unit-membership-guard';
	import { hasCapability } from '$lib/stores/auth.svelte';

	let {
		workOrderId,
		onDeleted,
		expectedUnitId,
		onUnitMismatch,
	}: {
		workOrderId: number;
		onDeleted: () => void;
		expectedUnitId?: number;
		onUnitMismatch?: () => void;
	} = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const WO_PRIORITIES = ['Low', 'Normal', 'High', 'Emergency'];

	const workOrderQuery = createQuery(() => ({
		queryKey: ['work-order', workOrderId],
		queryFn: () => workOrders.get(workOrderId),
		enabled: !isNaN(workOrderId) && workOrderId > 0,
	}));

	async function loadPropertyOptions(params: { search?: string; skip: number; take: number }) {
		const result = await properties.listPage(portfolioId, { ...params, sort: 'name' });
		return {
			...result,
			items: result.items.map((property) => ({
				id: property.id,
				label: property.name,
				description: `${property.addressLine1}, ${property.city}, ${property.state}`
			}))
		};
	}

	const wo = $derived(workOrderQuery.data);
	const caps = $derived(workOrderCapabilities(wo));

	const responsibilitiesQuery = createQuery(() => ({
		queryKey: ['work-order-responsibilities', workOrderId],
		queryFn: () => workOrders.responsibilities(workOrderId),
		enabled: workOrderId > 0,
	}));
	const candidatesQuery = createQuery(() => ({
		queryKey: ['work-order-responsibility-candidates', workOrderId],
		queryFn: () => workOrders.responsibilityCandidates(workOrderId),
		enabled: workOrderId > 0 && caps.canAssign,
	}));
	const currentPrimary = $derived(
		(responsibilitiesQuery.data ?? []).find((item) => item.kind === 'Primary' && !item.effectiveToUtc) ?? null
	);
	let selectedCandidateId = $state('');
	let assignmentReason = $state('Assigned by property manager');

	function responsibilityExpectations(candidateIds: number[]) {
		return [...new Set(candidateIds)].map((accessContextId) => {
			const candidate = (candidatesQuery.data ?? []).find((item) => item.accessContextId === accessContextId);
			if (!candidate) throw new Error('Refresh the technician list before changing responsibility.');
			return { accessContextId, expectedRevision: candidate.accessRevision };
		});
	}

	const assignResponsibilityMutation = createMutation(() => ({
		mutationFn: () => {
			const candidate = (candidatesQuery.data ?? []).find(
				(item) => item.membershipRoleAssignmentId === Number(selectedCandidateId)
			);
			if (!candidate) throw new Error('Choose a technician.');
			const contexts = [candidate.accessContextId];
			if (currentPrimary?.accessContextId) contexts.push(currentPrimary.accessContextId);
			return workOrders.assignResponsibility(workOrderId, {
				workspaceMembershipId: candidate.workspaceMembershipId,
				membershipRoleAssignmentId: candidate.membershipRoleAssignmentId,
				kind: 'Primary',
				expectedCurrentPrimaryResponsibilityId: currentPrimary?.id ?? null,
				accessRevisionExpectations: responsibilityExpectations(contexts),
				reason: assignmentReason.trim()
			});
		},
		onSuccess: () => { showSuccess('Technician responsibility updated.'); invalidateResponsibility(); },
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const closeResponsibilityMutation = createMutation(() => ({
		mutationFn: () => {
			if (!currentPrimary?.accessContextId) throw new Error('No current technician assignment.');
			return workOrders.closeResponsibility(workOrderId, currentPrimary.id, {
				accessRevisionExpectations: responsibilityExpectations([currentPrimary.accessContextId]),
				reason: 'Unassigned by property manager'
			});
		},
		onSuccess: () => { showSuccess('Technician unassigned.'); invalidateResponsibility(); },
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function invalidateResponsibility() {
		queryClient.invalidateQueries({ queryKey: ['work-order-responsibilities', workOrderId] });
		queryClient.invalidateQueries({ queryKey: ['work-order-responsibility-candidates', workOrderId] });
		invalidate();
	}

	$effect(() => {
		if (isMismatchedUnitSelection(wo, expectedUnitId)) onUnitMismatch?.();
	});

	const priorityOptions = $derived(WO_PRIORITIES.map((value) => ({ value, label: value })));

	// --- Inline edit ---
	let editing = $state(false);
	let form = $state({
		propertyId: '',
		title: '',
		description: '',
		technicianAccessInstructions: '',
		priority: 'Normal',
		category: 'General',
		// Costs & timing (editable directly, including on Completed orders — no reopen workflow).
		// Requested/completed are date-only; schedule fields are datetime-local wall-clock strings.
		requestedAt: '',
		scheduledFor: '',
		scheduledWindowEnd: '',
		completedAt: '',
		estimatedCost: '',
		actualCost: '',
	});
	let formErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);
	let selectedPropertyLabel = $state<string | null>(null);
	let commentBody = $state('');
	let commentPrivate = $state(false);

	// Snapshot of timing values exactly as seeded when editing began, so save() only re-sends values
	// the user actually changed. PATCH null/missing means "leave unchanged" for schedule fields.
	let seededDates = $state({ requestedAt: '', scheduledFor: '', scheduledWindowEnd: '', completedAt: '' });

	function startEditing() {
		if (!wo) return;
		form = {
			propertyId: String(wo.propertyId),
			title: wo.title,
			description: wo.description,
			technicianAccessInstructions: wo.technicianAccessInstructions ?? '',
			priority: wo.priority,
			category: wo.category,
			// Requested/completed remain date-only; schedule fields preserve the local wall-clock time.
			requestedAt: wo.requestedAt?.slice(0, 10) ?? '',
			scheduledFor: utcIsoToLocalInput(wo.scheduledFor),
			scheduledWindowEnd: utcIsoToLocalInput(wo.scheduledWindowEnd),
			completedAt: wo.completedAt?.slice(0, 10) ?? '',
			estimatedCost: wo.estimatedCost != null ? String(wo.estimatedCost) : '',
			actualCost: wo.actualCost != null ? String(wo.actualCost) : '',
		};
		// Remember the seeded timing values to dirty-check against on save (see seededDates).
		seededDates = {
			requestedAt: form.requestedAt,
			scheduledFor: form.scheduledFor,
			scheduledWindowEnd: form.scheduledWindowEnd,
			completedAt: form.completedAt,
		};
		selectedPropertyLabel = wo.propertyName ?? null;
		formErrors = {};
		editing = true;
	}
	function cancelEditing() {
		editing = false;
		selectedPropertyLabel = null;
		formErrors = {};
	}
	function save() {
		const result = parseForm(workOrderDetailSchema, form);
		const errors = { ...(result.errors ?? {}), ...workOrderWindowErrors() };
		if (Object.keys(errors).length > 0) {
			formErrors = errors;
			return;
		}
		if (!result.data) return;
		formErrors = {};
		// Drop unchanged timing values. Schedule fields that changed are sent with the browser's local
		// offset so the server stores the true instant and WorkOrderAppointmentSync keeps the same window.
		const data: Record<string, unknown> = { ...result.data };
		for (const field of ['requestedAt', 'completedAt'] as const) {
			if (form[field] === seededDates[field]) delete data[field];
		}
		for (const field of ['scheduledFor', 'scheduledWindowEnd'] as const) {
			if (form[field] === seededDates[field]) {
				delete data[field];
			} else {
				data[field] = localInputToOffsetIso(form[field]);
			}
		}
		saveMutation.mutate({ portfolioId, ...data });
	}

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['work-order', workOrderId] });
		queryClient.invalidateQueries({ queryKey: ['work-orders', portfolioId] });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => workOrders.update(workOrderId, data),
		onSuccess: () => {
			showSuccess('Work order updated.');
			editing = false;
			selectedPropertyLabel = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// Status change with an optional short note recorded on the timeline.
	let pendingStatus = $state<string | null>(null);
	let statusNote = $state('');

	function openStatusChange(next: string) {
		pendingStatus = next;
		statusNote = '';
	}
	function cancelStatusChange() {
		pendingStatus = null;
		statusNote = '';
	}
	function confirmStatusChange() {
		if (!wo || !pendingStatus) return;
		statusMutation.mutate({ id: wo.id, status: pendingStatus, note: statusNote.trim() || undefined });
	}

	const statusMutation = createMutation(() => ({
		mutationFn: ({ id: woId, status, note }: { id: number; status: string; note?: string }) =>
			caps.canEdit
				? workOrders.updateStatus(woId, status, note)
				: technician.update(woId, {
						status,
						technicianNote: note,
						expectedUpdatedAtUtc: wo!.updatedAt
					}),
		onSuccess: () => {
			showSuccess('Status updated.');
			pendingStatus = null;
			statusNote = '';
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: (woId: number) => workOrders.delete(woId),
		onSuccess: () => {
			showSuccess('Work order deleted.');
			onDeleted();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const commentMutation = createMutation(() => ({
		mutationFn: () =>
			workOrders.comment(workOrderId, {
				body: commentBody.trim(),
				isPrivate: caps.canCommentPrivately && commentPrivate
			}),
		onSuccess: () => {
			showSuccess('Comment added.');
			commentBody = '';
			commentPrivate = false;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function submitComment() {
		if (!commentBody.trim()) return;
		commentMutation.mutate();
	}

	// --- Dispatch to a vendor (text them the job) ---
	// Vendors are only loaded once the dispatch dialog is opened, to keep the page light.
	let showDispatch = $state(false);
	let selectedVendorId = $state<number | null>(null);
	let selectedVendorLabel = $state<string | null>(null);
	let selectedDispatchVendor = $state<Vendor | null>(null);
	let vendorOptionCache = $state<Record<string, Vendor>>({});
	let dispatchNote = $state('');
	let dispatched = $state(false);

	async function loadVendorOptions(params: { search?: string; skip: number; take: number }) {
		const result = await vendors.listPage(portfolioId, { ...params, sort: 'name' });
		vendorOptionCache = {
			...vendorOptionCache,
			...Object.fromEntries(result.items.map((vendor) => [String(vendor.id), vendor]))
		};
		return {
			...result,
			items: result.items.map((vendor) => ({
				id: vendor.id,
				label: vendor.name,
				description: `${vendor.serviceType}${vendor.phone ? ` · ${vendor.phone}` : ' · no phone on file'}`
			}))
		};
	}

	const assignedVendorQuery = createQuery(() => ({
		queryKey: ['vendor', wo?.vendorId],
		queryFn: () => vendors.get(wo!.vendorId!),
		enabled: caps.canContactVendor && (wo?.vendorId ?? 0) > 0
	}));
	const canConfirmDispatch = $derived(canDispatchToVendor(selectedDispatchVendor));
	const dispatchBlockReason = $derived(dispatchVendorBlockReason(selectedDispatchVendor));

	// The vendor assigned to this work order (when any), resolved by its exact id so the page does not
	// preload a capped vendor list. tel:/sms: hrefs strip everything but digits and a leading +, and the
	// whole value is URL-encoded.
	const assignedVendor = $derived(caps.canContactVendor ? assignedVendorQuery.data ?? null : null);
	function telHref(scheme: 'tel' | 'sms', phone: string | null | undefined): string | null {
		if (!phone) return null;
		const cleaned = phone.replace(/[^\d+]/g, '');
		return cleaned ? `${scheme}:${encodeURIComponent(cleaned)}` : null;
	}

	function openDispatch() {
		selectedVendorId = null;
		selectedVendorLabel = null;
		selectedDispatchVendor = null;
		dispatchNote = '';
		showDispatch = true;
	}
	function closeDispatch() {
		showDispatch = false;
		selectedVendorId = null;
		selectedVendorLabel = null;
		selectedDispatchVendor = null;
	}
	function ratingLabel(v: Vendor): string {
		if (v.averageRating == null || v.ratingCount === 0) return 'No ratings yet';
		const jobs = v.jobsCompleted === 1 ? '1 job' : `${v.jobsCompleted} jobs`;
		return `${v.averageRating.toFixed(1)} ★ · ${jobs} done`;
	}

	const dispatchMutation = createMutation(() => ({
		mutationFn: ({ vendorId, note }: { vendorId: number; note?: string }) =>
			workOrders.dispatch(workOrderId, { vendorId, note }),
		onSuccess: () => {
			showSuccess('Job texted to the vendor.');
			dispatched = true;
			showDispatch = false;
			invalidate();
		},
		// The API returns 400 with a plain "no phone" message — surface it as-is.
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function confirmDispatch() {
		if (selectedVendorId == null || !canConfirmDispatch) return;
		dispatchMutation.mutate({ vendorId: selectedVendorId, note: dispatchNote.trim() || undefined });
	}

	// --- Rate the vendor on a completed work order ---
	let showRate = $state(false);
	let rateStars = $state(0);
	let rateComment = $state('');

	function openRate() {
		rateStars = 0;
		rateComment = '';
		showRate = true;
	}
	function closeRate() {
		showRate = false;
	}

	const rateMutation = createMutation(() => ({
		mutationFn: ({ vendorId, stars, comment }: { vendorId: number; stars: number; comment?: string }) =>
			vendors.rate(vendorId, { stars, comment, workOrderId }),
		onSuccess: () => {
			showSuccess('Thanks — rating saved.');
			showRate = false;
			queryClient.invalidateQueries({ queryKey: ['vendors', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function confirmRate() {
		if (!wo?.vendorId || rateStars < 1) return;
		rateMutation.mutate({ vendorId: wo.vendorId, stars: rateStars, comment: rateComment.trim() || undefined });
	}

	const availableTransitions = $derived(
		wo ? caps.allowedStatusTransitions : []
	);

	function formatCurrency(val: number | undefined | null): string {
		if (val == null) return '—';
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(val);
	}

	function utcIsoToLocalInput(value: string | null | undefined): string {
		if (!value) return '';
		const date = new Date(value);
		if (Number.isNaN(date.getTime())) return '';
		const pad = (n: number) => String(n).padStart(2, '0');
		return (
			`${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` +
			`T${pad(date.getHours())}:${pad(date.getMinutes())}:00`
		);
	}

	function formatDateTime(value: string | null | undefined): string {
		if (!value) return '';
		const date = new Date(value);
		if (Number.isNaN(date.getTime())) return '';
		return date.toLocaleString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric',
			hour: 'numeric',
			minute: '2-digit'
		});
	}

	function workOrderWindowErrors(): Record<string, string> {
		if (form.scheduledFor && form.scheduledWindowEnd) {
			const start = new Date(form.scheduledFor).getTime();
			const end = new Date(form.scheduledWindowEnd).getTime();
			if (!Number.isNaN(start) && !Number.isNaN(end) && end <= start) {
				return { scheduledWindowEnd: 'Window end must be after the start time.' };
			}
		}
		return {};
	}

	function workOrderCapabilities(workOrder: typeof wo) {
		const source = (workOrder?.capabilities ?? {}) as Record<string, unknown>;
		const bool = (key: string) => typeof source[key] === 'boolean' ? Boolean(source[key]) : false;
		const transitions = source.allowedStatusTransitions;
		return {
			canEdit: bool('canEditRequestFields') || bool('canEditManagementFields'),
			canDelete: bool('canEditManagementFields'),
			canAssign: bool('canAssignTechnician'),
			canUpdateStatus: Array.isArray(transitions) && transitions.length > 0,
			canCommentPublicly: bool('canCommentPublicly'),
			canCommentPrivately: bool('canCommentPrivately'),
			canUploadPhoto: bool('canUploadPhoto'),
			canDeletePhoto: bool('canDeletePhoto'),
			canCancel: bool('canCancel'),
			canEditRequestFields: bool('canEditRequestFields'),
			canEditManagementFields: bool('canEditManagementFields'),
			canDispatchVendor: bool('canDispatchVendor'),
			canContactVendor: bool('canDispatchVendor'),
			canRateVendor: bool('canEditManagementFields'),
			canViewTenantContact: bool('canViewTenantContact'),
			canViewResidents: bool('canViewResidents'),
			canViewAccessInstructions: bool('canViewAccessInstructions'),
			canViewCosts: bool('canViewCosts'),
			canViewPrivateNotes: bool('canViewPrivateManagementNotes'),
			allowedStatusTransitions: Array.isArray(transitions) ? transitions.map(String) : []
		};
	}

	function accessRows(workOrder: typeof wo): Array<[string, string]> {
		if (!workOrder) return [];
		const rows: Array<[string, string]> = [];
		if (workOrder.residentMustBePresent != null) rows.push(['Presence', workOrder.residentMustBePresent ? 'Resident must be present' : 'Resident does not need to be present']);
		if (workOrder.callBeforeEntry) rows.push(['Call before entry', 'Yes']);
		if (workOrder.callIfNotHome) rows.push(['Call if not home', 'Yes']);
		if (workOrder.permissionToEnter != null) rows.push(['Permission to enter', workOrder.permissionToEnter ? 'Yes' : 'No']);
		if (workOrder.entryNotes) rows.push(['Entry notes', workOrder.entryNotes]);
		if (workOrder.petWarnings) rows.push(['Pets', workOrder.petWarnings]);
		if (workOrder.accessWarnings) rows.push(['Access warnings', workOrder.accessWarnings]);
		if (workOrder.technicianAccessInstructions) rows.push(['Technician access', workOrder.technicianAccessInstructions]);
		return rows;
	}
</script>

<!--
  Editable date row: DatePicker in edit mode, UTC-pinned date display otherwise. Mirrors the
  payment detail page's dateField so timing edits feel identical across the app.
-->
{#snippet dateField(opts: {
	label: string;
	value: string;
	setValue: (v: string) => void;
	display: string;
	testid: string;
	error?: string;
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
			/>
			{#if opts.error}<p class="mt-1 text-xs text-destructive" data-testid={`${opts.testid}-error`}>{opts.error}</p>{/if}
		{:else}
			<div class="m3-readonly-field flex flex-col justify-center" data-testid={`${opts.testid}-value`}>
				<span class="m3-readonly-field__label">{opts.label}</span>
				<span class="m3-readonly-field__value mt-1 font-mono tabular-nums">{opts.display === '' ? '-' : opts.display}</span>
			</div>
		{/if}
	</div>
{/snippet}

{#snippet dateTimeField(opts: {
	label: string;
	value: string;
	setValue: (value: string) => void;
	display: string;
	testid: string;
	error?: string;
})}
	<div data-testid={`${opts.testid}-field`}>
		{#if editing}
			<label class="mb-1 block text-xs font-medium text-muted-foreground" for={`${opts.testid}-input`}>{opts.label}</label>
			<DateTimePicker
				id={`${opts.testid}-input`}
				testid={`${opts.testid}-input`}
				value={opts.value}
				onchange={opts.setValue}
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

<svelte:head>
	<title>{wo?.title ?? 'Work Order'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="work-order-detail-page">
	{#if workOrderQuery.isLoading}
		<LoadingState label="Loading work order details" variant="page" testid="work-order-detail-loading" />
	{:else if workOrderQuery.isError}
		<div class="rounded-lg border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="work-order-detail-error">
			<p class="font-medium text-destructive">Could not load this work order.</p>
			<p class="mt-1 text-sm text-muted-foreground">Try again. No work-order changes have been made.</p>
			<Button class="mt-4" variant="outline" onclick={() => workOrderQuery.refetch()}>Try again</Button>
		</div>
	{:else if !wo}
		<p class="py-8 text-center text-sm text-muted-foreground" data-testid="work-order-detail-not-found">Work order not found.</p>
	{:else}
		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start justify-between gap-4">
			<div class="space-y-2">
				<div class="flex flex-wrap items-center gap-2">
					<h1 class="text-2xl font-bold" data-testid="work-order-detail-title">{wo.title}</h1>
					<StatusBadge status={wo.status} />
					<StatusBadge status={wo.priority} />
				</div>
				<div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground">
					{#if wo.propertyName}
						<span data-testid="work-order-detail-property">{wo.propertyName}</span>
					{/if}
					{#if wo.unitNumber}
						<a href="/units/{wo.unitId}?tab=maintenance" class="underline-offset-4 hover:underline" data-testid="work-order-detail-unit">Unit {wo.unitNumber}</a>
					{/if}
					{#if wo.vendorName}
						<span data-testid="work-order-detail-vendor">{wo.vendorName}</span>
					{/if}
				</div>
			</div>
			<div class="flex flex-wrap items-center gap-2">
				{#if editing}
					<Button
						variant="outline"
						size="sm"
						data-testid="work-order-edit-cancel"
						onclick={cancelEditing}
						disabled={saveMutation.isPending}
					>
						<X class="h-4 w-4" />
						Cancel
					</Button>
					<Button
						size="sm"
						data-testid="work-order-edit-save"
						onclick={save}
						disabled={saveMutation.isPending}
					>
						<Save class="h-4 w-4" />
						{saveMutation.isPending ? 'Saving…' : 'Save'}
					</Button>
				{:else}
					<!-- Status transition buttons -->
					{#each availableTransitions as nextStatus}
						<Button
							variant="outline"
							size="sm"
							disabled={statusMutation.isPending}
							data-testid="work-order-set-{nextStatus.toLowerCase()}"
							onclick={() => openStatusChange(nextStatus)}
						>
							{formatStatusLabel(nextStatus)}
						</Button>
					{/each}
					<!-- Text a vendor the job (they reply DONE to close it) -->
					{#if caps.canDispatchVendor && wo.status !== 'Completed' && wo.status !== 'Cancelled'}
						<Button
							variant="outline"
							size="sm"
							data-testid="work-order-dispatch"
							onclick={openDispatch}
						>
							<MessageSquare class="h-4 w-4" />
							Text a vendor
						</Button>
					{/if}
					<!-- Rate the vendor once the job is done -->
					{#if caps.canRateVendor && wo.status === 'Completed' && wo.vendorId}
						<Button
							variant="outline"
							size="sm"
							data-testid="work-order-rate-vendor"
							onclick={openRate}
						>
							<Star class="h-4 w-4" />
							Rate this vendor
						</Button>
					{/if}
					{#if caps.canEdit}<Button
						variant="outline"
						size="sm"
						data-testid="work-order-edit"
						onclick={startEditing}
					>
						<Pencil class="h-4 w-4" />
						Edit
					</Button>{/if}
					{#if caps.canDelete}<Button
						variant="outline"
						size="sm"
						class="hover:text-destructive"
						data-testid="work-order-delete"
						onclick={() => (showDeleteConfirm = true)}
					>
						<Trash2 class="h-4 w-4" />
						Delete
					</Button>{/if}
				{/if}
			</div>
		</div>

		<Card.Root class="mb-6" data-testid="work-order-responsibility-card">
			<Card.Header>
				<Card.Title>Who is handling this?</Card.Title>
				<Card.Description>
					{currentPrimary ? `${currentPrimary.memberDisplayName} is assigned to this work order.` : 'No technician is assigned yet.'}
				</Card.Description>
			</Card.Header>
			{#if caps.canAssign}
				<Card.Content class="flex flex-wrap items-end gap-3">
					<label class="grid min-w-64 gap-1 text-sm">
						<span>Technician</span>
						<Select.Root type="single" bind:value={selectedCandidateId}>
							<Select.Trigger class="w-full" data-testid="work-order-technician-select">
								{(candidatesQuery.data ?? []).find(
									(candidate) => String(candidate.membershipRoleAssignmentId) === selectedCandidateId
								)?.memberDisplayName ?? 'Choose a technician'}
							</Select.Trigger>
							<Select.Content>
								{#each candidatesQuery.data ?? [] as candidate}
									<Select.Item
										value={String(candidate.membershipRoleAssignmentId)}
										label={candidate.memberDisplayName}
									>
										{candidate.memberDisplayName}
									</Select.Item>
								{/each}
							</Select.Content>
						</Select.Root>
					</label>
					<label class="grid min-w-64 flex-1 gap-1 text-sm">
						<span>Assignment note</span>
						<input
							class="h-10 rounded-md border bg-background px-3"
							bind:value={assignmentReason}
							maxlength="1000"
							placeholder="Why this person is being assigned"
						/>
					</label>
					<Button onclick={() => assignResponsibilityMutation.mutate()} disabled={!selectedCandidateId || !assignmentReason.trim() || assignResponsibilityMutation.isPending}>
						{currentPrimary ? 'Reassign' : 'Assign'}
					</Button>
					{#if currentPrimary}
						<Button variant="outline" onclick={() => closeResponsibilityMutation.mutate()} disabled={closeResponsibilityMutation.isPending}>Unassign</Button>
					{/if}
				</Card.Content>
			{/if}
		</Card.Root>

		<!-- Dispatched hint: the vendor was texted and will reply DONE to close it. Gated on a REAL open
		     dispatch (server-computed hasActiveDispatch) or one just sent this session — never on a mere
		     vendor assignment, which would falsely claim the job was sent. -->
		{#if shouldShowActiveDispatchHint(wo.status, Boolean(wo.hasActiveDispatch), dispatched)}
			<div
				class="mb-6 flex items-start gap-3 rounded-md border bg-[var(--m3c-info-container)] text-[var(--m3c-on-info-container)] border-[color-mix(in_srgb,var(--info)_45%,transparent)] p-4 text-sm"
				data-testid="work-order-dispatched-hint"
			>
				<Check class="mt-0.5 h-4 w-4 shrink-0 text-[var(--m3c-on-info-container)]" />
				<p class="text-[var(--m3c-on-info-container)]">
					{#if wo.vendorName}
						<span class="font-medium">{wo.vendorName}</span> has the job.
					{:else}
						The vendor has the job.
					{/if}
					When they text back <span class="font-medium">DONE</span>, this work order closes automatically.
				</p>
			</div>
		{/if}

		<!-- Manager-only vendor contact actions. Technicians can update their assigned job but do not
		     receive vendor-dispatch authority or direct vendor contact details. -->
		{#if assignedVendor}
			<div class="mb-6 flex flex-wrap items-center gap-2 rounded-md border border-border bg-card p-3" data-testid="work-order-vendor-contact">
				<span class="mr-1 text-sm">
					<span class="text-muted-foreground">Vendor:</span>
					<span class="font-medium text-foreground">{assignedVendor.name}</span>
				</span>
				{#if telHref('tel', assignedVendor.phone)}
					<Button variant="outline" size="sm" href={telHref('tel', assignedVendor.phone)} data-testid="work-order-vendor-call">
						<Phone class="h-4 w-4" />
						Call
					</Button>
					<Button variant="outline" size="sm" href={telHref('sms', assignedVendor.phone)} data-testid="work-order-vendor-text">
						<MessageSquare class="h-4 w-4" />
						Text
					</Button>
				{/if}
				{#if assignedVendor.email}
					<Button variant="outline" size="sm" href={`mailto:${encodeURIComponent(assignedVendor.email)}`} data-testid="work-order-vendor-email">
						<Mail class="h-4 w-4" />
						Email
					</Button>
				{/if}
				{#if !assignedVendor.phone && !assignedVendor.email}
					<span class="text-xs text-muted-foreground" data-testid="work-order-vendor-no-contact">No phone or email on file for this vendor.</span>
				{/if}
			</div>
		{/if}

		<!-- Grouped detail cards -->
		<DetailCard title="Request" icon={Wrench} accent="primary" testid="work-order-detail-card" contentClass="grid gap-x-8 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
			<InlineField label="Title" bind:value={form.title} display={wo.title} {editing} error={formErrors.title} testid="work-order-detail-title-field" class="sm:col-span-2 lg:col-span-3" />
			<InlineField label="Description" bind:value={form.description} display={wo.description} {editing} type="textarea" error={formErrors.description} testid="work-order-detail-description" class="sm:col-span-2 lg:col-span-3" />
			<InlineField label="Safe technician access" bind:value={form.technicianAccessInstructions} display={wo.technicianAccessInstructions} {editing} type="textarea" testid="work-order-detail-technician-access" class="sm:col-span-2 lg:col-span-3" />
			{#if editing}
				<RemoteRecordSelect
					queryKey={['work-order-detail-property', portfolioId]}
					label="Property"
					bind:value={form.propertyId}
					selectedLabel={selectedPropertyLabel}
					placeholder="Select property"
					searchPlaceholder="Search properties…"
					emptyLabel="No matching properties"
					required
					loadPage={loadPropertyOptions}
					onValueChange={(_value, option) => (selectedPropertyLabel = option?.label ?? null)}
					testid="work-order-detail-property-field"
				/>
			{:else}
				<InlineField label="Property" bind:value={form.propertyId} display={wo.propertyName} editing={false} error={formErrors.propertyId} testid="work-order-detail-property-field" />
			{/if}
			<InlineField label="Priority" bind:value={form.priority} display={wo.priority} {editing} type="select" options={priorityOptions} testid="work-order-detail-priority" />
			<InlineField label="Category" bind:value={form.category} display={wo.category} {editing} error={formErrors.category} testid="work-order-detail-category" />
		</DetailCard>

		{#if ((caps.canViewTenantContact && (wo.requesterName || wo.requesterPhone || wo.requesterEmail)) || (caps.canViewResidents && (wo.residentNames?.length ?? 0) > 0) || (caps.canViewAccessInstructions && accessRows(wo).length > 0))}
			<Card.Root class="mt-6" data-testid="work-order-job-context">
				<Card.Header>
					<Card.Title>Job context</Card.Title>
				</Card.Header>
				<Card.Content class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
					{#if caps.canViewTenantContact && (wo.requesterName || wo.requesterPhone || wo.requesterEmail)}
						<div class="rounded-md border border-border p-3" data-testid="work-order-requestor">
							<p class="text-xs font-medium text-muted-foreground">Requestor</p>
							<p class="font-medium">{wo.requesterName ?? 'Resident'}</p>
							{#if wo.requesterPhone}<p class="text-sm text-muted-foreground">{wo.requesterPhone}</p>{/if}
							{#if wo.requesterEmail}<p class="text-sm text-muted-foreground">{wo.requesterEmail}</p>{/if}
						</div>
					{/if}
					{#if caps.canViewResidents && (wo.residentNames?.length ?? 0) > 0}
						<div class="rounded-md border border-border p-3" data-testid="work-order-residents">
							<p class="text-xs font-medium text-muted-foreground">Residents</p>
							<p class="font-medium">{wo.residentNames?.join(', ')}</p>
						</div>
					{/if}
					{#if caps.canViewAccessInstructions}
						{#each accessRows(wo) as [label, value]}
							<div class="rounded-md border border-border p-3" data-testid="work-order-access-row">
								<p class="text-xs font-medium text-muted-foreground">{label}</p>
								<p class="font-medium">{value}</p>
							</div>
						{/each}
					{/if}
				</Card.Content>
			</Card.Root>
		{/if}

		{#if caps.canViewPrivateNotes && wo.privateManagementNotes}
			<Card.Root class="mt-6" data-testid="work-order-private-notes">
				<Card.Header>
					<Card.Title>Private management notes</Card.Title>
				</Card.Header>
				<Card.Content>
					<p class="whitespace-pre-line text-sm">{wo.privateManagementNotes}</p>
				</Card.Content>
			</Card.Root>
		{/if}

		{#if caps.canCommentPublicly || caps.canCommentPrivately}
			<div class="mt-6 rounded-md border border-border p-3" data-testid="work-order-comment-actions">
				<label for="work-order-comment" class="text-sm font-medium">Add comment</label>
				<textarea
					id="work-order-comment"
					bind:value={commentBody}
					rows="3"
					class="mt-2 w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
					placeholder="Add a work-order update"
					data-testid="work-order-comment-body"
				></textarea>
				<div class="mt-3 flex flex-wrap items-center justify-between gap-2">
					{#if caps.canCommentPrivately}
						<label class="inline-flex items-center gap-2 text-sm text-muted-foreground">
							<input type="checkbox" bind:checked={commentPrivate} />
							Private management comment
						</label>
					{/if}
					<Button
						type="button"
						size="sm"
						onclick={submitComment}
						disabled={!commentBody.trim() || commentMutation.isPending}
						data-testid="work-order-comment-submit"
					>
						{commentMutation.isPending ? 'Adding...' : 'Add comment'}
					</Button>
				</div>
			</div>
		{/if}

		<!--
		  Costs & timing are editable directly in the page's edit mode (user decision: no reopen
		  workflow — works on Completed orders too; every change is audited in History below). In
		  view mode the optional rows stay hidden until set so the card stays calm; edit mode shows
		  them all so they can be filled in.
		-->
		<DetailCard title="Costs & timing" icon={Coins} accent="muted" testid="work-order-detail-costs" class="mt-6" contentClass="grid gap-x-8 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
			{@render dateField({ label: 'Requested', value: form.requestedAt, setValue: (v) => (form.requestedAt = v), display: formatDateOnly(wo.requestedAt), error: formErrors.requestedAt, testid: 'work-order-detail-requested' })}
			{#if editing || wo.scheduledFor}
				{@render dateTimeField({ label: 'Scheduled start', value: form.scheduledFor, setValue: (value) => (form.scheduledFor = value), display: wo.scheduledFor ? formatDateTime(wo.scheduledFor) : '', error: formErrors.scheduledFor, testid: 'work-order-detail-scheduled' })}
			{/if}
			{#if editing || wo.scheduledWindowEnd}
				{@render dateTimeField({ label: 'Service window end', value: form.scheduledWindowEnd, setValue: (value) => (form.scheduledWindowEnd = value), display: wo.scheduledWindowEnd ? formatDateTime(wo.scheduledWindowEnd) : '', error: formErrors.scheduledWindowEnd, testid: 'work-order-detail-scheduled-window-end' })}
			{/if}
			{#if editing || wo.completedAt}
				{@render dateField({ label: 'Completed', value: form.completedAt, setValue: (v) => (form.completedAt = v), display: wo.completedAt ? formatDateOnly(wo.completedAt) : '', error: formErrors.completedAt, testid: 'work-order-detail-completed' })}
			{/if}
			{#if caps.canViewCosts && (editing || wo.estimatedCost != null)}
				<InlineField label="Estimated Cost" type="number" bind:value={form.estimatedCost} display={wo.estimatedCost != null ? formatCurrency(wo.estimatedCost) : ''} {editing} error={formErrors.estimatedCost} testid="work-order-detail-estimated-cost" placeholder="0.00" />
			{/if}
			{#if caps.canViewCosts && (editing || wo.actualCost != null)}
				<InlineField label="Actual Cost" type="number" bind:value={form.actualCost} display={wo.actualCost != null ? formatCurrency(wo.actualCost) : ''} {editing} error={formErrors.actualCost} testid="work-order-detail-actual-cost" placeholder="0.00" />
			{/if}
		</DetailCard>

		<!-- Status timeline -->
		<Card.Root class="mt-6" data-testid="work-order-detail-timeline">
			<Card.Header>
				<Card.Title class="flex items-center gap-2 text-base">
					<History class="h-4 w-4 text-muted-foreground" />
					Status timeline
				</Card.Title>
			</Card.Header>
			<Card.Content>
				<WorkOrderTimeline timeline={wo.timeline} />
			</Card.Content>
		</Card.Root>

		<!-- Documents section -->
		<div class="mt-6" data-testid="work-order-detail-documents">
			<DocumentsPanel entityType="WorkOrder" entityId={workOrderId} title="Photos & documents" canUpload={caps.canUploadPhoto} canDelete={caps.canDeletePhoto} />
		</div>

		{#if (wo.photos?.length ?? 0) > 0}
			<Card.Root class="mt-6" data-testid="work-order-photo-summary">
				<Card.Header>
					<Card.Title>Photo notes</Card.Title>
				</Card.Header>
				<Card.Content class="flex flex-wrap gap-2">
					{#each wo.photos ?? [] as photo}
						<span class="rounded-full border border-border px-3 py-1 text-sm">{photo.caption ?? photo.fileName}</span>
					{/each}
				</Card.Content>
			</Card.Root>
		{/if}

		{#if (wo.activity?.length ?? 0) > 0}
			<Card.Root class="mt-6" data-testid="work-order-meaningful-activity">
				<Card.Header>
					<Card.Title>Activity</Card.Title>
				</Card.Header>
				<Card.Content class="space-y-3">
					{#each wo.activity ?? [] as item}
						<div class="rounded-md border border-border p-3">
							<p class="font-medium">{item.label ?? item.kind ?? 'Activity'}</p>
							{#if item.body || item.note}<p class="mt-1 text-sm text-muted-foreground">{item.body ?? item.note}</p>{/if}
							{#if item.actorLabel}<p class="mt-1 text-xs text-muted-foreground">{item.actorLabel}</p>{/if}
						</div>
					{/each}
				</Card.Content>
			</Card.Root>
		{/if}

		<!--
		  Per-record audit history. The status timeline above tracks status moves; this surfaces
		  every other recorded change (property, costs, timing dates, etc.) — who, what, and when —
		  so edits made here (incl. on Completed orders) are visible like any other field edit.
		-->
		{#if caps.canViewPrivateNotes}
			<div class="mt-6 rounded-lg border border-border bg-card p-4" data-testid="work-order-history-section">
				<h2 class="mb-1 text-base font-semibold">History</h2>
				<p class="mb-3 text-sm text-muted-foreground">Every recorded change to this work order — who, what, and when.</p>
				<RecordHistory entityType="WorkOrder" entityId={workOrderId} />
			</div>
		{/if}
	{/if}
</div>

<!-- Status change: optional note recorded on the timeline -->
<Dialog.Root open={pendingStatus !== null} onOpenChange={(v) => { if (!v) cancelStatusChange(); }}>
	<Dialog.Content data-testid="work-order-status-note-dialog">
		<Dialog.Header>
			<Dialog.Title>
				{pendingStatus ? `Mark ${formatStatusLabel(pendingStatus)}` : 'Mark status'}
			</Dialog.Title>
		</Dialog.Header>
		{#if wo && pendingStatus}
			<p class="text-sm text-muted-foreground">
				{formatStatusTransitionCopy(wo.status, pendingStatus)}
			</p>
		{/if}
		<div class="space-y-1.5">
			<label for="status-note" class="text-xs font-medium text-muted-foreground">Note (optional)</label>
			<textarea
				id="status-note"
				bind:value={statusNote}
				rows={3}
				maxlength={2000}
				placeholder="Add a note for the history (e.g. parts ordered, tenant notified)…"
				class="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
				data-testid="work-order-status-note-input"
			></textarea>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={cancelStatusChange} disabled={statusMutation.isPending} data-testid="work-order-status-note-cancel">
				Cancel
			</Button>
			<Button onclick={confirmStatusChange} disabled={statusMutation.isPending} data-testid="work-order-status-note-confirm">
				{statusMutation.isPending ? 'Updating…' : 'Update status'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Dispatch: pick a vendor (with rating summary) and text them the job -->
<Dialog.Root open={showDispatch} onOpenChange={(v) => { if (!v) closeDispatch(); }}>
	<Dialog.Content class="max-w-lg" data-testid="work-order-dispatch-dialog">
		<Dialog.Header>
			<Dialog.Title>Text a vendor the job</Dialog.Title>
			<Dialog.Description>
				Pick a vendor and we'll text them the work order. They reply <span class="font-medium">DONE</span> when
				finished and it closes itself.
			</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-3" data-testid="work-order-dispatch-vendor-list">
			<RemoteRecordSelect
				queryKey={['work-order-dispatch-vendor', portfolioId]}
				label="Vendor"
				value={selectedVendorId == null ? '' : String(selectedVendorId)}
				selectedLabel={selectedVendorLabel}
				placeholder="Choose a vendor"
				searchPlaceholder="Search vendors…"
				emptyLabel="No matching vendors"
				loadPage={loadVendorOptions}
				onValueChange={(value, option) => {
					selectedVendorId = value ? Number(value) : null;
					selectedVendorLabel = option?.label ?? null;
					selectedDispatchVendor = value ? vendorOptionCache[value] ?? null : null;
				}}
				testid="work-order-dispatch-vendor-select"
			/>
			<Button variant="outline" size="sm" href="/vendors?create=1" data-testid="work-order-dispatch-add-vendor">
				Add vendor
			</Button>

			{#if dispatchBlockReason}
				<p class="text-xs text-muted-foreground" data-testid="work-order-dispatch-block-reason">
					{dispatchBlockReason}
				</p>
			{/if}

			{#if selectedDispatchVendor}
				<div class="flex items-center justify-between gap-3 rounded-md border border-border p-3 text-sm" data-testid="work-order-dispatch-vendor-summary">
					<span>{selectedDispatchVendor.name}</span>
					<span class="text-xs text-muted-foreground">{ratingLabel(selectedDispatchVendor)}</span>
				</div>
			{/if}

			<div class="space-y-1.5">
				<label for="dispatch-note" class="text-xs font-medium text-muted-foreground">Note for the vendor (optional)</label>
				<textarea
					id="dispatch-note"
					bind:value={dispatchNote}
					rows={2}
					maxlength={500}
					placeholder="e.g. gate code 1234, tenant home after 5pm"
					class="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
					data-testid="work-order-dispatch-note"
				></textarea>
			</div>
		</div>

		<Dialog.Footer>
			<Button variant="outline" onclick={closeDispatch} disabled={dispatchMutation.isPending} data-testid="work-order-dispatch-cancel">
				Cancel
			</Button>
			<Button
				onclick={confirmDispatch}
				disabled={!canConfirmDispatch || dispatchMutation.isPending}
				data-testid="work-order-dispatch-confirm"
			>
				{dispatchMutation.isPending ? 'Texting…' : 'Text the job'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Rate the vendor on a completed work order -->
<Dialog.Root open={showRate} onOpenChange={(v) => { if (!v) closeRate(); }}>
	<Dialog.Content class="max-w-md" data-testid="work-order-rate-dialog">
		<Dialog.Header>
			<Dialog.Title>Rate {wo?.vendorName ?? 'the vendor'}</Dialog.Title>
			<Dialog.Description>How did this job go? Your rating builds the vendor's scorecard.</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-3">
			<div class="flex items-center gap-3">
				<StarRating bind:value={rateStars} interactive size="lg" testid="work-order-rate-stars" />
				<span class="text-sm text-muted-foreground" data-testid="work-order-rate-value">
					{rateStars > 0 ? `${rateStars} of 5` : 'Tap a star'}
				</span>
			</div>
			<div class="space-y-1.5">
				<label for="rate-comment" class="text-xs font-medium text-muted-foreground">Comment (optional)</label>
				<textarea
					id="rate-comment"
					bind:value={rateComment}
					rows={3}
					maxlength={2000}
					placeholder="e.g. on time, fixed it right the first time"
					class="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
					data-testid="work-order-rate-comment"
				></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={closeRate} disabled={rateMutation.isPending} data-testid="work-order-rate-cancel">
				Cancel
			</Button>
			<Button onclick={confirmRate} disabled={rateStars < 1 || rateMutation.isPending} data-testid="work-order-rate-confirm">
				{rateMutation.isPending ? 'Saving…' : 'Save rating'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={showDeleteConfirm}
	title="Delete work order"
	message={wo ? `Delete "${wo.title}"?` : ''}
	busy={deleteMutation.isPending}
	testid="work-order-delete"
	onconfirm={() => wo && deleteMutation.mutate(wo.id)}
	oncancel={() => (showDeleteConfirm = false)}
/>
