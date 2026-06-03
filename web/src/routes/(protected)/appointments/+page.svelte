<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
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
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const APPT_TYPES = ['Showing', 'MoveIn', 'MoveOut', 'Inspection', 'MaintenanceVisit', 'OwnerMeeting'];
	const APPT_STATUSES = ['Scheduled', 'Confirmed', 'Completed', 'Cancelled', 'NoShow'];

	const PAGE_SIZE = 20;
	let search = $state('');
	let typeFilter = $state('');
	let statusFilter = $state('');
	let skip = $state(0);
	const debouncedSearch = debounced(() => search, 300);
	$effect(() => {
		debouncedSearch.value;
		typeFilter;
		statusFilter;
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

	const list = $derived((appointmentsQuery.data ?? []).filter((a) =>
		(!typeFilter || a.type === typeFilter) && (!statusFilter || a.status === statusFilter)
	));

	// Default sort by scheduledStart ascending
	let gridPage = $state(1);

	const columns: ColumnDef<Appointment>[] = [
		{ key: 'title', title: 'Title', sortable: true, mobileRole: 'title' },
		{ key: 'type', title: 'Type', mobileRole: 'meta' },
		{ key: 'scheduledStart', title: 'When', format: 'datetime', sortable: true, mobileRole: 'subtitle' },
		{ key: 'propertyName', title: 'Property', mobileRole: 'meta' },
		{ key: 'tenantName', title: 'Tenant', mobileRole: 'meta' },
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCellSnippet,
		},
	];
</script>

{#snippet statusCellSnippet(appt: Appointment)}
	<StatusBadge status={appt.status} />
{/snippet}

<svelte:head>
	<title>Appointments - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="appointments-page">
	<div class="mb-4 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Appointments</h1>
			<p class="text-sm text-muted-foreground">Showings, move-ins, inspections, and service visits.</p>
		</div>
	</div>

	<DataGrid
		data={list}
		{columns}
		loading={appointmentsQuery.isLoading}
		emptyMessage="No appointments found."
		getRowKey={(a) => a.id}
		onRowClick={(a) => goto('/appointments/' + a.id)}
		bind:page={gridPage}
		pageSize={PAGE_SIZE}
		data-testid="appointments-list"
	>
		{#snippet toolbar()}
			<div class="max-w-sm flex-1">
				<SearchInput bind:value={search} placeholder="Search appointments…" testid="appointment-search" />
			</div>
			<Select.Root type="single" bind:value={typeFilter}>
				<Select.Trigger class="w-full max-w-[180px]" data-testid="appointment-type-filter">
					{typeFilter ? typeFilter : 'All types'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="All types">All types</Select.Item>
					{#each APPT_TYPES as t}<Select.Item value={t} label={t}>{t}</Select.Item>{/each}
				</Select.Content>
			</Select.Root>
			<Select.Root type="single" bind:value={statusFilter}>
				<Select.Trigger class="w-full max-w-[180px]" data-testid="appointment-status-filter">
					{statusFilter ? statusFilter : 'All statuses'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="All statuses">All statuses</Select.Item>
					{#each APPT_STATUSES as s}<Select.Item value={s} label={s}>{s}</Select.Item>{/each}
				</Select.Content>
			</Select.Root>
			<Button data-testid="appointment-create-button" class="ml-auto gap-2" onclick={openCreate}>
				<Plus class="h-4 w-4" />
				New Appointment
			</Button>
		{/snippet}
	</DataGrid>
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
	open={deleteTarget !== null}
	title="Delete appointment"
	message={deleteTarget ? `Delete "${deleteTarget.title}"?` : ''}
	busy={deleteMutation.isPending}
	testid="appointment-delete"
	onconfirm={() => deleteTarget && deleteMutation.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
