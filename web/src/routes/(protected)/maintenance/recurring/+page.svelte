<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		recurringMaintenance,
		type RecurringMaintenanceTask,
	} from '$lib/api/endpoints/recurring-maintenance';
	import { properties } from '$lib/api/endpoints/properties';
	import { units } from '$lib/api/endpoints/units';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { recurringMaintenanceSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import { ExternalLink, Plus, Pencil, Trash2 } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { formatDateOnly } from '$lib/utils/date';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';

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

	const propertyName = (id: number) =>
		tasksQuery.data?.items.find((t) => t.propertyId === id)?.propertyName ??
		`Property #${id}`;

	function formatNextDue(value: string): string {
		return formatDateOnly(value) || '—';
	}

	function formatTime(value: string | null | undefined): string {
		if (!value) return 'No time set';
		const [hourRaw, minuteRaw] = value.split(':');
		const hour = Number(hourRaw);
		const minute = Number(minuteRaw);
		if (!Number.isFinite(hour) || !Number.isFinite(minute)) return value;
		const suffix = hour >= 12 ? 'PM' : 'AM';
		const displayHour = hour % 12 || 12;
		return `${displayHour}:${minute.toString().padStart(2, '0')} ${suffix}`;
	}

	function formatCurrency(value: number | null | undefined): string {
		if (value == null) return '—';
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value);
	}

	const tasks = $derived(tasksQuery.data?.items ?? []);
	const tasksTotalCount = $derived(tasksQuery.data?.totalCount ?? 0);
	const taskRangeStart = $derived(tasksTotalCount === 0 ? 0 : (gridPage - 1) * PAGE_SIZE + 1);
	const taskRangeEnd = $derived(Math.min(gridPage * PAGE_SIZE, tasksTotalCount));

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
		scheduledTime: '',
		estimatedCost: '',
		priority: 'Normal',
		isActive: true,
	};
	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...emptyForm });
	let selectedPropertyLabel = $state('');
	let selectedUnitLabel = $state('');
	let selectedVendorLabel = $state('');
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

	async function loadPropertyOptions(params: { search?: string; skip: number; take: number }) {
		const result = await properties.listPage(portfolioId, {
			...params,
			search: params.search ?? '',
			sort: 'name'
		});
		return {
			...result,
			items: result.items.map((property) => ({ id: property.id, label: property.name })),
		};
	}

	async function loadUnitOptions(params: { search?: string; skip: number; take: number }) {
		if (!form.propertyId) return { items: [], totalCount: 0, skip: params.skip, take: params.take };
		const result = await units.listWithHealthPage({
			...params,
			search: params.search ?? '',
			propertyId: Number(form.propertyId),
			sort: 'unitNumber',
		});
		return {
			...result,
			items: result.items.map((unit) => ({
				id: unit.id,
				label: `Unit ${unit.unitNumber}`,
			})),
		};
	}

	async function loadVendorOptions(params: { search?: string; skip: number; take: number }) {
		const result = await vendors.listPage(portfolioId, {
			...params,
			search: params.search ?? '',
			sort: 'name'
		});
		return {
			...result,
			items: result.items.map((vendor) => ({ id: vendor.id, label: vendor.name })),
		};
	}

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
		selectedPropertyLabel = '';
		selectedUnitLabel = '';
		selectedVendorLabel = '';
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
			scheduledTime: task.scheduledTime ? task.scheduledTime.slice(0, 5) : '',
			estimatedCost: task.estimatedCost == null ? '' : String(task.estimatedCost),
			priority: task.priority,
			isActive: task.isActive,
		};
		selectedPropertyLabel = task.propertyName ?? `Property #${task.propertyId}`;
		selectedUnitLabel = task.unitNumber ? `Unit ${task.unitNumber}` : '';
		selectedVendorLabel = task.vendorName ?? '';
		errors = {};
		showForm = true;
	}

	function closeForm() {
		showForm = false;
		editingId = null;
		selectedPropertyLabel = '';
		selectedUnitLabel = '';
		selectedVendorLabel = '';
		errors = {};
	}

	// Reset the unit selection when the property changes (units are property-scoped).
	function onPropertyChange(value: string) {
		if (value !== form.propertyId) {
			form.unitId = '';
			selectedUnitLabel = '';
		}
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
			scheduledTime: d.scheduledTime ? `${d.scheduledTime}:00` : null,
			estimatedCost: d.estimatedCost,
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
			title: 'Context',
			sortable: true,
			mobileRole: 'subtitle',
			cell: contextCell,
		},
		{ key: 'recurrenceInterval', title: 'Schedule', sortable: true, mobileRole: 'meta', cell: scheduleCell },
		{ key: 'nextDueDate', title: 'Next', sortable: true, mobileRole: 'meta', cell: nextDueCell },
		{ key: 'budget', title: 'Budget', align: 'right', mobileRole: 'meta', cell: budgetCell },
		{ key: 'generated', title: 'Work orders', align: 'right', mobileRole: 'meta', cell: generatedCell },
		{ key: 'priority', title: 'Priority', sortable: true, mobileRole: 'badge', cell: priorityCell },
		{ key: 'isActive', title: 'Active', sortable: true, align: 'center', mobileRole: 'badge', cell: activeCell },
		{ key: 'actions', title: '', align: 'right', mobileRole: 'hidden', cell: actionsCell },
	];
</script>

{#snippet titleCell(t: RecurringMaintenanceTask)}
	<span class="font-medium" data-testid="recurring-task-title">{t.title}</span>
{/snippet}

{#snippet contextCell(t: RecurringMaintenanceTask)}
	<div class="min-w-0 text-sm" data-testid="recurring-task-context-{t.id}">
		<p class="truncate font-medium">{t.propertyName ?? propertyName(t.propertyId)}</p>
		<p class="truncate text-xs text-muted-foreground">
			{t.unitNumber ? `Unit ${t.unitNumber}` : 'Whole property'}{t.vendorName ? ` · ${t.vendorName}` : ''}
		</p>
	</div>
{/snippet}

{#snippet scheduleCell(t: RecurringMaintenanceTask)}
	<div class="text-sm" data-testid="recurring-task-schedule-{t.id}">
		<p>{INTERVAL_PHRASE[t.recurrenceInterval] ?? t.recurrenceInterval}</p>
		<p class="text-xs text-muted-foreground">{formatTime(t.scheduledTime)}</p>
	</div>
{/snippet}

{#snippet nextDueCell(t: RecurringMaintenanceTask)}
	<span class="text-sm">Next: {formatNextDue(t.nextDueDate)}</span>
{/snippet}

{#snippet budgetCell(t: RecurringMaintenanceTask)}
	<div class="text-right text-sm" data-testid="recurring-task-budget-{t.id}">
		<p class="font-mono tabular-nums">{formatCurrency(t.estimatedCost)}</p>
		<p class="font-mono text-xs tabular-nums text-muted-foreground">{formatCurrency(t.monthlyEstimatedCost)}/mo</p>
	</div>
{/snippet}

{#snippet generatedCell(t: RecurringMaintenanceTask)}
	<div class="flex justify-end" data-testid="recurring-task-generated-{t.id}">
		{#if t.lastGeneratedWorkOrderId}
			<a
				href="/maintenance/{t.lastGeneratedWorkOrderId}"
				onclick={(e) => e.stopPropagation()}
				class="inline-flex items-center gap-1 rounded-md px-2 py-1 text-sm text-primary underline-offset-4 hover:underline"
				data-testid="recurring-task-last-work-order-{t.id}"
			>
				{t.generatedWorkOrderCount} linked
				<ExternalLink class="h-3.5 w-3.5" />
			</a>
		{:else}
			<span class="text-sm text-muted-foreground">0 linked</span>
		{/if}
	</div>
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

{#snippet headerActions()}
	<Button data-testid="recurring-task-create-button" onclick={openCreate}><Plus class="h-4 w-4" /> New recurring task</Button>
{/snippet}

<svelte:head>
	<title>Recurring Maintenance - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="recurring-maintenance-page">
	<PageBreadcrumb crumbs={[{ label: 'Work Orders', href: '/maintenance' }, { label: 'Recurring' }]} />

	<PageHeader
		class="mt-4 mb-6"
		band
		art={10}
		tone="coral"
		eyebrow="Work"
		title="Recurring Maintenance"
		description='Set up work that repeats on a schedule, like "Change the HVAC filter every quarter." Rental Command creates each work order when it is due.'
		actions={headerActions}
		data-testid="recurring-maintenance-header"
	/>

	<p class="mb-3 text-sm text-muted-foreground" data-testid="recurring-task-list-range">
		{tasksTotalCount === 0
			? 'Showing 0 of 0 recurring tasks'
			: `Showing ${taskRangeStart}–${taskRangeEnd} of ${tasksTotalCount} recurring task${tasksTotalCount === 1 ? '' : 's'}`}
	</p>

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
				<RemoteRecordSelect
					queryKey={['recurring-task-properties', portfolioId]}
					label="Property"
					bind:value={form.propertyId}
					selectedLabel={selectedPropertyLabel}
					placeholder="Select property"
					searchPlaceholder="Search properties…"
					disabled={editingId != null}
					required
					testid="recurring-task-property-input"
					loadPage={loadPropertyOptions}
					onValueChange={(value, option) => {
						onPropertyChange(value);
						selectedPropertyLabel = option?.label ?? '';
					}}
				/>
				{#if editingId != null}<p class="mt-1 text-xs text-muted-foreground">The property can't be changed after creation.</p>{/if}
				{#if errors.propertyId}<p class="mt-1 text-xs text-destructive" data-testid="recurring-task-property-error">{errors.propertyId}</p>{/if}
			</div>

			<!-- Unit (optional, scoped to property) -->
			<div>
				<RemoteRecordSelect
					queryKey={['recurring-task-units', portfolioId, form.propertyId]}
					label="Unit (optional)"
					bind:value={form.unitId}
					selectedLabel={selectedUnitLabel}
					placeholder={form.propertyId ? 'Whole property' : 'Pick a property first'}
					searchPlaceholder="Search units…"
					clearLabel="Whole property"
					disabled={!form.propertyId}
					testid="recurring-task-unit-input"
					loadPage={loadUnitOptions}
					onValueChange={(_value, option) => (selectedUnitLabel = option?.label ?? '')}
				/>
			</div>

			<!-- Vendor (optional) -->
			<div>
				<RemoteRecordSelect
					queryKey={['recurring-task-vendors', portfolioId]}
					label="Vendor (optional)"
					bind:value={form.vendorId}
					selectedLabel={selectedVendorLabel}
					placeholder="No vendor"
					searchPlaceholder="Search vendors…"
					clearLabel="No vendor"
					testid="recurring-task-vendor-input"
					loadPage={loadVendorOptions}
					onValueChange={(_value, option) => (selectedVendorLabel = option?.label ?? '')}
				/>
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

			<!-- Time + budget -->
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label class="mb-1 block text-sm font-medium" for="rt-time">Scheduled time <span class="text-muted-foreground">(optional)</span></label>
					<Input id="rt-time" data-testid="recurring-task-scheduled-time-input" type="time" bind:value={form.scheduledTime} />
				</div>
				<div>
					<label class="mb-1 block text-sm font-medium" for="rt-estimated-cost">Expected cost <span class="text-muted-foreground">(optional)</span></label>
					<Input id="rt-estimated-cost" data-testid="recurring-task-estimated-cost-input" type="text" inputmode="decimal" mask="currency" bind:value={form.estimatedCost} placeholder="0.00" />
					{#if errors.estimatedCost}<p class="mt-1 text-xs text-destructive" data-testid="recurring-task-estimated-cost-error">{errors.estimatedCost}</p>{/if}
				</div>
			</div>

			{#if editingId != null}
				<div class="rounded-md border border-border bg-muted/30 px-3 py-2 text-sm" data-testid="recurring-task-linked-summary">
					{#if (tasks.find((t) => t.id === editingId)?.generatedWorkOrderCount ?? 0) > 0}
						{tasks.find((t) => t.id === editingId)?.generatedWorkOrderCount} generated work order{(tasks.find((t) => t.id === editingId)?.generatedWorkOrderCount ?? 0) === 1 ? '' : 's'} linked to this schedule.
					{:else}
						No work orders have been generated from this schedule yet.
					{/if}
				</div>
			{/if}

			<!-- Priority + category -->
			<div class="grid grid-cols-2 gap-2">
				<div>
					<span class="mb-1 block text-sm font-medium">Priority</span>
					<Select.Root type="single" bind:value={form.priority}>
						<Select.Trigger class="w-full" data-testid="recurring-task-priority-input">
							{form.priority ? formatStatusLabel(form.priority) : 'Priority'}
						</Select.Trigger>
						<Select.Content>
							{#each PRIORITIES as p}
								<Select.Item value={p} label={formatStatusLabel(p)}>{formatStatusLabel(p)}</Select.Item>
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
