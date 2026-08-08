<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { appointments } from '$lib/api/endpoints/appointments';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import type { Appointment } from '$lib/types';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { appointmentSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DateTimePicker from '$lib/components/shared/DateTimePicker.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Pencil, Save, Trash2, X, CalendarCheck, CheckCircle, XCircle, UserX, CalendarClock, Users } from '@lucide/svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';
	import { buildAppointmentDetailSavePayload, createAppointmentDetailEditForm } from '../appointment-detail-form';
	import { labelForType } from '../calendar-utils';
	import { appointmentDetailStatusActions, appointmentDetailStatusLabel, appointmentStatusOptions } from '../appointment-detail-state';
	import { invalidateAppointmentQueries } from '../appointment-query-keys';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const APPT_TYPES = ['Showing', 'MoveIn', 'MoveOut', 'Inspection', 'MaintenanceVisit', 'OwnerMeeting'];

	type AppointmentWorkOrderOption = {
		id: number;
		label: string;
		description?: string | null;
		propertyId?: number | null;
		unitId?: number | null;
		tenantId?: number | null;
		propertyName?: string | null;
		tenantName?: string | null;
	};

	const id = $derived(Number(page.params.id));

	const appointmentQuery = createQuery(() => ({
		queryKey: ['appointment', id],
		queryFn: () => appointments.get(id),
		enabled: !isNaN(id) && id > 0,
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
	async function loadTenantOptions(params: { search?: string; skip: number; take: number }) {
		const result = await tenants.listPage(portfolioId, { ...params, sort: 'lastName' });
		return {
			...result,
			items: result.items.map((tenant) => ({
				id: tenant.id,
				label: tenant.fullName || `${tenant.firstName} ${tenant.lastName}`,
				description: tenant.email || tenant.phone || null
			}))
		};
	}
	async function loadWorkOrderOptions(params: { search?: string; skip: number; take: number }) {
		const result = await workOrders.listPage(portfolioId, { ...params, sort: '-requestedAt' });
		return {
			...result,
			items: result.items.map((workOrder) => ({
				id: workOrder.id,
				label: `#${workOrder.id} ${workOrder.title}`,
				description: [
					workOrder.propertyName,
					workOrder.unitNumber ? `Unit ${workOrder.unitNumber}` : null,
					workOrder.tenantName,
					workOrder.status
				].filter(Boolean).join(' · '),
				propertyId: workOrder.propertyId,
				unitId: workOrder.unitId,
				tenantId: workOrder.tenantId,
				propertyName: workOrder.propertyName,
				tenantName: workOrder.tenantName
			}))
		};
	}

	const appt = $derived(appointmentQuery.data);

	const typeOptions = $derived(APPT_TYPES.map((value) => ({ value, label: labelForType(value) })));
	const statusOptions = appointmentStatusOptions();
	const statusActions = $derived(appt ? appointmentDetailStatusActions(appt.status) : null);

	function invalidate() {
		invalidateAppointmentQueries(queryClient, portfolioId, id);
	}

	// ── Inline edit ─────────────────────────────────────────────────────────────
	let editing = $state(false);
	let form = $state({
		title: '', type: 'Showing', scheduledStart: '', scheduledEnd: '',
		propertyId: '', unitId: '', tenantId: '', workOrderId: '', prospectName: '', prospectEmail: '', assignedTo: '', status: 'Scheduled',
	});
	let formErrors = $state<Record<string, string>>({});
	let selectedPropertyLabel = $state<string | null>(null);
	let selectedTenantLabel = $state<string | null>(null);
	let selectedWorkOrderLabel = $state<string | null>(null);

	function startEditing() {
		if (!appt) return;
		form = createAppointmentDetailEditForm(appt);
		selectedPropertyLabel = appt.propertyName ?? null;
		selectedTenantLabel = appt.tenantName ?? null;
		selectedWorkOrderLabel = appt.workOrderId != null ? `#${appt.workOrderId}` : null;
		formErrors = {};
		editing = true;
	}
	function cancelEditing() {
		editing = false;
		formErrors = {};
	}
	function applyWorkOrderSelection(option: AppointmentWorkOrderOption | null) {
		selectedWorkOrderLabel = option?.label ?? null;
		if (!option) return;
		form.propertyId = option.propertyId != null ? String(option.propertyId) : '';
		form.unitId = option.unitId != null ? String(option.unitId) : '';
		form.tenantId = option.tenantId != null ? String(option.tenantId) : '';
		selectedPropertyLabel = option.propertyName ?? null;
		selectedTenantLabel = option.tenantName ?? null;
	}
	function save() {
		const result = parseForm(appointmentSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate(buildAppointmentDetailSavePayload(result.data, form, portfolioId));
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
		mutationFn: (appointment: Appointment) => appointments.delete(appointment.id, appointment.propertyId),
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
		<LoadingState label="Loading appointment details" variant="page" testid="appointment-detail-loading" />
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
		<div class="rc-hero mb-6 flex flex-wrap items-start justify-between gap-4">
			<div class="space-y-1">
				<div class="flex flex-wrap items-center gap-2">
					<h1 class="text-2xl font-bold" data-testid="appointment-detail-title">{appt.title}</h1>
					<StatusBadge status={appt.status} />
				</div>
				<p class="text-sm text-muted-foreground" data-testid="appointment-detail-type">{labelForType(appt.type)}</p>
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
					{#if statusActions?.canConfirm}
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
					{#if statusActions?.canComplete}
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
					{#if statusActions?.canCancel}
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
					{#if statusActions?.canNoShow}
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

		<!-- Grouped detail cards -->
		<div class="grid gap-6 lg:grid-cols-2">
			<DetailCard title="What & when" icon={CalendarClock} accent="primary" testid="appointment-detail-card" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Title" bind:value={form.title} display={appt.title} {editing} error={formErrors.title} testid="appointment-detail-title-field" class="sm:col-span-2" />
				<InlineField label="Type" bind:value={form.type} display={labelForType(appt.type)} {editing} type="select" options={typeOptions} testid="appointment-detail-type-field" />
				<InlineField label="Status" bind:value={form.status} display={appointmentDetailStatusLabel(appt.status)} {editing} type="select" options={statusOptions} testid="appointment-detail-status-field" />
				{@render dateTimeField({ label: 'Start', value: form.scheduledStart, setValue: (value) => (form.scheduledStart = value), display: fmtDateTime(appt.scheduledStart), error: formErrors.scheduledStart, testid: 'appointment-detail-start' })}
				{@render dateTimeField({ label: 'End', value: form.scheduledEnd, setValue: (value) => (form.scheduledEnd = value), display: appt.scheduledEnd ? fmtDateTime(appt.scheduledEnd) : '', error: formErrors.scheduledEnd, testid: 'appointment-detail-end' })}
			</DetailCard>

			<DetailCard title="Who" icon={Users} accent="muted" testid="appointment-detail-who" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				{#if editing}
					<RemoteRecordSelect
						queryKey={['appointment-detail-property', portfolioId]}
						label="Property"
						bind:value={form.propertyId}
						selectedLabel={selectedPropertyLabel}
						placeholder="No property"
						clearLabel="No property"
						searchPlaceholder="Search properties…"
						emptyLabel="No matching properties"
						loadPage={loadPropertyOptions}
						onValueChange={(_value, option) => (selectedPropertyLabel = option?.label ?? null)}
						testid="appointment-detail-property"
					/>
					<RemoteRecordSelect
						queryKey={['appointment-detail-tenant', portfolioId]}
						label="Tenant"
						bind:value={form.tenantId}
						selectedLabel={selectedTenantLabel}
						placeholder="No tenant"
						clearLabel="No tenant"
						searchPlaceholder="Search tenants…"
						emptyLabel="No matching tenants"
						loadPage={loadTenantOptions}
						onValueChange={(_value, option) => (selectedTenantLabel = option?.label ?? null)}
						testid="appointment-detail-tenant"
					/>
					<RemoteRecordSelect
						queryKey={['appointment-detail-work-order', portfolioId]}
						label="Work order"
						bind:value={form.workOrderId}
						selectedLabel={selectedWorkOrderLabel}
						placeholder="No work order"
						clearLabel="No work order"
						searchPlaceholder="Search work orders…"
						emptyLabel="No matching work orders"
						loadPage={loadWorkOrderOptions}
						onValueChange={(_value, option) => applyWorkOrderSelection(option as AppointmentWorkOrderOption | null)}
						testid="appointment-detail-work-order"
					/>
				{:else}
					<InlineField label="Property" bind:value={form.propertyId} display={appt.propertyName ? `${appt.propertyName}${appt.unitNumber ? ' · Unit ' + appt.unitNumber : ''}` : ''} editing={false} testid="appointment-detail-property" />
					<InlineField label="Tenant" bind:value={form.tenantId} display={appt.tenantName ?? ''} editing={false} testid="appointment-detail-tenant" />
					<InlineField label="Work order" bind:value={form.workOrderId} display={appt.workOrderId != null ? `#${appt.workOrderId}` : ''} editing={false} testid="appointment-detail-work-order" />
				{/if}
				<InlineField label="Prospect" bind:value={form.prospectName} display={appt.prospectName} {editing} testid="appointment-detail-prospect-name" />
				<InlineField label="Prospect email" bind:value={form.prospectEmail} display={appt.prospectEmail} {editing} type="email" error={formErrors.prospectEmail} testid="appointment-detail-prospect-email" />
				<InlineField label="Assigned to" bind:value={form.assignedTo} display={appt.assignedTo} {editing} testid="appointment-detail-assigned" />
			</DetailCard>

			<DetailCard title="Record" icon={CalendarCheck} accent="muted" testid="appointment-detail-record" class="lg:col-span-2" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<div>
					<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Created</dt>
					<dd class="mt-1 text-sm">{fmtDate(appt.createdAt)}</dd>
				</div>
				<div>
					<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Updated</dt>
					<dd class="mt-1 text-sm">{fmtDate(appt.updatedAt)}</dd>
				</div>
				{#if appt.notes}
					<div class="sm:col-span-2">
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Notes</dt>
						<dd class="mt-1 whitespace-pre-wrap text-sm">{appt.notes}</dd>
					</div>
				{/if}
			</DetailCard>
		</div>

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
	onconfirm={() => appt && deleteMutation.mutate(appt)}
	oncancel={() => (showDelete = false)}
/>
