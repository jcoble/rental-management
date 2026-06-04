<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { appointments } from '$lib/api/endpoints/appointments';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { appointmentSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { Pencil, Save, Trash2, X, CalendarCheck, CheckCircle, XCircle, UserX } from '@lucide/svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const APPT_TYPES = ['Showing', 'MoveIn', 'MoveOut', 'Inspection', 'MaintenanceVisit', 'OwnerMeeting'];
	const APPT_STATUSES = ['Scheduled', 'Confirmed', 'Completed', 'Cancelled', 'NoShow'];

	const id = $derived(Number(page.params.id));

	const appointmentQuery = createQuery(() => ({
		queryKey: ['appointment', id],
		queryFn: () => appointments.get(id),
		enabled: !isNaN(id) && id > 0,
	}));
	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
	}));
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId, { take: 200 }),
	}));

	const appt = $derived(appointmentQuery.data);

	const typeOptions = $derived(APPT_TYPES.map((value) => ({ value, label: value })));
	const statusOptions = $derived(APPT_STATUSES.map((value) => ({ value, label: value })));
	const propertyOptions = $derived([
		{ value: '', label: 'No property' },
		...(propertiesQuery.data ?? []).map((p) => ({ value: String(p.id), label: p.name })),
	]);
	const tenantOptions = $derived([
		{ value: '', label: 'No tenant' },
		...(tenantsQuery.data ?? []).map((t) => ({
			value: String(t.id),
			label: t.fullName || `${t.firstName} ${t.lastName}`,
		})),
	]);

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['appointment', id] });
		queryClient.invalidateQueries({ queryKey: ['appointments', portfolioId] });
	}

	// ── Inline edit ─────────────────────────────────────────────────────────────
	let editing = $state(false);
	let form = $state({
		title: '', type: 'Showing', scheduledStart: '', scheduledEnd: '',
		propertyId: '', tenantId: '', prospectName: '', prospectEmail: '', assignedTo: '', status: 'Scheduled',
	});
	let formErrors = $state<Record<string, string>>({});

	function startEditing() {
		if (!appt) return;
		form = {
			title: appt.title, type: appt.type, status: appt.status,
			scheduledStart: appt.scheduledStart?.slice(0, 16) ?? '',
			scheduledEnd: appt.scheduledEnd?.slice(0, 16) ?? '',
			propertyId: appt.propertyId != null ? String(appt.propertyId) : '',
			tenantId: appt.tenantId != null ? String(appt.tenantId) : '',
			prospectName: appt.prospectName ?? '', prospectEmail: appt.prospectEmail ?? '', assignedTo: appt.assignedTo ?? '',
		};
		formErrors = {};
		editing = true;
	}
	function cancelEditing() {
		editing = false;
		formErrors = {};
	}
	function save() {
		const result = parseForm(appointmentSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => appointments.update(id, data),
		onSuccess: () => {
			showSuccess('Appointment updated.');
			editing = false;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: ({ id: apptId, status }: { id: number; status: string }) =>
			appointments.update(apptId, { status }),
		onSuccess: () => {
			showSuccess('Appointment updated.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ── Delete ────────────────────────────────────────────────────────────────
	let showDelete = $state(false);
	const deleteMutation = createMutation(() => ({
		mutationFn: (apptId: number) => appointments.delete(apptId),
		onSuccess: () => {
			showSuccess('Appointment deleted.');
			goto('/appointments');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ── Formatters ────────────────────────────────────────────────────────────
	function fmtDateTime(val?: string | null) {
		if (!val) return '—';
		const d = new Date(val);
		return isNaN(d.getTime()) ? val : d.toLocaleString();
	}
	function fmtDate(val?: string | null) {
		if (!val) return '—';
		const d = new Date(val);
		return isNaN(d.getTime()) ? val : d.toLocaleDateString();
	}
</script>

<svelte:head>
	<title>{appt ? appt.title : 'Appointment'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="appointment-detail-page">
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Appointments', href: '/appointments' },
				{ label: appt?.title ?? 'Appointment' },
			]}
		/>
	</div>

	{#if appointmentQuery.isLoading}
		<div class="flex items-center justify-center py-16 text-muted-foreground" data-testid="appointment-detail-loading">
			Loading…
		</div>
	{:else if appointmentQuery.isError}
		<div class="flex flex-col items-center gap-3 py-16" data-testid="appointment-detail-error">
			<p class="text-muted-foreground">Failed to load appointment.</p>
			<Button variant="outline" onclick={() => appointmentQuery.refetch()}>Retry</Button>
		</div>
	{:else if !appt}
		<div class="flex flex-col items-center gap-3 py-16" data-testid="appointment-detail-not-found">
			<p class="text-muted-foreground">Appointment not found.</p>
			<Button variant="outline" href="/appointments">Back to Appointments</Button>
		</div>
	{:else}
		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start justify-between gap-4">
			<div class="space-y-1">
				<div class="flex flex-wrap items-center gap-2">
					<h1 class="text-2xl font-bold" data-testid="appointment-detail-title">{appt.title}</h1>
					<StatusBadge status={appt.status} />
				</div>
				<p class="text-sm text-muted-foreground" data-testid="appointment-detail-type">{appt.type}</p>
				<p class="text-sm text-muted-foreground" data-testid="appointment-detail-when">
					{fmtDateTime(appt.scheduledStart)}{appt.scheduledEnd ? ' – ' + fmtDateTime(appt.scheduledEnd) : ''}
				</p>
			</div>

			<!-- Action buttons -->
			<div class="flex flex-wrap items-center gap-2">
				{#if editing}
					<Button variant="outline" size="sm" data-testid="appointment-edit-cancel" onclick={cancelEditing} disabled={saveMutation.isPending}>
						<X class="mr-1.5 h-4 w-4" />
						Cancel
					</Button>
					<Button size="sm" data-testid="appointment-edit-save" onclick={save} disabled={saveMutation.isPending}>
						<Save class="mr-1.5 h-4 w-4" />
						{saveMutation.isPending ? 'Saving…' : 'Save'}
					</Button>
				{:else}
					<!-- Status actions -->
					{#if appt.status === 'Scheduled'}
						<Button
							variant="outline"
							size="sm"
							data-testid="appointment-confirm"
							disabled={statusMutation.isPending}
							onclick={() => statusMutation.mutate({ id: appt.id, status: 'Confirmed' })}
						>
							<CalendarCheck class="mr-1.5 h-4 w-4" />
							Confirm
						</Button>
					{/if}
					{#if appt.status !== 'Completed' && appt.status !== 'Cancelled' && appt.status !== 'NoShow'}
						<Button
							variant="outline"
							size="sm"
							data-testid="appointment-complete"
							disabled={statusMutation.isPending}
							onclick={() => statusMutation.mutate({ id: appt.id, status: 'Completed' })}
						>
							<CheckCircle class="mr-1.5 h-4 w-4" />
							Mark complete
						</Button>
					{/if}
					{#if appt.status !== 'Cancelled' && appt.status !== 'Completed'}
						<Button
							variant="outline"
							size="sm"
							disabled={statusMutation.isPending}
							onclick={() => statusMutation.mutate({ id: appt.id, status: 'Cancelled' })}
						>
							<XCircle class="mr-1.5 h-4 w-4" />
							Cancel
						</Button>
					{/if}
					{#if appt.status === 'Scheduled' || appt.status === 'Confirmed'}
						<Button
							variant="outline"
							size="sm"
							disabled={statusMutation.isPending}
							onclick={() => statusMutation.mutate({ id: appt.id, status: 'NoShow' })}
						>
							<UserX class="mr-1.5 h-4 w-4" />
							No-show
						</Button>
					{/if}

					<!-- Edit / Delete -->
					<Button variant="outline" size="sm" data-testid="appointment-edit" onclick={startEditing}>
						<Pencil class="mr-1.5 h-4 w-4" />
						Edit
					</Button>
					<Button variant="ghost" size="icon" data-testid="appointment-delete" aria-label="Delete appointment" onclick={() => (showDelete = true)}>
						<Trash2 class="h-4 w-4" />
					</Button>
				{/if}
			</div>
		</div>

		<!-- Info card -->
		<Card.Root data-testid="appointment-detail-card">
			<Card.Header>
				<Card.Title class="text-base">Details</Card.Title>
			</Card.Header>
			<Card.Content>
				<div class="grid gap-x-6 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
					<InlineField label="Title" bind:value={form.title} display={appt.title} {editing} onedit={startEditing} error={formErrors.title} testid="appointment-detail-title-field" class="sm:col-span-2 lg:col-span-3" />
					<InlineField label="Type" bind:value={form.type} display={appt.type} {editing} onedit={startEditing} type="select" options={typeOptions} testid="appointment-detail-type-field" />
					<InlineField label="Status" bind:value={form.status} display={appt.status} {editing} onedit={startEditing} type="select" options={statusOptions} testid="appointment-detail-status-field" />
					<InlineField label="Start" bind:value={form.scheduledStart} display={fmtDateTime(appt.scheduledStart)} {editing} onedit={startEditing} type="datetime-local" error={formErrors.scheduledStart} testid="appointment-detail-start" />
					<InlineField label="End" bind:value={form.scheduledEnd} display={appt.scheduledEnd ? fmtDateTime(appt.scheduledEnd) : ''} {editing} onedit={startEditing} type="datetime-local" error={formErrors.scheduledEnd} testid="appointment-detail-end" />
					<InlineField label="Property" bind:value={form.propertyId} display={appt.propertyName ? `${appt.propertyName}${appt.unitNumber ? ' · Unit ' + appt.unitNumber : ''}` : ''} {editing} onedit={startEditing} type="select" options={propertyOptions} testid="appointment-detail-property" />
					<InlineField label="Tenant" bind:value={form.tenantId} display={appt.tenantName ?? ''} {editing} onedit={startEditing} type="select" options={tenantOptions} testid="appointment-detail-tenant" />
					<InlineField label="Prospect" bind:value={form.prospectName} display={appt.prospectName} {editing} onedit={startEditing} testid="appointment-detail-prospect-name" />
					<InlineField label="Prospect email" bind:value={form.prospectEmail} display={appt.prospectEmail} {editing} onedit={startEditing} type="email" error={formErrors.prospectEmail} testid="appointment-detail-prospect-email" />
					<InlineField label="Assigned to" bind:value={form.assignedTo} display={appt.assignedTo} {editing} onedit={startEditing} testid="appointment-detail-assigned" />
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Created</dt>
						<dd class="mt-1 text-sm">{fmtDate(appt.createdAt)}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Updated</dt>
						<dd class="mt-1 text-sm">{fmtDate(appt.updatedAt)}</dd>
					</div>
					{#if appt.notes}
						<div class="sm:col-span-2 lg:col-span-3">
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Notes</dt>
							<dd class="mt-1 whitespace-pre-wrap text-sm">{appt.notes}</dd>
						</div>
					{/if}
				</div>
			</Card.Content>
		</Card.Root>

		<!-- Documents section -->
		<div class="mt-6" data-testid="appointment-detail-documents">
			<DocumentsPanel entityType="Appointment" entityId={id} />
		</div>
	{/if}
</div>

<ConfirmDialog
	open={showDelete}
	title="Delete appointment"
	message={appt ? `Delete "${appt.title}"?` : ''}
	busy={deleteMutation.isPending}
	testid="appointment-delete"
	onconfirm={() => appt && deleteMutation.mutate(appt.id)}
	oncancel={() => (showDelete = false)}
/>
