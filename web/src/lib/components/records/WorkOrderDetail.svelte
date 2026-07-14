<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { properties } from '$lib/api/endpoints/properties';
	import { vendors } from '$lib/api/endpoints/vendors';
	import type { Vendor } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { workOrderDetailSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import StarRating from '$lib/components/shared/StarRating.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import { History, Pencil, Save, Trash2, X, MessageSquare, Star, Check, Wrench, Coins, Phone, Mail } from '@lucide/svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';
	import WorkOrderTimeline from '$lib/components/shared/WorkOrderTimeline.svelte';
	import {
		canDispatchToVendor,
		dispatchVendorBlockReason,
		formatStatusTransitionCopy,
		shouldShowActiveDispatchHint,
		workOrderStatusActionTargets,
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

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
	}));

	const wo = $derived(workOrderQuery.data);
	const canManageWork = $derived(hasCapability('work.manage'));
	const canAssignWork = $derived(hasCapability('responsibility.assign-existing-member'));

	const responsibilitiesQuery = createQuery(() => ({
		queryKey: ['work-order-responsibilities', workOrderId],
		queryFn: () => workOrders.responsibilities(workOrderId),
		enabled: workOrderId > 0,
	}));
	const candidatesQuery = createQuery(() => ({
		queryKey: ['work-order-responsibility-candidates', workOrderId],
		queryFn: () => workOrders.responsibilityCandidates(workOrderId),
		enabled: workOrderId > 0 && canAssignWork,
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
	const propertyOptions = $derived(
		(propertiesQuery.data ?? []).map((p) => ({ value: String(p.id), label: p.name }))
	);

	// --- Inline edit ---
	let editing = $state(false);
	let form = $state({
		propertyId: '',
		title: '',
		description: '',
		priority: 'Normal',
		category: 'General',
		// Costs & timing (editable directly, including on Completed orders — no reopen workflow).
		// Dates are bound as `yyyy-MM-dd` for the DatePicker; costs as plain numeric strings.
		requestedAt: '',
		scheduledFor: '',
		completedAt: '',
		estimatedCost: '',
		actualCost: '',
	});
	let formErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);

	// Snapshot of the timing dates (yyyy-MM-dd) exactly as seeded when editing began, so save() re-sends
	// ONLY a date the user actually changed. These fields bind a full timestamp as date-only; re-submitting
	// an untouched date would overwrite the stored instant's time-of-day with 00:00:00Z (BUG-3). A field
	// left equal to its seed is dropped from the PATCH → "leave unchanged" server-side.
	let seededDates = $state({ requestedAt: '', scheduledFor: '', completedAt: '' });

	function startEditing() {
		if (!wo) return;
		form = {
			propertyId: String(wo.propertyId),
			title: wo.title,
			description: wo.description,
			priority: wo.priority,
			category: wo.category,
			// Work-order timing fields are full timestamps; the DatePicker edits the calendar day,
			// so seed with just the date part. Costs seed as strings ('' when not set).
			requestedAt: wo.requestedAt?.slice(0, 10) ?? '',
			scheduledFor: wo.scheduledFor?.slice(0, 10) ?? '',
			completedAt: wo.completedAt?.slice(0, 10) ?? '',
			estimatedCost: wo.estimatedCost != null ? String(wo.estimatedCost) : '',
			actualCost: wo.actualCost != null ? String(wo.actualCost) : '',
		};
		// Remember the seeded date-only values to dirty-check against on save (see seededDates).
		seededDates = {
			requestedAt: form.requestedAt,
			scheduledFor: form.scheduledFor,
			completedAt: form.completedAt,
		};
		formErrors = {};
		editing = true;
	}
	function cancelEditing() {
		editing = false;
		formErrors = {};
	}
	function save() {
		const result = parseForm(workOrderDetailSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		// Drop any timing date the user didn't actually change so an unchanged value isn't re-sent and
		// truncated to UTC-midnight (BUG-3); a dropped field is left untouched server-side. Costs and the
		// Request fields always go through.
		const data: Record<string, unknown> = { ...result.data };
		for (const field of ['requestedAt', 'scheduledFor', 'completedAt'] as const) {
			if (form[field] === seededDates[field]) delete data[field];
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
			canManageWork
				? workOrders.updateStatus(woId, status, note)
				: workOrders.updateAssigned(woId, {
						status,
						technicianNote: note,
						expectedUpdatedAtUtc: wo?.updatedAt
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

	// --- Dispatch to a vendor (text them the job) ---
	// Vendors are only loaded once the dispatch dialog is opened, to keep the page light.
	let showDispatch = $state(false);
	let selectedVendorId = $state<number | null>(null);
	let dispatchNote = $state('');
	let dispatched = $state(false);

	const vendorsQuery = createQuery(() => ({
		queryKey: ['vendors', portfolioId],
		queryFn: () => vendors.list(portfolioId, { take: 200 }),
		// Loaded for the dispatch picker AND whenever this work order already has a vendor, so the
		// manager-only call/text/email contact links can resolve the assigned vendor's details.
		enabled: portfolioId > 0 && (showDispatch || (canManageWork && wo?.vendorId != null)),
	}));
	const vendorList = $derived(vendorsQuery.data ?? []);
	const selectedDispatchVendor = $derived(
		selectedVendorId == null ? null : (vendorList.find((v) => v.id === selectedVendorId) ?? null)
	);
	const canConfirmDispatch = $derived(canDispatchToVendor(selectedDispatchVendor));
	const dispatchBlockReason = $derived(dispatchVendorBlockReason(selectedDispatchVendor));

	// The vendor assigned to this work order (when any), resolved from the loaded list so we can offer
	// call / text / email contact actions matching the mobile trio. tel:/sms: hrefs strip everything but
	// digits and a leading +, and the whole value is URL-encoded.
	const assignedVendor = $derived(
		canManageWork && wo?.vendorId != null
			? (vendorList.find((v) => v.id === wo.vendorId) ?? null)
			: null
	);
	function telHref(scheme: 'tel' | 'sms', phone: string | null | undefined): string | null {
		if (!phone) return null;
		const cleaned = phone.replace(/[^\d+]/g, '');
		return cleaned ? `${scheme}:${encodeURIComponent(cleaned)}` : null;
	}

	function openDispatch() {
		selectedVendorId = null;
		dispatchNote = '';
		showDispatch = true;
	}
	function closeDispatch() {
		showDispatch = false;
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
		wo ? workOrderStatusActionTargets(wo.status, !canManageWork) : []
	);

	function formatCurrency(val: number | undefined | null): string {
		if (val == null) return '—';
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(val);
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

<svelte:head>
	<title>{wo?.title ?? 'Work Order'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="work-order-detail-page">
	{#if workOrderQuery.isLoading}
		<p class="py-8 text-center text-sm text-muted-foreground" data-testid="work-order-detail-loading">Loading…</p>
	{:else if workOrderQuery.isError}
		<p class="py-8 text-center text-sm text-destructive" data-testid="work-order-detail-error">Failed to load work order.</p>
	{:else if !wo}
		<p class="py-8 text-center text-sm text-muted-foreground" data-testid="work-order-detail-not-found">Work order not found.</p>
	{:else}
		<Card.Root class="mb-6" data-testid="work-order-responsibility-card">
			<Card.Header>
				<Card.Title>Technician responsibility</Card.Title>
				<Card.Description>
					{currentPrimary ? `${currentPrimary.memberDisplayName} is responsible for this work order.` : 'No technician is currently assigned.'}
				</Card.Description>
			</Card.Header>
			{#if canAssignWork}
				<Card.Content class="flex flex-wrap items-end gap-3">
					<label class="grid min-w-64 gap-1 text-sm">Technician
						<select class="h-10 rounded-md border bg-background px-3" bind:value={selectedCandidateId}>
							<option value="">Choose a technician</option>
							{#each candidatesQuery.data ?? [] as candidate}
								<option value={String(candidate.membershipRoleAssignmentId)}>{candidate.memberDisplayName}</option>
							{/each}
						</select>
					</label>
					<label class="grid min-w-64 flex-1 gap-1 text-sm">Reason
						<input class="h-10 rounded-md border bg-background px-3" bind:value={assignmentReason} maxlength="1000" />
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
					{#if canManageWork && wo.status !== 'Completed' && wo.status !== 'Cancelled'}
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
					{#if canManageWork && wo.status === 'Completed' && wo.vendorId}
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
					{#if canManageWork}<Button
						variant="outline"
						size="sm"
						data-testid="work-order-edit"
						onclick={startEditing}
					>
						<Pencil class="h-4 w-4" />
						Edit
					</Button>{/if}
					{#if canManageWork}<Button
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
			<InlineField label="Property" bind:value={form.propertyId} display={wo.propertyName} {editing} type="select" options={propertyOptions} error={formErrors.propertyId} testid="work-order-detail-property-field" />
			<InlineField label="Priority" bind:value={form.priority} display={wo.priority} {editing} type="select" options={priorityOptions} testid="work-order-detail-priority" />
			<InlineField label="Category" bind:value={form.category} display={wo.category} {editing} error={formErrors.category} testid="work-order-detail-category" />
		</DetailCard>

		<!--
		  Costs & timing are editable directly in the page's edit mode (user decision: no reopen
		  workflow — works on Completed orders too; every change is audited in History below). In
		  view mode the optional rows stay hidden until set so the card stays calm; edit mode shows
		  them all so they can be filled in.
		-->
		<DetailCard title="Costs & timing" icon={Coins} accent="muted" testid="work-order-detail-costs" class="mt-6" contentClass="grid gap-x-8 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
			{@render dateField({ label: 'Requested', value: form.requestedAt, setValue: (v) => (form.requestedAt = v), display: formatDateOnly(wo.requestedAt), error: formErrors.requestedAt, testid: 'work-order-detail-requested' })}
			{#if editing || wo.scheduledFor}
				{@render dateField({ label: 'Scheduled For', value: form.scheduledFor, setValue: (v) => (form.scheduledFor = v), display: wo.scheduledFor ? formatDateOnly(wo.scheduledFor) : '', error: formErrors.scheduledFor, testid: 'work-order-detail-scheduled' })}
			{/if}
			{#if editing || wo.completedAt}
				{@render dateField({ label: 'Completed', value: form.completedAt, setValue: (v) => (form.completedAt = v), display: wo.completedAt ? formatDateOnly(wo.completedAt) : '', error: formErrors.completedAt, testid: 'work-order-detail-completed' })}
			{/if}
			{#if editing || wo.estimatedCost != null}
				<InlineField label="Estimated Cost" type="number" bind:value={form.estimatedCost} display={wo.estimatedCost != null ? formatCurrency(wo.estimatedCost) : ''} {editing} error={formErrors.estimatedCost} testid="work-order-detail-estimated-cost" placeholder="0.00" />
			{/if}
			{#if editing || wo.actualCost != null}
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
			<DocumentsPanel entityType="WorkOrder" entityId={workOrderId} title="Photos & documents" />
		</div>

		<!--
		  Per-record audit history. The status timeline above tracks status moves; this surfaces
		  every other recorded change (property, costs, timing dates, etc.) — who, what, and when —
		  so edits made here (incl. on Completed orders) are visible like any other field edit.
		-->
		<div class="mt-6 rounded-lg border border-border bg-card p-4" data-testid="work-order-history-section">
			<h2 class="mb-1 text-base font-semibold">History</h2>
			<p class="mb-3 text-sm text-muted-foreground">Every recorded change to this work order — who, what, and when.</p>
			<RecordHistory entityType="WorkOrder" entityId={workOrderId} />
		</div>
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

		{#if vendorsQuery.isLoading}
			<p class="py-6 text-center text-sm text-muted-foreground" data-testid="work-order-dispatch-loading">Loading vendors…</p>
		{:else if vendorList.length === 0}
			<div class="space-y-3 py-6 text-center" data-testid="work-order-dispatch-empty">
				<p class="text-sm text-muted-foreground">No vendors yet. Add a vendor before texting this job.</p>
				<Button variant="outline" size="sm" href="/vendors?create=1" data-testid="work-order-dispatch-add-vendor">
					Add vendor
				</Button>
			</div>
		{:else}
			<div class="max-h-64 space-y-2 overflow-y-auto pr-1" data-testid="work-order-dispatch-vendor-list">
				{#each vendorList as v (v.id)}
					<button
						type="button"
						class="flex w-full items-center justify-between gap-3 rounded-md border p-3 text-left transition-colors hover:bg-accent
							{selectedVendorId === v.id ? 'border-primary ring-1 ring-primary' : 'border-border'}"
						data-testid="work-order-dispatch-vendor-{v.id}"
						aria-pressed={selectedVendorId === v.id}
						onclick={() => (selectedVendorId = v.id)}
					>
						<div class="min-w-0">
							<div class="flex items-center gap-2">
								<span class="truncate font-medium">{v.name}</span>
								{#if v.preferred}<StatusBadge status="Preferred" map={{ Preferred: { class: 'm3-tone-chip border m3-tone--success' } }} />{/if}
							</div>
							<p class="truncate text-xs text-muted-foreground">{v.serviceType}{v.phone ? ` · ${v.phone}` : ' · no phone on file'}</p>
						</div>
						<div class="flex shrink-0 items-center gap-2">
							{#if v.averageRating != null && v.ratingCount > 0}
								<StarRating value={v.averageRating} size="sm" testid="work-order-dispatch-vendor-{v.id}-stars" />
							{/if}
							<span class="whitespace-nowrap text-xs text-muted-foreground">{ratingLabel(v)}</span>
						</div>
					</button>
				{/each}
			</div>

			{#if dispatchBlockReason}
				<p class="text-xs text-muted-foreground" data-testid="work-order-dispatch-block-reason">
					{dispatchBlockReason}
				</p>
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
		{/if}

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
