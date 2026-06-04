<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { properties } from '$lib/api/endpoints/properties';
	import { vendors } from '$lib/api/endpoints/vendors';
	import type { Vendor } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { workOrderSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import StarRating from '$lib/components/shared/StarRating.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import { History, Pencil, Save, Trash2, X, MessageSquare, Star, Check } from '@lucide/svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';
	import WorkOrderTimeline from '$lib/components/shared/WorkOrderTimeline.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const id = $derived(Number($page.params.id));

	const WO_PRIORITIES = ['Low', 'Normal', 'High', 'Emergency'];

	// Status transitions: map current status → allowed next statuses
	const STATUS_TRANSITIONS: Record<string, string[]> = {
		New: ['Scheduled', 'InProgress', 'Cancelled'],
		Scheduled: ['InProgress', 'WaitingParts', 'Cancelled'],
		InProgress: ['WaitingParts', 'Completed', 'Cancelled'],
		WaitingParts: ['InProgress', 'Completed', 'Cancelled'],
		Completed: [],
		Cancelled: ['New'],
	};

	const workOrderQuery = createQuery(() => ({
		queryKey: ['work-order', id],
		queryFn: () => workOrders.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
	}));

	const wo = $derived(workOrderQuery.data);

	const priorityOptions = $derived(WO_PRIORITIES.map((value) => ({ value, label: value })));
	const propertyOptions = $derived(
		(propertiesQuery.data ?? []).map((p) => ({ value: String(p.id), label: p.name }))
	);

	// --- Inline edit ---
	let editing = $state(false);
	let form = $state({ propertyId: '', title: '', description: '', priority: 'Normal', category: 'General' });
	let formErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);

	function startEditing() {
		if (!wo) return;
		form = {
			propertyId: String(wo.propertyId),
			title: wo.title,
			description: wo.description,
			priority: wo.priority,
			category: wo.category,
		};
		formErrors = {};
		editing = true;
	}
	function cancelEditing() {
		editing = false;
		formErrors = {};
	}
	function save() {
		const result = parseForm(workOrderSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data });
	}

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['work-order', id] });
		queryClient.invalidateQueries({ queryKey: ['work-orders', portfolioId] });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => workOrders.update(id, data),
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
			workOrders.updateStatus(woId, status, note),
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
			goto('/maintenance');
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
		enabled: showDispatch && portfolioId > 0,
	}));
	const vendorList = $derived(vendorsQuery.data ?? []);

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
			workOrders.dispatch(id, { vendorId, note }),
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
		if (selectedVendorId == null) return;
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
			vendors.rate(vendorId, { stars, comment, workOrderId: id }),
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
		wo ? (STATUS_TRANSITIONS[wo.status] ?? []) : []
	);

	function formatDate(val: string | undefined | null): string {
		if (!val) return '—';
		const d = new Date(val);
		return isNaN(d.getTime()) ? val : d.toLocaleDateString();
	}

	function formatCurrency(val: number | undefined | null): string {
		if (val == null) return '—';
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(val);
	}
</script>

<svelte:head>
	<title>{wo?.title ?? 'Work Order'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="work-order-detail-page">
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Maintenance', href: '/maintenance' },
				{ label: wo?.title ?? 'Work Order' },
			]}
		/>
	</div>

	{#if workOrderQuery.isLoading}
		<p class="py-8 text-center text-sm text-muted-foreground" data-testid="work-order-detail-loading">Loading…</p>
	{:else if workOrderQuery.isError}
		<p class="py-8 text-center text-sm text-destructive" data-testid="work-order-detail-error">Failed to load work order.</p>
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
						<span data-testid="work-order-detail-unit">Unit {wo.unitNumber}</span>
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
							{nextStatus === 'InProgress' ? 'In Progress' : nextStatus}
						</Button>
					{/each}
					<!-- Text a vendor the job (they reply DONE to close it) -->
					{#if wo.status !== 'Completed' && wo.status !== 'Cancelled'}
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
					{#if wo.status === 'Completed' && wo.vendorId}
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
					<Button
						variant="outline"
						size="sm"
						data-testid="work-order-edit"
						onclick={startEditing}
					>
						<Pencil class="h-4 w-4" />
						Edit
					</Button>
					<Button
						variant="outline"
						size="sm"
						class="hover:text-destructive"
						data-testid="work-order-delete"
						onclick={() => (showDeleteConfirm = true)}
					>
						<Trash2 class="h-4 w-4" />
						Delete
					</Button>
				{/if}
			</div>
		</div>

		<!-- Dispatched hint: the vendor was texted and will reply DONE to close it -->
		{#if dispatched || (wo.vendorName && wo.status !== 'Completed' && wo.status !== 'Cancelled')}
			<div
				class="mb-6 flex items-start gap-3 rounded-md border border-blue-200 bg-blue-50 p-4 text-sm dark:border-blue-900 dark:bg-blue-950/40"
				data-testid="work-order-dispatched-hint"
			>
				<Check class="mt-0.5 h-4 w-4 shrink-0 text-blue-600 dark:text-blue-400" />
				<p class="text-blue-900 dark:text-blue-200">
					{#if wo.vendorName}
						<span class="font-medium">{wo.vendorName}</span> has the job.
					{:else}
						The vendor has the job.
					{/if}
					When they text back <span class="font-medium">DONE</span>, this work order closes automatically.
				</p>
			</div>
		{/if}

		<!-- Info Card -->
		<Card.Root>
			<Card.Header>
				<Card.Title>Details</Card.Title>
			</Card.Header>
			<Card.Content>
				<div class="grid gap-x-8 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
					<InlineField label="Title" bind:value={form.title} display={wo.title} {editing} onedit={startEditing} error={formErrors.title} testid="work-order-detail-title-field" class="sm:col-span-2 lg:col-span-3" />
					<InlineField label="Description" bind:value={form.description} display={wo.description} {editing} onedit={startEditing} type="textarea" error={formErrors.description} testid="work-order-detail-description" class="sm:col-span-2 lg:col-span-3" />
					<InlineField label="Property" bind:value={form.propertyId} display={wo.propertyName} {editing} onedit={startEditing} type="select" options={propertyOptions} error={formErrors.propertyId} testid="work-order-detail-property-field" />
					<InlineField label="Priority" bind:value={form.priority} display={wo.priority} {editing} onedit={startEditing} type="select" options={priorityOptions} testid="work-order-detail-priority" />
					<InlineField label="Category" bind:value={form.category} display={wo.category} {editing} onedit={startEditing} error={formErrors.category} testid="work-order-detail-category" />
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Requested</dt>
						<dd class="mt-1 font-mono text-sm tabular-nums" data-testid="work-order-detail-requested">{formatDate(wo.requestedAt)}</dd>
					</div>
					{#if wo.scheduledFor}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Scheduled For</dt>
							<dd class="mt-1 font-mono text-sm tabular-nums" data-testid="work-order-detail-scheduled">{formatDate(wo.scheduledFor)}</dd>
						</div>
					{/if}
					{#if wo.completedAt}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Completed</dt>
							<dd class="mt-1 font-mono text-sm tabular-nums" data-testid="work-order-detail-completed">{formatDate(wo.completedAt)}</dd>
						</div>
					{/if}
					{#if wo.estimatedCost != null}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Estimated Cost</dt>
							<dd class="mt-1 font-mono text-sm tabular-nums" data-testid="work-order-detail-estimated-cost">{formatCurrency(wo.estimatedCost)}</dd>
						</div>
					{/if}
					{#if wo.actualCost != null}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Actual Cost</dt>
							<dd class="mt-1 font-mono text-sm tabular-nums" data-testid="work-order-detail-actual-cost">{formatCurrency(wo.actualCost)}</dd>
						</div>
					{/if}
				</div>
			</Card.Content>
		</Card.Root>

		<!-- Status history timeline -->
		<Card.Root class="mt-6" data-testid="work-order-detail-timeline">
			<Card.Header>
				<Card.Title class="flex items-center gap-2 text-base">
					<History class="h-4 w-4 text-muted-foreground" />
					Status history
				</Card.Title>
			</Card.Header>
			<Card.Content>
				<WorkOrderTimeline timeline={wo.timeline} />
			</Card.Content>
		</Card.Root>

		<!-- Documents section -->
		<div class="mt-6" data-testid="work-order-detail-documents">
			<DocumentsPanel entityType="WorkOrder" entityId={id} title="Photos & documents" />
		</div>
	{/if}
</div>

<!-- Status change: optional note recorded on the timeline -->
<Dialog.Root open={pendingStatus !== null} onOpenChange={(v) => { if (!v) cancelStatusChange(); }}>
	<Dialog.Content data-testid="work-order-status-note-dialog">
		<Dialog.Header>
			<Dialog.Title>
				{pendingStatus === 'InProgress' ? 'Mark In Progress' : `Mark ${pendingStatus ?? ''}`}
			</Dialog.Title>
		</Dialog.Header>
		{#if wo && pendingStatus}
			<p class="text-sm text-muted-foreground">
				Change status from <span class="font-medium text-foreground">{wo.status}</span> to
				<span class="font-medium text-foreground">{pendingStatus === 'InProgress' ? 'In Progress' : pendingStatus}</span>.
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
			<p class="py-6 text-center text-sm text-muted-foreground" data-testid="work-order-dispatch-empty">
				No vendors yet. Add one under Owners &amp; Vendors first.
			</p>
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
								{#if v.preferred}<StatusBadge status="Preferred" map={{ Preferred: { class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-400 dark:border-green-800' } }} />{/if}
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
				disabled={selectedVendorId == null || dispatchMutation.isPending}
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
