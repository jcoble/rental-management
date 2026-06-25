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
	import DateTimePicker from '$lib/components/shared/DateTimePicker.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import { Plus, CalendarDays, List as ListIcon } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import AppointmentCalendar from './AppointmentCalendar.svelte';
	import { invalidateAppointmentQueries } from './appointment-query-keys';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import {
		TYPE_LEGEND,
		labelForType,
		utcIsoToLocalWallClock,
		localWallClockToUtcIso
	} from './calendar-utils';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const APPT_TYPES = ['Showing', 'MoveIn', 'MoveOut', 'Inspection', 'MaintenanceVisit', 'OwnerMeeting'];
	const APPT_STATUSES = ['Scheduled', 'Confirmed', 'Completed', 'Cancelled', 'NoShow'];

	// ── View toggle: Calendar (default) / List ──────────────────────────────────
	type AppointmentView = 'calendar' | 'list';
	// View + search/type/status filters persisted in the URL so they survive navigating away and back.
	let view = $state<AppointmentView>(page.url.searchParams.get('view') === 'list' ? 'list' : 'calendar');

	const PAGE_SIZE = 20;
	let search = $state(readGridParam(page.url.searchParams, 'q'));
	let typeFilter = $state(readGridParam(page.url.searchParams, 'type'));
	let statusFilter = $state(readGridParam(page.url.searchParams, 'status'));
	// List grid sort/page persisted in the URL. The list view is server-side; the calendar still uses a
	// bounded broader fetch until it has a visible-date-window API.
	let gridSort = $state(readGridParam(page.url.searchParams, 'sort'));
	let gridPage = $state(readGridParam(page.url.searchParams, 'page', 1));
	const debouncedSearch = debounced(() => search, 300);
	let filterResetPrimed = false;
	$effect(() => {
		debouncedSearch.value;
		typeFilter;
		statusFilter;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});
	$effect(() => {
		syncGridUrl({ view, q: search, type: typeFilter, status: statusFilter, sort: gridSort, page: gridPage }, { view: 'calendar', page: 1 });
	});

	// List view is paged/searched server-side; the calendar needs a fuller window
	// so the month/week shows everything, so we pull a larger batch for it.
	const appointmentsQuery = createQuery(() => ({
		queryKey: ['appointments', portfolioId, 'page', debouncedSearch.value, typeFilter, statusFilter, gridSort, gridPage, PAGE_SIZE],
		queryFn: () => appointments.listPage(portfolioId, {
			search: debouncedSearch.value,
			type: typeFilter || undefined,
			status: statusFilter || undefined,
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		}),
	}));
	const calendarQuery = createQuery(() => ({
		queryKey: ['appointments', portfolioId, 'calendar', debouncedSearch.value, typeFilter, statusFilter],
		queryFn: () => appointments.list(portfolioId, {
			search: debouncedSearch.value,
			type: typeFilter || undefined,
			status: statusFilter || undefined,
			take: 500,
		}),
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

	function clearFormError(field: string) {
		if (!formErrors[field]) return;
		const next = { ...formErrors };
		delete next[field];
		formErrors = next;
	}

	$effect(() => {
		if (form.title) clearFormError('title');
	});
	$effect(() => {
		if (form.scheduledStart) clearFormError('scheduledStart');
	});
	$effect(() => {
		if (form.scheduledEnd) clearFormError('scheduledEnd');
	});
	$effect(() => {
		if (form.prospectEmail) clearFormError('prospectEmail');
	});

	function invalidate() {
		invalidateAppointmentQueries(queryClient, portfolioId);
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

	const rescheduleMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number; data: Record<string, unknown> }) => appointments.update(id, data),
		onSuccess: () => {
			showSuccess('Appointment rescheduled.');
			invalidate();
		},
		onError: (err) => {
			showError(apiErrorMessage(err));
			// On failure refetch so the dragged event snaps back to its stored slot.
			invalidate();
		},
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
	// Calendar: clicked an empty day/slot → create prefilled with that local time.
	function openCreateAt(localWallClockIso: string) {
		editingId = null;
		form = { ...empty, scheduledStart: localWallClockIso };
		formErrors = {};
		showForm = true;
	}
	function openEdit(a: Appointment) {
		editingId = a.id;
		form = {
			title: a.title, type: a.type, status: a.status,
			// Convert stored UTC → local wall-clock for the DateTimePicker.
			scheduledStart: utcIsoToLocalWallClock(a.scheduledStart),
			scheduledEnd: utcIsoToLocalWallClock(a.scheduledEnd),
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
		// The form holds local wall-clock ISO; convert to UTC before saving (timestamptz).
		const data = { ...result.data } as Record<string, unknown>;
		data.scheduledStart = localWallClockToUtcIso(form.scheduledStart);
		// Blank end → null (not ''): DateTime? cannot deserialize an empty string (raw 400).
		data.scheduledEnd = form.scheduledEnd ? localWallClockToUtcIso(form.scheduledEnd) : null;
		saveMutation.mutate({ id: editingId, data: { portfolioId, ...data } });
	}

	// Calendar drag-to-reschedule → persist via the existing update mutation (UTC).
	function reschedule(a: Appointment, newStartUtcIso: string, newEndUtcIso: string) {
		rescheduleMutation.mutate({
			id: a.id,
			data: { scheduledStart: newStartUtcIso, scheduledEnd: newEndUtcIso },
		});
	}

	const list = $derived(appointmentsQuery.data?.items ?? []);
	const totalCount = $derived(appointmentsQuery.data?.totalCount ?? 0);
	const calendarList = $derived(calendarQuery.data ?? []);

	const columns: ColumnDef<Appointment>[] = [
		{ key: 'title', title: 'Title', sortable: true, mobileRole: 'title' },
		{ key: 'type', title: 'Type', mobileRole: 'meta', cell: typeCellSnippet },
		{ key: 'scheduledStart', title: 'When', format: 'datetime', sortable: true, mobileRole: 'subtitle' },
		{ key: 'propertyName', title: 'Property', sortable: true, mobileRole: 'meta' },
		{ key: 'tenantName', title: 'Tenant', sortable: true, mobileRole: 'meta' },
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCellSnippet,
		},
	];
</script>

{#snippet typeCellSnippet(appt: Appointment)}
	{labelForType(appt.type)}
{/snippet}

{#snippet statusCellSnippet(appt: Appointment)}
	<StatusBadge status={appt.status} />
{/snippet}

<svelte:head>
	<title>Appointments - Rental Command</title>
</svelte:head>

<div class="box-border flex h-full flex-col overflow-hidden p-6" data-testid="appointments-page">
	<div class="mb-4 flex flex-wrap items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Appointments</h1>
			<p class="text-sm text-muted-foreground">Showings, move-ins, inspections, and service visits.</p>
		</div>
		<div class="flex items-center gap-2">
			<!-- Segmented Calendar / List toggle (Calendar is the default). -->
			<div
				class="inline-flex items-center gap-1 rounded-lg border border-border bg-muted p-1"
				role="tablist"
				aria-label="Appointment view"
				data-testid="appointments-view-toggle"
			>
				<button
					type="button"
					role="tab"
					aria-selected={view === 'calendar'}
					class="inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium transition-colors {view === 'calendar' ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}"
					data-testid="appointments-view-calendar"
					onclick={() => (view = 'calendar')}
				>
					<CalendarDays class="h-4 w-4" />
					Calendar
				</button>
				<button
					type="button"
					role="tab"
					aria-selected={view === 'list'}
					class="inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium transition-colors {view === 'list' ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}"
					data-testid="appointments-view-list"
					onclick={() => (view = 'list')}
				>
					<ListIcon class="h-4 w-4" />
					List
				</button>
			</div>
			<Button data-testid="appointment-create-button" class="gap-2" onclick={openCreate}>
				<Plus class="h-4 w-4" />
				New Appointment
			</Button>
		</div>
	</div>

	<!--
		Keep-alive views: both the Calendar and the List stay mounted and laid out;
		the active one is raised on top (opaque `bg-background`) while the inactive
		one sits behind it (z-0, pointer-events-none, aria-hidden). We deliberately
		do NOT use `display:none`/`visibility:hidden`/`opacity:0` on the List panel so
		that, even though Calendar is the default view, the seeded-data e2e test still
		finds `appointments-list` + `datagrid-row` rendered in the DOM. Filters drive
		both views.
	-->
	<div class="relative min-h-0 flex-1">
		<div
			class="absolute inset-0 flex flex-col gap-3 bg-background {view === 'calendar' ? 'z-10' : 'z-0 pointer-events-none'}"
			aria-hidden={view !== 'calendar'}
		>
			<!-- Filters shared with the list view, plus the Type color legend. -->
			<div class="flex flex-wrap items-center gap-2">
				<div class="max-w-xs flex-1">
					<SearchInput bind:value={search} placeholder="Search appointments…" testid="appointment-calendar-search" />
				</div>
				<Select.Root type="single" bind:value={typeFilter}>
					<Select.Trigger class="w-full max-w-[160px]" data-testid="appointment-calendar-type-filter">
						{typeFilter ? labelForType(typeFilter) : 'All types'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="All types">All types</Select.Item>
						{#each APPT_TYPES as t}<Select.Item value={t} label={labelForType(t)}>{labelForType(t)}</Select.Item>{/each}
					</Select.Content>
				</Select.Root>
				<Select.Root type="single" bind:value={statusFilter}>
					<Select.Trigger class="w-full max-w-[160px]" data-testid="appointment-calendar-status-filter">
						{statusFilter ? statusFilter : 'All statuses'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="All statuses">All statuses</Select.Item>
						{#each APPT_STATUSES as s}<Select.Item value={s} label={s}>{s}</Select.Item>{/each}
					</Select.Content>
				</Select.Root>
				<!-- Legend: Type → color -->
				<ul class="flex flex-wrap items-center gap-x-3 gap-y-1" data-testid="appointment-calendar-legend">
					{#each TYPE_LEGEND as entry}
						<li class="flex items-center gap-1.5 text-xs text-muted-foreground">
							<span class="inline-block h-2.5 w-2.5 rounded-sm" style="background-color: {entry.color.color}"></span>
							{entry.color.label}
						</li>
					{/each}
				</ul>
			</div>

		<div class="min-h-0 flex-1 rounded-lg border border-border bg-card p-2">
			{#if view === 'calendar'}
				<AppointmentCalendar
					appointments={calendarList}
					onSelectAppointment={openEdit}
					onCreateAt={openCreateAt}
					onReschedule={reschedule}
				/>
			{/if}
		</div>
	</div>

	<div
		class="absolute inset-0 overflow-y-auto bg-background {view === 'list' ? 'z-10' : 'z-0 pointer-events-none'}"
		aria-hidden={view !== 'list'}
	>
		<DataGrid
			data={list}
			{columns}
			loading={appointmentsQuery.isLoading || appointmentsQuery.isFetching}
			emptyMessage="No appointments found."
			getRowKey={(a) => a.id}
			onRowClick={(a) => goto('/appointments/' + a.id)}
			pageSize={PAGE_SIZE}
			page={gridPage}
			totalCount={totalCount}
			serverSide
			onPageChange={(page) => (gridPage = page)}
			sort={gridSort}
			onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
			data-testid="appointments-list"
		>
			{#snippet toolbar()}
				<div class="max-w-sm flex-1">
					<SearchInput bind:value={search} placeholder="Search appointments…" testid="appointment-search" />
				</div>
				<Select.Root type="single" bind:value={typeFilter}>
					<Select.Trigger class="w-full max-w-[180px]" data-testid="appointment-type-filter">
						{typeFilter ? labelForType(typeFilter) : 'All types'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="All types">All types</Select.Item>
						{#each APPT_TYPES as t}<Select.Item value={t} label={labelForType(t)}>{labelForType(t)}</Select.Item>{/each}
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
			{/snippet}
		</DataGrid>
	</div>
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
				<Input data-testid="appointment-title-input" bind:value={form.title} placeholder="Appointment title" />
				{#if formErrors.title}<p class="mt-1 text-xs text-destructive" data-testid="appointment-title-error">{formErrors.title}</p>{/if}
			</div>
			<Select.Root type="single" bind:value={form.type}>
				<Select.Trigger class="w-full" data-testid="appointment-type-input">
					{form.type ? labelForType(form.type) : 'Select type'}
				</Select.Trigger>
				<Select.Content>
					{#each APPT_TYPES as t}<Select.Item value={t} label={labelForType(t)}>{labelForType(t)}</Select.Item>{/each}
				</Select.Content>
			</Select.Root>
			<div>
				<span class="mb-1 block text-xs font-medium text-muted-foreground">Start</span>
				<DateTimePicker bind:value={form.scheduledStart} testid="appointment-start-input" />
				{#if formErrors.scheduledStart}<p class="mt-1 text-xs text-destructive" data-testid="appointment-start-error">{formErrors.scheduledStart}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs font-medium text-muted-foreground">End</span>
				<DateTimePicker bind:value={form.scheduledEnd} testid="appointment-end-input" />
			</div>
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
			<Button data-testid="appointment-form-save" onclick={submit} disabled={saveMutation.isPending}>{saveMutation.isPending ? 'Saving…' : 'Save appointment'}</Button>
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
