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
	import { ArrowLeft, ArrowRight, Check, Plus, CalendarDays, List as ListIcon } from '@lucide/svelte';
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
	import PageHeader from '$lib/components/m3/PageHeader.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const APPT_TYPES = ['Showing', 'MoveIn', 'MoveOut', 'Inspection', 'MaintenanceVisit', 'OwnerMeeting'];
	const APPT_STATUSES = ['Scheduled', 'Confirmed', 'Completed', 'Cancelled', 'NoShow'];
	const FORM_STEPS = [
		{
			key: 'details',
			label: 'Details',
			helper: 'Name the appointment and set the type.'
		},
		{
			key: 'schedule',
			label: 'Schedule',
			helper: 'Pick the date and time window.'
		},
		{
			key: 'people',
			label: 'People',
			helper: 'Connect it to a property, tenant, or prospect.'
		},
	] as const;
	type AppointmentFormStep = typeof FORM_STEPS[number]['key'];
	const STEP_FIELDS: Record<AppointmentFormStep, string[]> = {
		details: ['title', 'type', 'status'],
		schedule: ['scheduledStart', 'scheduledEnd'],
		people: ['propertyId', 'tenantId', 'assignedTo', 'prospectName', 'prospectEmail'],
	};

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
	let formStep = $state<AppointmentFormStep>('details');
	let deleteTarget = $state<Appointment | null>(null);
	const formStepIndex = $derived(FORM_STEPS.findIndex((step) => step.key === formStep));
	const currentFormStep = $derived(FORM_STEPS[formStepIndex] ?? FORM_STEPS[0]);

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

	function firstStepWithErrors(errors: Record<string, string>): AppointmentFormStep {
		for (const step of FORM_STEPS) {
			if (STEP_FIELDS[step.key].some((field) => errors[field])) return step.key;
		}
		return 'details';
	}

	function stepHasError(step: AppointmentFormStep): boolean {
		return STEP_FIELDS[step].some((field) => Boolean(formErrors[field]));
	}

	function filterStepErrors(errors: Record<string, string>, step: AppointmentFormStep): Record<string, string> {
		const fields = new Set(STEP_FIELDS[step]);
		return Object.fromEntries(Object.entries(errors).filter(([field]) => fields.has(field)));
	}

	function replaceStepErrors(step: AppointmentFormStep, errors: Record<string, string>) {
		const fields = new Set(STEP_FIELDS[step]);
		const next = Object.fromEntries(Object.entries(formErrors).filter(([field]) => !fields.has(field)));
		formErrors = { ...next, ...errors };
	}

	function validateStep(step: AppointmentFormStep): boolean {
		const result = parseForm(appointmentSchema, form);
		const errors = filterStepErrors(result.errors ?? {}, step);
		replaceStepErrors(step, errors);
		return Object.keys(errors).length === 0;
	}

	function goToFormStep(step: AppointmentFormStep) {
		formStep = step;
	}

	function nextFormStep() {
		if (!validateStep(formStep)) return;
		const nextStep = FORM_STEPS[Math.min(formStepIndex + 1, FORM_STEPS.length - 1)];
		formStep = nextStep.key;
	}

	function previousFormStep() {
		const previousStep = FORM_STEPS[Math.max(formStepIndex - 1, 0)];
		formStep = previousStep.key;
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
		formStep = 'details';
		showForm = true;
	}
	// Calendar: clicked an empty day/slot → create prefilled with that local time.
	function openCreateAt(localWallClockIso: string) {
		editingId = null;
		form = { ...empty, scheduledStart: localWallClockIso };
		formErrors = {};
		formStep = 'details';
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
		formStep = 'details';
		showForm = true;
	}
	function closeForm() {
		showForm = false;
		editingId = null;
		formErrors = {};
		formStep = 'details';
	}
	function submit() {
		const result = parseForm(appointmentSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			formStep = firstStepWithErrors(result.errors);
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

{#snippet headerActions()}
	<div class="flex flex-wrap items-center gap-2">
		<div
			class="m3-glass inline-flex items-center gap-1 rounded-lg p-1"
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
{/snippet}

<svelte:head>
	<title>Appointments - Rental Command</title>
</svelte:head>

<div class="box-border flex h-full flex-col overflow-hidden p-6" data-testid="appointments-page">
	<PageHeader
		class="mb-4"
		band
		art={3}
		tone="sky"
		eyebrow="Work"
		title="Appointments"
		description="Showings, move-ins, inspections, and service visits."
		actions={headerActions}
		data-testid="appointments-header"
	/>

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
	<Dialog.Content class="max-w-4xl gap-0 overflow-hidden p-0">
		<Dialog.Header class="border-b border-border px-6 pb-4 pt-6">
			<Dialog.Title>{editingId == null ? 'New Appointment' : 'Edit Appointment'}</Dialog.Title>
			<p class="max-w-2xl text-sm text-muted-foreground">
				Walk through the appointment in a few pieces so the schedule, people, and details stay clear.
			</p>
		</Dialog.Header>
		<div class="border-b border-border px-6 py-4">
			<ol class="grid gap-2 sm:grid-cols-3" data-testid="appointment-form-steps">
				{#each FORM_STEPS as step, index}
					<li>
						<button
							type="button"
							class="flex min-h-16 w-full items-center gap-3 rounded-lg border px-3 py-2 text-left transition hover:border-primary/60 {formStep === step.key ? 'border-primary bg-primary/10 text-foreground' : stepHasError(step.key) ? 'border-destructive/50 bg-destructive/10 text-foreground' : index < formStepIndex ? 'border-emerald-500/50 bg-emerald-500/10 text-foreground' : 'border-border bg-muted/30 text-muted-foreground'}"
							aria-current={formStep === step.key ? 'step' : undefined}
							data-testid={`appointment-step-${step.key}`}
							onclick={() => goToFormStep(step.key)}
						>
							<span class="flex size-8 shrink-0 items-center justify-center rounded-full border text-sm font-semibold {formStep === step.key ? 'border-primary bg-primary text-primary-foreground' : index < formStepIndex ? 'border-emerald-500 bg-emerald-500 text-white' : 'border-border bg-background'}">
								{#if index < formStepIndex && !stepHasError(step.key)}
									<Check class="size-4" />
								{:else}
									{index + 1}
								{/if}
							</span>
							<span class="min-w-0">
								<span class="block text-sm font-semibold">{step.label}</span>
								<span class="block text-xs leading-snug text-muted-foreground">{step.helper}</span>
							</span>
						</button>
					</li>
				{/each}
			</ol>
		</div>

		<div class="max-h-[calc(100dvh-19rem)] overflow-y-auto px-6 py-5" data-testid="appointment-form">
			<div class="mb-5">
				<p class="text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">Step {formStepIndex + 1} of {FORM_STEPS.length}</p>
				<h3 class="mt-1 text-lg font-semibold text-foreground">{currentFormStep.label}</h3>
				<p class="text-sm text-muted-foreground">{currentFormStep.helper}</p>
			</div>

			{#if formStep === 'details'}
				<div class="grid gap-4 sm:grid-cols-2">
					<div class="sm:col-span-2">
						<label class="mb-2 block text-sm font-medium text-muted-foreground" for="appointment-title">Appointment title</label>
						<Input id="appointment-title" data-testid="appointment-title-input" bind:value={form.title} placeholder="Appointment title" />
						{#if formErrors.title}<p class="mt-1 text-xs text-destructive" data-testid="appointment-title-error">{formErrors.title}</p>{/if}
					</div>
					<div>
						<span class="mb-2 block text-sm font-medium text-muted-foreground">Type</span>
						<Select.Root type="single" bind:value={form.type}>
							<Select.Trigger class="w-full" data-testid="appointment-type-input">
								{form.type ? labelForType(form.type) : 'Select type'}
							</Select.Trigger>
							<Select.Content>
								{#each APPT_TYPES as t}<Select.Item value={t} label={labelForType(t)}>{labelForType(t)}</Select.Item>{/each}
							</Select.Content>
						</Select.Root>
					</div>
					<div>
						<span class="mb-2 block text-sm font-medium text-muted-foreground">Status</span>
						<Select.Root type="single" bind:value={form.status}>
							<Select.Trigger class="w-full" data-testid="appointment-status-input">
								{form.status ? form.status : 'Select status'}
							</Select.Trigger>
							<Select.Content>
								{#each APPT_STATUSES as s}<Select.Item value={s} label={s}>{s}</Select.Item>{/each}
							</Select.Content>
						</Select.Root>
					</div>
				</div>
			{:else if formStep === 'schedule'}
				<div class="grid gap-4 lg:grid-cols-2">
					<div>
						<span class="mb-2 block text-sm font-medium text-muted-foreground">Start</span>
						<DateTimePicker bind:value={form.scheduledStart} testid="appointment-start-input" />
						{#if formErrors.scheduledStart}<p class="mt-1 text-xs text-destructive" data-testid="appointment-start-error">{formErrors.scheduledStart}</p>{/if}
					</div>
					<div>
						<span class="mb-2 block text-sm font-medium text-muted-foreground">End</span>
						<DateTimePicker bind:value={form.scheduledEnd} testid="appointment-end-input" />
					</div>
				</div>
			{:else}
				<div class="grid gap-4 sm:grid-cols-2">
					<div>
						<span class="mb-2 block text-sm font-medium text-muted-foreground">Property</span>
						<Select.Root type="single" bind:value={form.propertyId}>
							<Select.Trigger class="w-full" data-testid="appointment-property-input">
								{form.propertyId ? (propertiesQuery.data?.find((p) => String(p.id) === form.propertyId)?.name ?? form.propertyId) : 'No property'}
							</Select.Trigger>
							<Select.Content>
								<Select.Item value="" label="No property">No property</Select.Item>
								{#each propertiesQuery.data || [] as property}<Select.Item value={String(property.id)} label={property.name}>{property.name}</Select.Item>{/each}
							</Select.Content>
						</Select.Root>
					</div>
					<div>
						<span class="mb-2 block text-sm font-medium text-muted-foreground">Tenant</span>
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
					</div>
					<div>
						<label class="mb-2 block text-sm font-medium text-muted-foreground" for="appointment-assigned">Assigned to</label>
						<Input id="appointment-assigned" data-testid="appointment-assigned-input" bind:value={form.assignedTo} placeholder="Assigned to" />
					</div>
					<div>
						<label class="mb-2 block text-sm font-medium text-muted-foreground" for="appointment-prospect-name">Prospect name</label>
						<Input id="appointment-prospect-name" data-testid="appointment-prospect-name-input" bind:value={form.prospectName} placeholder="Prospect name" />
					</div>
					<div class="sm:col-span-2">
						<label class="mb-2 block text-sm font-medium text-muted-foreground" for="appointment-prospect-email">Prospect email</label>
						<Input id="appointment-prospect-email" data-testid="appointment-prospect-email-input" bind:value={form.prospectEmail} placeholder="Prospect email" />
						{#if formErrors.prospectEmail}<p class="mt-1 text-xs text-destructive" data-testid="appointment-prospect-email-error">{formErrors.prospectEmail}</p>{/if}
					</div>
				</div>
			{/if}
		</div>

		<Dialog.Footer class="flex-row items-center justify-between gap-3 border-t border-border px-6 py-4">
			{#if formStep === 'details'}
				<Button variant="outline" class="min-w-24" data-testid="appointment-form-cancel" onclick={closeForm}>Cancel</Button>
			{:else}
				<Button variant="outline" class="min-w-24" data-testid="appointment-form-back" onclick={previousFormStep}>
					<ArrowLeft class="size-4" />
					Back
				</Button>
			{/if}
			{#if formStep === 'people'}
				<Button class="min-w-36" data-testid="appointment-form-save" onclick={submit} disabled={saveMutation.isPending}>{saveMutation.isPending ? 'Saving…' : 'Save appointment'}</Button>
			{:else}
				<Button class="min-w-28" data-testid="appointment-form-next" onclick={nextFormStep}>
					Next
					<ArrowRight class="size-4" />
				</Button>
			{/if}
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
