<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, Pencil, Save, Trash2, X } from '@lucide/svelte';
	import { appointments } from '$lib/api/endpoints/appointments';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { appointmentSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';

	const queryClient = useQueryClient();
	const appointmentId = $derived(parseInt($page.params.id ?? '0', 10));
	const portfolioId = $derived(getCurrentPortfolioId());
	const APPT_TYPES = ['Showing', 'MoveIn', 'MoveOut', 'Inspection', 'MaintenanceVisit', 'OwnerMeeting'];
	const APPT_STATUSES = ['Scheduled', 'Confirmed', 'Completed', 'Cancelled', 'NoShow'];

	let editing = $state(false);
	let form = $state({
		title: '',
		type: 'Showing',
		status: 'Scheduled',
		scheduledStart: '',
		scheduledEnd: '',
		propertyId: '',
		tenantId: '',
		assignedTo: '',
		prospectName: '',
		prospectEmail: ''
	});
	let formErrors = $state<Record<string, string>>({});

	const appointmentQuery = createQuery(() => ({
		queryKey: ['appointment', appointmentId],
		queryFn: () => appointments.get(appointmentId),
		enabled: appointmentId > 0
	}));
	const propertiesQuery = createQuery(() => ({ queryKey: ['properties', portfolioId], queryFn: () => properties.list(portfolioId, { take: 200 }) }));
	const tenantsQuery = createQuery(() => ({ queryKey: ['tenants', portfolioId], queryFn: () => tenants.list(portfolioId, { take: 200 }) }));

	const appointment = $derived(appointmentQuery.data);
	const typeOptions = $derived(APPT_TYPES.map((value) => ({ value, label: value })));
	const statusOptions = $derived(APPT_STATUSES.map((value) => ({ value, label: value })));
	const propertyOptions = $derived([{ value: '', label: 'No property' }, ...(propertiesQuery.data ?? []).map((p) => ({ value: String(p.id), label: p.name }))]);
	const tenantOptions = $derived([{ value: '', label: 'No tenant' }, ...(tenantsQuery.data ?? []).map((t) => ({ value: String(t.id), label: t.fullName || `${t.firstName} ${t.lastName}` }))]);

	function startEditing() {
		if (!appointment) return;
		form = {
			title: appointment.title,
			type: appointment.type,
			status: appointment.status,
			scheduledStart: appointment.scheduledStart?.slice(0, 16) ?? '',
			scheduledEnd: appointment.scheduledEnd?.slice(0, 16) ?? '',
			propertyId: appointment.propertyId != null ? String(appointment.propertyId) : '',
			tenantId: appointment.tenantId != null ? String(appointment.tenantId) : '',
			assignedTo: appointment.assignedTo ?? '',
			prospectName: appointment.prospectName ?? '',
			prospectEmail: appointment.prospectEmail ?? ''
		};
		formErrors = {};
		editing = true;
	}

	function cancelEditing() {
		editing = false;
		formErrors = {};
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => appointments.update(appointmentId, data),
		onSuccess: () => {
			showSuccess('Appointment updated.');
			editing = false;
			queryClient.invalidateQueries({ queryKey: ['appointment', appointmentId] });
			queryClient.invalidateQueries({ queryKey: ['appointments', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function saveAppointment() {
		const result = parseForm(appointmentSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data });
	}

	const deleteMutation = createMutation(() => ({
		mutationFn: () => appointments.delete(appointmentId),
		onSuccess: () => {
			showSuccess('Appointment deleted.');
			goto('/appointments');
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));
</script>

<svelte:head>
	<title>{appointment?.title ?? 'Appointment'} - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="appointment-detail-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-3">
		<div class="min-w-0">
			<Button variant="ghost" href="/appointments" class="mb-2 -ml-3"><ArrowLeft class="h-4 w-4" />Appointments</Button>
			<h1 class="truncate text-2xl font-bold">{appointment?.title ?? 'Appointment'}</h1>
			<p class="text-sm text-muted-foreground">{appointment?.type ?? ''}{#if appointment?.status} · {appointment.status}{/if}</p>
		</div>
		{#if appointment}
			<div class="flex gap-2">
				{#if editing}
					<Button variant="outline" onclick={cancelEditing} disabled={saveMutation.isPending}><X class="h-4 w-4" />Cancel</Button>
					<Button onclick={saveAppointment} disabled={saveMutation.isPending}><Save class="h-4 w-4" />{saveMutation.isPending ? 'Saving...' : 'Save'}</Button>
				{:else}
					<Button variant="outline" onclick={startEditing}><Pencil class="h-4 w-4" />Edit</Button>
					<Button variant="destructive" onclick={() => deleteMutation.mutate()} disabled={deleteMutation.isPending}><Trash2 class="h-4 w-4" />Delete</Button>
				{/if}
			</div>
		{/if}
	</div>

	{#if appointmentQuery.isLoading}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Loading appointment...</div>
	{:else if !appointment}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Appointment not found.</div>
	{:else}
		<Card.Root>
			<Card.Header>
				<Card.Title>Appointment Details</Card.Title>
				<Card.Description>Schedule, assignment, and contact information.</Card.Description>
			</Card.Header>
			<Card.Content>
				<div class="grid gap-4 md:grid-cols-3">
					<InlineField label="Title" bind:value={form.title} display={appointment.title} {editing} error={formErrors.title} testid="appointment-detail-title" class="md:col-span-2" />
					<InlineField label="Type" bind:value={form.type} display={appointment.type} {editing} type="select" options={typeOptions} testid="appointment-detail-type" />
					<InlineField label="Start date and time" bind:value={form.scheduledStart} display={new Date(appointment.scheduledStart).toLocaleString()} {editing} type="datetime-local" error={formErrors.scheduledStart} testid="appointment-detail-start" />
					<InlineField label="End date and time" bind:value={form.scheduledEnd} display={appointment.scheduledEnd ? new Date(appointment.scheduledEnd).toLocaleString() : ''} {editing} type="datetime-local" testid="appointment-detail-end" />
					<InlineField label="Status" bind:value={form.status} display={appointment.status} {editing} type="select" options={statusOptions} testid="appointment-detail-status" />
					<InlineField label="Property" bind:value={form.propertyId} display={appointment.propertyName ?? 'No property'} {editing} type="select" options={propertyOptions} testid="appointment-detail-property" />
					<InlineField label="Tenant" bind:value={form.tenantId} display={appointment.tenantName ?? 'No tenant'} {editing} type="select" options={tenantOptions} testid="appointment-detail-tenant" />
					<InlineField label="Assigned to" bind:value={form.assignedTo} display={appointment.assignedTo} {editing} testid="appointment-detail-assigned" />
					<InlineField label="Prospect name" bind:value={form.prospectName} display={appointment.prospectName} {editing} testid="appointment-detail-prospect-name" />
					<InlineField label="Prospect email" bind:value={form.prospectEmail} display={appointment.prospectEmail} {editing} type="email" error={formErrors.prospectEmail} testid="appointment-detail-prospect-email" />
				</div>
			</Card.Content>
		</Card.Root>
	{/if}
</div>
