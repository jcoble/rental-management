<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { appointments } from '$lib/api/endpoints/appointments';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import type { Appointment } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { appointmentSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { Pencil, Trash2, CalendarCheck, CheckCircle, XCircle, UserX } from '@lucide/svelte';
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

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['appointment', id] });
		queryClient.invalidateQueries({ queryKey: ['appointments', portfolioId] });
	}

	// ── Edit form ─────────────────────────────────────────────────────────────
	const empty = {
		title: '', type: 'Showing', scheduledStart: '', scheduledEnd: '',
		propertyId: '', tenantId: '', prospectName: '', prospectEmail: '', assignedTo: '', status: 'Scheduled',
	};
	let showForm = $state(false);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});

	function openEdit(a: Appointment) {
		form = {
			title: a.title, type: a.type, status: a.status,
			scheduledStart: a.scheduledStart?.slice(0, 16) ?? '',
			scheduledEnd: a.scheduledEnd?.slice(0, 16) ?? '',
			propertyId: a.propertyId != null ? String(a.propertyId) : '',
			tenantId: a.tenantId != null ? String(a.tenantId) : '',
			prospectName: a.prospectName ?? '', prospectEmail: a.prospectEmail ?? '', assignedTo: a.assignedTo ?? '',
		};
		formErrors = {};
		showForm = true;
	}
	function closeForm() {
		showForm = false;
		formErrors = {};
	}
	function submit() {
		const result = parseForm(appointmentSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ id, data: { portfolioId, ...result.data } });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: ({ id: apptId, data }: { id: number; data: Record<string, unknown> }) =>
			appointments.update(apptId, data),
		onSuccess: () => {
			showSuccess('Appointment updated.');
			closeForm();
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
				<!-- Status actions -->
				{#if appt.status === 'Scheduled'}
					<Button
						variant="outline"
						size="sm"
						data-testid="appointment-complete"
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
				<Button variant="outline" size="sm" data-testid="appointment-edit" onclick={() => openEdit(appt)}>
					<Pencil class="mr-1.5 h-4 w-4" />
					Edit
				</Button>
				<Button variant="ghost" size="icon" data-testid="appointment-delete" aria-label="Delete appointment" onclick={() => (showDelete = true)}>
					<Trash2 class="h-4 w-4" />
				</Button>
			</div>
		</div>

		<!-- Info card -->
		<Card.Root data-testid="appointment-detail-card">
			<Card.Header>
				<Card.Title class="text-base">Details</Card.Title>
			</Card.Header>
			<Card.Content>
				<dl class="grid gap-x-6 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Status</dt>
						<dd class="mt-1"><StatusBadge status={appt.status} /></dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Type</dt>
						<dd class="mt-1 text-sm">{appt.type}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Start</dt>
						<dd class="mt-1 text-sm">{fmtDateTime(appt.scheduledStart)}</dd>
					</div>
					{#if appt.scheduledEnd}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">End</dt>
							<dd class="mt-1 text-sm">{fmtDateTime(appt.scheduledEnd)}</dd>
						</div>
					{/if}
					{#if appt.propertyName}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Property</dt>
							<dd class="mt-1 text-sm">{appt.propertyName}{appt.unitNumber ? ' · Unit ' + appt.unitNumber : ''}</dd>
						</div>
					{/if}
					{#if appt.tenantName}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Tenant</dt>
							<dd class="mt-1 text-sm">{appt.tenantName}</dd>
						</div>
					{/if}
					{#if appt.prospectName}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Prospect</dt>
							<dd class="mt-1 text-sm">{appt.prospectName}</dd>
						</div>
					{/if}
					{#if appt.prospectEmail}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Prospect Email</dt>
							<dd class="mt-1 text-sm">
								<a href="mailto:{appt.prospectEmail}" class="text-primary hover:underline">{appt.prospectEmail}</a>
							</dd>
						</div>
					{/if}
					{#if appt.assignedTo}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Assigned To</dt>
							<dd class="mt-1 text-sm">{appt.assignedTo}</dd>
						</div>
					{/if}
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
				</dl>
			</Card.Content>
		</Card.Root>

		<!-- Documents section -->
		<div class="mt-6" data-testid="appointment-detail-documents">
			<DocumentsPanel entityType="Appointment" entityId={id} />
		</div>
	{/if}
</div>

<!-- Edit dialog (reusing same form as list page) -->
<Dialog.Root
	open={showForm}
	onOpenChange={(v) => { if (!v) closeForm(); }}
>
	<Dialog.Content class="max-w-2xl">
		<Dialog.Header>
			<Dialog.Title>Edit Appointment</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3 md:grid-cols-3" data-testid="appointment-form">
			<div class="md:col-span-2">
				<Input data-testid="appointment-title-input" bind:value={form.title} placeholder="Appointment title" />
				{#if formErrors.title}<p class="mt-1 text-xs text-destructive" data-testid="appointment-title-error">{formErrors.title}</p>{/if}
			</div>
			<Select.Root type="single" bind:value={form.type}>
				<Select.Trigger class="w-full" data-testid="appointment-type-input">
					{form.type ? form.type : 'Select type'}
				</Select.Trigger>
				<Select.Content>
					{#each APPT_TYPES as t}<Select.Item value={t} label={t}>{t}</Select.Item>{/each}
				</Select.Content>
			</Select.Root>
			<div>
				<Input data-testid="appointment-start-input" type="datetime-local" bind:value={form.scheduledStart} />
				{#if formErrors.scheduledStart}<p class="mt-1 text-xs text-destructive" data-testid="appointment-start-error">{formErrors.scheduledStart}</p>{/if}
			</div>
			<Input data-testid="appointment-end-input" type="datetime-local" bind:value={form.scheduledEnd} />
			<Select.Root type="single" bind:value={form.status}>
				<Select.Trigger class="w-full" data-testid="appointment-status-input">
					{form.status ? form.status : 'Select status'}
				</Select.Trigger>
				<Select.Content>
					{#each APPT_STATUSES as s}<Select.Item value={s} label={s}>{s}</Select.Item>{/each}
				</Select.Content>
			</Select.Root>
			<Select.Root type="single" bind:value={form.propertyId}>
				<Select.Trigger class="w-full" data-testid="appointment-property-input">
					{form.propertyId ? (propertiesQuery.data?.find((p) => String(p.id) === form.propertyId)?.name ?? form.propertyId) : 'No property'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="No property">No property</Select.Item>
					{#each propertiesQuery.data || [] as property}<Select.Item value={String(property.id)} label={property.name}>{property.name}</Select.Item>{/each}
				</Select.Content>
			</Select.Root>
			<Select.Root type="single" bind:value={form.tenantId}>
				<Select.Trigger class="w-full" data-testid="appointment-tenant-input">
					{#if form.tenantId}
						{tenantsQuery.data?.find((t) => String(t.id) === form.tenantId)?.fullName || (() => { const t = tenantsQuery.data?.find((t) => String(t.id) === form.tenantId); return t ? `${t.firstName} ${t.lastName}` : form.tenantId; })()}
					{:else}
						No tenant
					{/if}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="No tenant">No tenant</Select.Item>
					{#each tenantsQuery.data || [] as tenant}<Select.Item value={String(tenant.id)} label={tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}>{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}</Select.Item>{/each}
				</Select.Content>
			</Select.Root>
			<Input data-testid="appointment-assigned-input" bind:value={form.assignedTo} placeholder="Assigned to" />
			<Input data-testid="appointment-prospect-name-input" bind:value={form.prospectName} placeholder="Prospect name" />
			<div>
				<Input data-testid="appointment-prospect-email-input" bind:value={form.prospectEmail} placeholder="Prospect email" />
				{#if formErrors.prospectEmail}<p class="mt-1 text-xs text-destructive" data-testid="appointment-prospect-email-error">{formErrors.prospectEmail}</p>{/if}
			</div>
		</div>
		<Dialog.Footer>
			<Button variant="outline" data-testid="appointment-form-cancel" onclick={closeForm}>Cancel</Button>
			<Button data-testid="appointment-form-save" onclick={submit} disabled={saveMutation.isPending}>{saveMutation.isPending ? 'Saving…' : 'Save Appointment'}</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={showDelete}
	title="Delete appointment"
	message={appt ? `Delete "${appt.title}"?` : ''}
	busy={deleteMutation.isPending}
	testid="appointment-delete"
	onconfirm={() => appt && deleteMutation.mutate(appt.id)}
	oncancel={() => (showDelete = false)}
/>
