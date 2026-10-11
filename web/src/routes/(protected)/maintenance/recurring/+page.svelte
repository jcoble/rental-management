<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		recurringMaintenance,
		type RecurringMaintenanceTask,
	} from '$lib/api/endpoints/recurring-maintenance';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import RecurringMaintenanceFormDialog from '$lib/components/maintenance/RecurringMaintenanceFormDialog.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import { ExternalLink, Plus, Pencil, Trash2 } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { formatDateOnly } from '$lib/utils/date';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;

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

	const tasks = $derived(tasksQuery.data?.items ?? []);
	const tasksTotalCount = $derived(tasksQuery.data?.totalCount ?? 0);
	const taskRangeStart = $derived(tasksTotalCount === 0 ? 0 : (gridPage - 1) * PAGE_SIZE + 1);
	const taskRangeEnd = $derived(Math.min(gridPage * PAGE_SIZE, tasksTotalCount));

	let showForm = $state(false);
	let editingTask = $state<RecurringMaintenanceTask | null>(null);
	let deleteTarget = $state<RecurringMaintenanceTask | null>(null);

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['recurring-maintenance', portfolioId] });
	}

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
		editingTask = null;
		showForm = true;
	}

	function openEdit(task: RecurringMaintenanceTask) {
		editingTask = task;
		showForm = true;
	}

	function closeForm() {
		showForm = false;
		editingTask = null;
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
		{ key: 'generated', title: 'Repairs', align: 'right', mobileRole: 'meta', cell: generatedCell },
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
		<p class="font-mono tabular-nums">{formatAccountingCurrency(t.estimatedCost)}</p>
		<p class="font-mono text-xs tabular-nums text-muted-foreground">{formatAccountingCurrency(t.monthlyEstimatedCost)}/mo</p>
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
	<PageBreadcrumb crumbs={[{ label: 'Repairs', href: '/maintenance' }, { label: 'Recurring' }]} />

	<PageHeader
		class="mt-4 mb-6"
		band
		art={10}
		tone="coral"
		eyebrow="Work"
		title="Recurring Maintenance"
		description='Set up work that repeats on a schedule, like "Change the HVAC filter every quarter." Rental Command creates each repair when it is due.'
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
		emptyMessage="No recurring tasks yet. Add one to have repairs created on a schedule."
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

<!-- Create / edit dialog (shared with the unit Recurring work view). -->
<RecurringMaintenanceFormDialog
	open={showForm}
	task={editingTask}
	tasks={tasks}
	onclose={closeForm}
	onsaved={() => { closeForm(); invalidate(); }}
/>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete recurring task"
	message={deleteTarget ? `Delete "${deleteTarget.title}"? We'll stop creating repairs for it.` : ''}
	busy={deleteMutation.isPending}
	testid="recurring-task-delete"
	onconfirm={() => deleteTarget && deleteMutation.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
