<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		recurringMaintenance,
		type RecurringMaintenanceTask,
	} from '$lib/api/endpoints/recurring-maintenance';
	import { properties } from '$lib/api/endpoints/properties';
	import { vendors } from '$lib/api/endpoints/vendors';
	import type { Unit } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { recurringMaintenanceSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import { Plus, RefreshCw, Pencil, Trash2 } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { formatDateOnly } from '$lib/utils/date';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;

	const INTERVALS = ['Weekly', 'Monthly', 'Quarterly', 'SemiAnnually', 'Annually'] as const;
	const PRIORITIES = ['Low', 'Normal', 'High', 'Emergency'] as const;

	// Plain-language phrasing for the cadence ("Runs every Quarter").
	const INTERVAL_PHRASE: Record<string, string> = {
		Weekly: 'Runs every Week',
		Monthly: 'Runs every Month',
		Quarterly: 'Runs every Quarter',
		SemiAnnually: 'Runs every 6 Months',
		Annually: 'Runs every Year',
	};

	// Search + the active-only toggle persisted in the URL so they survive navigating away and back.
	let search = $state(readGridParam(page.url.searchParams, 'q'));
	let activeOnly = $state(page.url.searchParams.get('active') === '1');
	let gridSort = $state(readGridParam(page.url.searchParams, 'sort'));
	let gridPage = $state(readGridParam(page.url.searchParams, 'page', 1));
	const debouncedSearch = debounced(() => search, 300);

	let filterResetPrimed = false;
	$effect(() => {
		search;
		activeOnly;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl({ q: search, active: activeOnly ? '1' : '', sort: gridSort, page: gridPage }, { page: 1 });
	});

	const tasksQuery = createQuery(() => ({
		queryKey: ['recurring-maintenance', portfolioId, 'page', debouncedSearch.value, activeOnly, gridSort, gridPage, PAGE_SIZE],
		queryFn: () =>
			recurringMaintenance.listPage({
				search: debouncedSearch.value,
				activeOnly: activeOnly || undefined,
				sort: gridSort || undefined,
				skip: (gridPage - 1) * PAGE_SIZE,
				take: PAGE_SIZE,
			}),
		enabled: portfolioId > 0,
	}));

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
		enabled: portfolioId > 0,
	}));

	const vendorsQuery = createQuery(() => ({
		queryKey: ['vendors', portfolioId],
		queryFn: () => vendors.list(portfolioId, { take: 200 }),
		enabled: portfolioId > 0,
	}));

	const propertyName = (id: number) =>
		tasksQuery.data?.items.find((t) => t.propertyId === id)?.propertyName ??
		(propertiesQuery.data ?? []).find((p) => p.id === id)?.name ??
		`Property #${id}`;

	function formatNextDue(value: string): string {
		return formatDateOnly(value) || '—';
	}

	const tasks = $derived(tasksQuery.data?.items ?? []);
	const tasksTotalCount = $derived(tasksQuery.data?.totalCount ?? 0);

	// ── Form / dialog ──────────────────────────────────────────────────────────
	const emptyForm = {
		propertyId: '',
		title: '',
		description: '',
		category: '',
		unitId: '',
		vendorId: '',
		recurrenceInterval: 'Monthly',
		nextDueDate: '',
		priority: 'Normal',
		isActive: true,
	};
	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...emptyForm });
	let errors = $state<Record<string, string>>({});
	let deleteTarget = $state<RecurringMaintenanceTask | null>(null);

	function clearRecurringError(field: string) {
		const next = clearFieldError(errors, field);
		if (next !== errors) errors = next;
	}

	$effect(() => {
		if (form.propertyId) clearRecurringError('propertyId');
	});
	$effect(() => {
		if (form.title.trim()) clearRecurringError('title');
	});
	$effect(() => {
		if (form.nextDueDate) clearRecurringError('nextDueDate');
	});
	$effect(() => {
		if (form.category.trim()) clearRecurringError('category');
	});

	// Units for the selected property (optional picker, scoped to property).
	const unitsQuery = createQuery(() => ({
		queryKey: ['units', Number(form.propertyId)],
		queryFn: () => properties.listUnits(Number(form.propertyId)),
		enabled: !!form.propertyId,
	}));
	const units = $derived<Unit[]>(unitsQuery.data ?? []);

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['recurring-maintenance', portfolioId] });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null
				? recurringMaintenance.create(data as never)
				: recurringMaintenance.update(id, data as never),
		onSuccess: (_r, vars) => {
			showSuccess(vars.id == null ? 'Recurring task created.' : 'Recurring task updated.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const setActiveMutation = createMutation(() => ({
		mutationFn: ({ id, isActive }: { id: number; isActive: boolean }) =>
			recurringMaintenance.setActive(id, isActive),
		onSuccess: (task) => {
			showSuccess(task.isActive ? 'Task turned on.' : 'Task paused.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: (id: number) => recurringMaintenance.remove(id),
		onSuccess: () => {
			showSuccess('Recurring task deleted.');
			deleteTarget = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreate() {
		editingId = null;
		form = { ...emptyForm };
		errors = {};
		showForm = true;
	}

	function openEdit(task: RecurringMaintenanceTask) {
		editingId = task.id;
		form = {
			propertyId: String(task.propertyId),
			title: task.title,
			description: task.description ?? '',
			category: task.category ?? '',
			unitId: task.unitId == null ? '' : String(task.unitId),
			vendorId: task.vendorId == null ? '' : String(task.vendorId),
			recurrenceInterval: task.recurrenceInterval,
			nextDueDate: task.nextDueDate ? task.nextDueDate.slice(0, 10) : '',
			priority: task.priority,
			isActive: task.isActive,
		};
		errors = {};
		showForm = true;
	}

	function closeForm() {
		showForm = false;
		editingId = null;
		errors = {};
	}

	// Reset the unit selection when the property changes (units are property-scoped).
	function onPropertyChange(value: string) {
		if (value !== form.propertyId) form.unitId = '';
		form.propertyId = value;
	}

	function submit() {
		const result = parseForm(recurringMaintenanceSchema, form);
		if (result.errors) {
			errors = result.errors;
			return;
		}
		errors = {};
		const d = result.data;
		// On create, propertyId is part of the payload; on update it is omitted
		// (the API does not allow re-pointing it).
		const base = {
			title: d.title,
			description: d.description,
			category: d.category,
			unitId: d.unitId,
			vendorId: d.vendorId,
			recurrenceInterval: d.recurrenceInterval,
			nextDueDate: d.nextDueDate,
			priority: d.priority,
			isActive: d.isActive,
		};
		const payload = editingId == null ? { propertyId: d.propertyId, ...base } : base;
		saveMutation.mutate({ id: editingId, data: payload });
	}

	const columns: ColumnDef<RecurringMaintenanceTask>[] = [
		{ key: 'title', title: 'Task', sortable: true, mobileRole: 'title', cell: titleCell },
		{
			key: 'property',
			title: 'Property',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (t) => t.propertyName ?? propertyName(t.propertyId),
		},
		{ key: 'recurrenceInterval', title: 'Schedule', sortable: true, mobileRole: 'meta', cell: scheduleCell },
		{ key: 'nextDueDate', title: 'Next', sortable: true, mobileRole: 'meta', cell: nextDueCell },
		{ key: 'priority', title: 'Priority', sortable: true, mobileRole: 'badge', cell: priorityCell },
		{ key: 'isActive', title: 'Active', sortable: true, align: 'center', mobileRole: 'badge', cell: activeCell },
		{ key: 'actions', title: '', align: 'right', mobileRole: 'hidden', cell: actionsCell },
	];
</script>

{#snippet titleCell(t: RecurringMaintenanceTask)}
	<span class="font-medium" data-testid="recurring-task-title">{t.title}</span>
{/snippet}

{#snippet scheduleCell(t: RecurringMaintenanceTask)}
	<span class="text-sm text-muted-foreground">{INTERVAL_PHRASE[t.recurrenceInterval] ?? t.recurrenceInterval}</span>
{/snippet}

{#snippet nextDueCell(t: RecurringMaintenanceTask)}
	<span class="text-sm">Next: {formatNextDue(t.nextDueDate)}</span>
{/snippet}

{#snippet priorityCell(t: RecurringMaintenanceTask)}
	<StatusBadge status={t.priority} />
{/snippet}

{#snippet activeCell(t: RecurringMaintenanceTask)}
	<!-- onclick.stopPropagation so toggling doesn't open the edit dialog via row click -->
	<button
		type="button"
		role="switch"
		aria-checked={t.isActive}
		data-testid="recurring-task-active-toggle-{t.id}"
		disabled={setActiveMutation.isPending}
		onclick={(e) => {
			e.stopPropagation();
			setActiveMutation.mutate({ id: t.id, isActive: !t.isActive });
		}}
		class="relative inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors disabled:opacity-50 {t.isActive ? 'bg-primary' : 'bg-input'}"
		aria-label={t.isActive ? `Pause recurring task ${t.title}` : `Resume recurring task ${t.title}`}
	>
		<span class="inline-block h-4 w-4 transform rounded-full bg-background shadow transition-transform {t.isActive ? 'translate-x-4' : 'translate-x-0.5'}"></span>
	</button>
{/snippet}

{#snippet actionsCell(t: RecurringMaintenanceTask)}
	<div class="flex justify-end gap-1">
		<Button
			variant="ghost"
			size="icon"
			data-testid="recurring-task-edit-{t.id}"
			aria-label={`Edit recurring task ${t.title}`}
			onclick={(e) => { e.stopPropagation(); openEdit(t); }}
		>
			<Pencil class="h-4 w-4" />
		</Button>
		<Button
			variant="ghost"
			size="icon"
			data-testid="recurring-task-delete-{t.id}"
			aria-label={`Delete recurring task ${t.title}`}
			onclick={(e) => { e.stopPropagation(); deleteTarget = t; }}
		>
			<Trash2 class="h-4 w-4 text-destructive" />
		</Button>
	</div>
{/snippet}

<svelte:head>
	<title>Recurring Maintenance - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="recurring-maintenance-page">
	<PageBreadcrumb crumbs={[{ label: 'Work Orders', href: '/maintenance' }, { label: 'Recurring' }]} />

	<div class="mt-4 mb-6 flex items-center justify-between gap-3">
		<div>
			<h1 class="flex items-center gap-2 text-2xl font-bold"><RefreshCw class="h-6 w-6" /> Recurring Maintenance</h1>
			<p class="text-sm text-muted-foreground">
				Set up chores that repeat on a schedule (like "HVAC filter every Quarter"). We create the work order for you each time it comes due.
			</p>
		</div>
		<Button data-testid="recurring-task-create-button" onclick={openCreate}><Plus class="h-4 w-4" /> New recurring task</Button>
	</div>

	<DataGrid
		data={tasks}
		{columns}
		loading={tasksQuery.isLoading}
		emptyMessage="No recurring tasks yet. Add one to have work orders created on a schedule."
		onRowClick={(t) => openEdit(t)}
		getRowKey={(t) => t.id}
		data-testid="recurring-tasks-list"
		pageSize={PAGE_SIZE}
		page={gridPage}
		totalCount={tasksTotalCount}
		serverSide
		onPageChange={(page) => (gridPage = page)}
		sort={gridSort}
		onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
	>
		{#snippet toolbar()}
			<div class="flex flex-1 items-center gap-2 min-w-0">
				<SearchInput bind:value={search} placeholder="Search recurring tasks…" testid="recurring-task-search" />
			</div>
			<label class="flex shrink-0 items-center gap-2 text-sm" data-testid="recurring-task-active-filter">
				<Checkbox bind:checked={activeOnly} />
				Active only
			</label>
		{/snippet}
	</DataGrid>
</div>

<!-- Create / edit dialog -->
<Dialog.Root open={showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'New Recurring Task' : 'Edit Recurring Task'}</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-3" data-testid="recurring-task-form">
			<!-- Title -->
			<div>
				<label class="mb-1 block text-sm font-medium" for="rt-title">What needs doing?</label>
				<Input id="rt-title" data-testid="recurring-task-title-input" bind:value={form.title} placeholder="e.g. Change HVAC filter" />
				{#if errors.title}<p class="mt-1 text-xs text-destructive" data-testid="recurring-task-title-error">{errors.title}</p>{/if}
			</div>

			<!-- Property (required) -->
			<div>
				<span class="mb-1 block text-sm font-medium">Property</span>
				<Select.Root type="single" value={form.propertyId} onValueChange={onPropertyChange}>
					<Select.Trigger class="w-full" data-testid="recurring-task-property-input" disabled={editingId != null}>
						{form.propertyId
							? ((propertiesQuery.data ?? []).find((p) => String(p.id) === form.propertyId)?.name ?? 'Select property')
							: 'Select property'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="Select property">Select property</Select.Item>
						{#each propertiesQuery.data ?? [] as property}
							<Select.Item value={String(property.id)} label={property.name}>{property.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				{#if editingId != null}<p class="mt-1 text-xs text-muted-foreground">The property can't be changed after creation.</p>{/if}
				{#if errors.propertyId}<p class="mt-1 text-xs text-destructive" data-testid="recurring-task-property-error">{errors.propertyId}</p>{/if}
			</div>

			<!-- Unit (optional, scoped to property) -->
			<div>
				<span class="mb-1 block text-sm font-medium">Unit <span class="text-muted-foreground">(optional)</span></span>
				<Select.Root type="single" bind:value={form.unitId}>
					<Select.Trigger class="w-full" data-testid="recurring-task-unit-input" disabled={!form.propertyId}>
						{form.unitId
							? ('Unit ' + (units.find((u) => String(u.id) === form.unitId)?.unitNumber ?? form.unitId))
							: (form.propertyId ? 'Whole property' : 'Pick a property first')}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="Whole property">Whole property</Select.Item>
						{#each units as unit}
							<Select.Item value={String(unit.id)} label={'Unit ' + unit.unitNumber}>Unit {unit.unitNumber}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>

			<!-- Vendor (optional) -->
			<div>
				<span class="mb-1 block text-sm font-medium">Vendor <span class="text-muted-foreground">(optional)</span></span>
				<Select.Root type="single" bind:value={form.vendorId}>
					<Select.Trigger class="w-full" data-testid="recurring-task-vendor-input">
						{form.vendorId
							? ((vendorsQuery.data ?? []).find((v) => String(v.id) === form.vendorId)?.name ?? 'Select vendor')
							: 'No vendor'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="No vendor">No vendor</Select.Item>
						{#each vendorsQuery.data ?? [] as vendor}
							<Select.Item value={String(vendor.id)} label={vendor.name}>{vendor.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>

			<!-- Schedule + next due -->
			<div class="grid grid-cols-2 gap-2">
				<div>
					<span class="mb-1 block text-sm font-medium">How often?</span>
					<Select.Root type="single" bind:value={form.recurrenceInterval}>
						<Select.Trigger class="w-full" data-testid="recurring-task-interval-input">
							{INTERVAL_PHRASE[form.recurrenceInterval] ?? form.recurrenceInterval}
						</Select.Trigger>
						<Select.Content>
							{#each INTERVALS as interval}
								<Select.Item value={interval} label={INTERVAL_PHRASE[interval]}>{INTERVAL_PHRASE[interval]}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div>
					<label class="mb-1 block text-sm font-medium" for="rt-next">Next due</label>
					<DatePicker id="rt-next" testid="recurring-task-next-due-input" bind:value={form.nextDueDate} placeholder="Next due date" />
					{#if errors.nextDueDate}<p class="mt-1 text-xs text-destructive" data-testid="recurring-task-next-due-error">{errors.nextDueDate}</p>{/if}
				</div>
			</div>

			<!-- Priority + category -->
			<div class="grid grid-cols-2 gap-2">
				<div>
					<span class="mb-1 block text-sm font-medium">Priority</span>
					<Select.Root type="single" bind:value={form.priority}>
						<Select.Trigger class="w-full" data-testid="recurring-task-priority-input">
							{form.priority || 'Priority'}
						</Select.Trigger>
						<Select.Content>
							{#each PRIORITIES as p}
								<Select.Item value={p} label={p}>{p}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div>
					<label class="mb-1 block text-sm font-medium" for="rt-category">Category <span class="text-muted-foreground">(optional)</span></label>
					<Input id="rt-category" data-testid="recurring-task-category-input" bind:value={form.category} placeholder="e.g. HVAC" />
				</div>
			</div>

			<!-- Description -->
			<div>
				<label class="mb-1 block text-sm font-medium" for="rt-desc">Notes <span class="text-muted-foreground">(optional)</span></label>
				<textarea
					id="rt-desc"
					data-testid="recurring-task-description-input"
					bind:value={form.description}
					rows={2}
					class="w-full rounded border border-border bg-background px-3 py-2 text-sm"
					placeholder="Anything the vendor or you should know"
				></textarea>
			</div>

			<!-- Active switch -->
			<label class="flex items-center gap-2 text-sm" data-testid="recurring-task-active-input">
				<Checkbox bind:checked={form.isActive} />
				<span>Active <span class="text-muted-foreground">— when on, we create the work order automatically each time it's due.</span></span>
			</label>
		</div>
		<Dialog.Footer>
			<Button variant="outline" data-testid="recurring-task-form-cancel" onclick={closeForm}>Cancel</Button>
			<Button data-testid="recurring-task-form-save" onclick={submit} disabled={saveMutation.isPending}>
				{saveMutation.isPending ? 'Saving…' : 'Save'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete recurring task"
	message={deleteTarget ? `Delete "${deleteTarget.title}"? We'll stop creating work orders for it.` : ''}
	busy={deleteMutation.isPending}
	testid="recurring-task-delete"
	onconfirm={() => deleteTarget && deleteMutation.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
