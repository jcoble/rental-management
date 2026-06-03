<script lang="ts">
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { appointments } from '$lib/api/endpoints/appointments';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import type { Appointment } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { appointmentSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const APPT_TYPES = ['Showing', 'MoveIn', 'MoveOut', 'Inspection', 'MaintenanceVisit', 'OwnerMeeting'];
	const APPT_STATUSES = ['Scheduled', 'Confirmed', 'Completed', 'Cancelled', 'NoShow'];

	const PAGE_SIZE = 20;
	let search = $state('');
	let typeFilter = $state('');
	let skip = $state(0);
	const debouncedSearch = debounced(() => search, 300);
	$effect(() => {
		debouncedSearch.value;
		typeFilter;
		skip = 0;
	});

	const appointmentsQuery = createQuery(() => ({
		queryKey: ['appointments', portfolioId, debouncedSearch.value, skip],
		queryFn: () => appointments.list(portfolioId, { search: debouncedSearch.value, skip, take: PAGE_SIZE }),
	}));
	const propertiesQuery = createQuery(() => ({ queryKey: ['properties', portfolioId], queryFn: () => properties.list(portfolioId, { take: 200 }) }));
	const tenantsQuery = createQuery(() => ({ queryKey: ['tenants', portfolioId], queryFn: () => tenants.list(portfolioId, { take: 200 }) }));

	const empty = {
		title: '', type: 'Showing', scheduledStart: '', scheduledEnd: '',
		propertyId: '', tenantId: '', prospectName: '', prospectEmail: '', assignedTo: '', status: 'Scheduled',
	};
	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let deleteTarget = $state<Appointment | null>(null);

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['appointments', portfolioId] });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null ? appointments.create(data) : appointments.update(id, data),
		onSuccess: (_r, vars) => {
			showSuccess(vars.id == null ? 'Appointment created.' : 'Appointment updated.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: ({ id, status }: { id: number; status: string }) => appointments.update(id, { status }),
		onSuccess: () => {
			showSuccess('Appointment updated.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: (id: number) => appointments.delete(id),
		onSuccess: () => {
			showSuccess('Appointment deleted.');
			deleteTarget = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreate() {
		editingId = null;
		form = { ...empty };
		formErrors = {};
		showForm = true;
	}
	function openEdit(a: Appointment) {
		editingId = a.id;
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
		editingId = null;
		formErrors = {};
	}
	function submit() {
		const result = parseForm(appointmentSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ id: editingId, data: { portfolioId, ...result.data } });
	}

	const list = $derived((appointmentsQuery.data ?? []).filter((a) => !typeFilter || a.type === typeFilter));
	const inputClass = 'h-10 rounded border border-border bg-background px-3 py-2 text-sm';
</script>

<svelte:head>
	<title>Appointments - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="appointments-page">
	<div class="mb-4 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Appointments</h1>
			<p class="text-sm text-muted-foreground">Showings, move-ins, inspections, and service visits.</p>
		</div>
		<button data-testid="appointment-create-button" class="inline-flex items-center gap-2 rounded-md bg-primary px-3 py-2 text-sm text-white" onclick={openCreate}>
			<Plus class="h-4 w-4" />
			New Appointment
		</button>
	</div>

	<div class="mb-4 flex flex-wrap items-center gap-3">
		<div class="max-w-sm flex-1"><SearchInput bind:value={search} placeholder="Search appointments…" testid="appointment-search" /></div>
		<select data-testid="appointment-type-filter" bind:value={typeFilter} class="{inputClass} h-9">
			<option value="">All types</option>
			{#each APPT_TYPES as t}<option value={t}>{t}</option>{/each}
		</select>
	</div>

	<div class="grid gap-3" data-testid="appointments-list">
		{#if appointmentsQuery.isLoading}
			<div class="rounded-lg border border-border bg-card p-6 text-center text-muted-foreground" data-testid="appointments-loading">Loading…</div>
		{:else if list.length === 0}
			<div class="rounded-lg border border-border bg-card p-6 text-center text-muted-foreground" data-testid="appointments-empty">No appointments found.</div>
		{:else}
			{#each list as appointment (appointment.id)}
				<div class="rounded-lg border border-border bg-card p-4" data-testid="appointment-row" data-appointment-id={appointment.id}>
					<div class="flex items-center justify-between gap-2">
						<div class="min-w-0">
							<p class="truncate font-medium" data-testid="appointment-title">{appointment.title}</p>
							<p class="text-xs text-muted-foreground">{new Date(appointment.scheduledStart).toLocaleString()} · {appointment.type}</p>
						</div>
						<div class="flex shrink-0 items-center gap-1">
							<span class="rounded border border-border bg-background px-2 py-0.5 text-xs">{appointment.status}</span>
							{#if appointment.status !== 'Completed'}
								<button data-testid="appointment-complete" class="rounded border border-border px-2 py-1 text-xs" onclick={() => statusMutation.mutate({ id: appointment.id, status: 'Completed' })}>Complete</button>
							{/if}
							<a href={`/appointments/${appointment.id}`} data-testid="appointment-details" class="rounded border border-border px-2 py-1 text-xs text-primary hover:bg-secondary">Details</a>
							<button data-testid="appointment-edit" aria-label="Edit appointment" class="rounded p-1.5 text-muted-foreground hover:bg-secondary hover:text-foreground" onclick={() => goto(`/appointments/${appointment.id}`)}><Pencil class="h-4 w-4" /></button>
							<button data-testid="appointment-delete" aria-label="Delete appointment" class="rounded p-1.5 text-muted-foreground hover:bg-secondary hover:text-destructive" onclick={() => (deleteTarget = appointment)}><Trash2 class="h-4 w-4" /></button>
						</div>
					</div>
					<p class="mt-1 text-xs text-muted-foreground">{appointment.propertyName || 'No property'} · {appointment.tenantName || appointment.prospectName || 'No contact'}</p>
				</div>
			{/each}
		{/if}
	</div>

	<div class="mt-4">
		<Pagination bind:skip take={PAGE_SIZE} count={appointmentsQuery.data?.length ?? 0} testid="appointment-pagination" />
	</div>
</div>

<Dialog.Root
	open={showForm}
	onOpenChange={(v) => { if (!v) closeForm(); }}
>
	<Dialog.Content class="max-w-2xl">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'New Appointment' : 'Edit Appointment'}</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3 md:grid-cols-3" data-testid="appointment-form">
			<div class="md:col-span-2">
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="appointment-title-input">Appointment title</label>
				<input id="appointment-title-input" data-testid="appointment-title-input" bind:value={form.title} class="{inputClass} w-full" placeholder="Appointment title" />
				{#if formErrors.title}<p class="mt-1 text-xs text-destructive" data-testid="appointment-title-error">{formErrors.title}</p>{/if}
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="appointment-type-input">Type</label>
				<select id="appointment-type-input" data-testid="appointment-type-input" bind:value={form.type} class="{inputClass} w-full">{#each APPT_TYPES as t}<option value={t}>{t}</option>{/each}</select>
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="appointment-start-input">Start date and time</label>
				<input id="appointment-start-input" data-testid="appointment-start-input" type="datetime-local" bind:value={form.scheduledStart} class="{inputClass} w-full" />
				{#if formErrors.scheduledStart}<p class="mt-1 text-xs text-destructive" data-testid="appointment-start-error">{formErrors.scheduledStart}</p>{/if}
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="appointment-end-input">End date and time</label>
				<input id="appointment-end-input" data-testid="appointment-end-input" type="datetime-local" bind:value={form.scheduledEnd} class="{inputClass} w-full" />
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="appointment-status-input">Status</label>
				<select id="appointment-status-input" data-testid="appointment-status-input" bind:value={form.status} class="{inputClass} w-full">{#each APPT_STATUSES as s}<option value={s}>{s}</option>{/each}</select>
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="appointment-property-input">Property</label>
				<select id="appointment-property-input" data-testid="appointment-property-input" bind:value={form.propertyId} class="{inputClass} w-full"><option value="">No property</option>{#each propertiesQuery.data || [] as property}<option value={property.id}>{property.name}</option>{/each}</select>
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="appointment-tenant-input">Tenant</label>
				<select id="appointment-tenant-input" data-testid="appointment-tenant-input" bind:value={form.tenantId} class="{inputClass} w-full"><option value="">No tenant</option>{#each tenantsQuery.data || [] as tenant}<option value={tenant.id}>{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}</option>{/each}</select>
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="appointment-assigned-input">Assigned to</label>
				<input id="appointment-assigned-input" data-testid="appointment-assigned-input" bind:value={form.assignedTo} class="{inputClass} w-full" placeholder="Assigned to" />
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="appointment-prospect-name-input">Prospect name</label>
				<input id="appointment-prospect-name-input" data-testid="appointment-prospect-name-input" bind:value={form.prospectName} class="{inputClass} w-full" placeholder="Prospect name" />
			</div>
			<div>
				<label class="mb-1 block text-xs font-medium text-muted-foreground" for="appointment-prospect-email-input">Prospect email</label>
				<input id="appointment-prospect-email-input" data-testid="appointment-prospect-email-input" bind:value={form.prospectEmail} class="{inputClass} w-full" placeholder="Prospect email" />
				{#if formErrors.prospectEmail}<p class="mt-1 text-xs text-destructive" data-testid="appointment-prospect-email-error">{formErrors.prospectEmail}</p>{/if}
			</div>
		</div>
		<Dialog.Footer>
			<button data-testid="appointment-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground hover:bg-secondary" onclick={closeForm}>Cancel</button>
			<button data-testid="appointment-form-save" onclick={submit} class="rounded bg-primary px-3 py-2 text-sm text-white" disabled={saveMutation.isPending}>{saveMutation.isPending ? 'Saving…' : 'Save Appointment'}</button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete appointment"
	message={deleteTarget ? `Delete “${deleteTarget.title}”?` : ''}
	busy={deleteMutation.isPending}
	testid="appointment-delete"
	onconfirm={() => deleteTarget && deleteMutation.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
