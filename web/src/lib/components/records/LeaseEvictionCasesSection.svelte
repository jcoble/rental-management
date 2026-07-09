<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		evictionCases,
		type CreateEvictionCaseEventRequest,
		type CreateEvictionCaseRequest,
		type EvictionCase,
		type EvictionCaseStatus,
		type EvictionEventType
	} from '$lib/api/endpoints/eviction-cases';
	import { evictionCaseEventSchema, evictionCaseSchema, parseForm } from '$lib/schemas';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { formatDateOnly } from '$lib/utils/date';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';

	let { leaseId }: { leaseId: number } = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 10;

	let page = $state(1);
	let sort = $state('-filedOnDate');
	const query = createQuery(() => ({
		queryKey: ['eviction-cases', leaseId, 'page', page, sort, PAGE_SIZE],
		queryFn: () =>
			evictionCases.listPage({
				leaseId,
				skip: (page - 1) * PAGE_SIZE,
				take: PAGE_SIZE,
				sort: sort || undefined
			}),
		enabled: leaseId > 0
	}));

	const list = $derived(query.data?.items ?? []);
	const totalCount = $derived(query.data?.totalCount ?? 0);

	const statusOptions: { value: EvictionCaseStatus; label: string }[] = [
		{ value: 'Draft', label: 'Draft' },
		{ value: 'NoticeServed', label: 'Notice served' },
		{ value: 'Filed', label: 'Filed' },
		{ value: 'HearingScheduled', label: 'Hearing scheduled' },
		{ value: 'Judgment', label: 'Judgment' },
		{ value: 'MoveOut', label: 'Move-out' },
		{ value: 'Settled', label: 'Settled' },
		{ value: 'Dismissed', label: 'Dismissed' }
	];

	const eventOptions: { value: EvictionEventType; label: string }[] = [
		{ value: 'NoticeServed', label: 'Notice served' },
		{ value: 'Filed', label: 'Filed' },
		{ value: 'HearingScheduled', label: 'Hearing scheduled' },
		{ value: 'Judgment', label: 'Judgment' },
		{ value: 'MoveOut', label: 'Move-out' },
		{ value: 'Settlement', label: 'Settlement' },
		{ value: 'Dismissal', label: 'Dismissal' },
		{ value: 'PaymentPlan', label: 'Payment plan' },
		{ value: 'Note', label: 'Note' }
	];

	const emptyForm = {
		status: 'Filed' as EvictionCaseStatus,
		filedOnDate: todayIso(),
		hearingDate: '',
		courtName: '',
		caseNumber: '',
		resolution: '',
		notes: ''
	};

	const emptyEventForm = {
		eventType: 'Filed' as EvictionEventType,
		eventDate: todayIso(),
		notes: ''
	};

	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...emptyForm });
	let formErrors = $state<Record<string, string>>({});
	let deleteTarget = $state<EvictionCase | null>(null);
	let eventTarget = $state<EvictionCase | null>(null);
	let eventForm = $state({ ...emptyEventForm });
	let eventFormErrors = $state<Record<string, string>>({});

	const detailQuery = createQuery(() => ({
		queryKey: ['eviction-case', editingId],
		queryFn: () => evictionCases.get(editingId ?? 0),
		enabled: showForm && editingId != null
	}));
	const timeline = $derived(detailQuery.data?.events ?? []);

	function openAdd() {
		editingId = null;
		form = { ...emptyForm, filedOnDate: todayIso() };
		formErrors = {};
		showForm = true;
	}

	function openEdit(item: EvictionCase) {
		editingId = item.id;
		form = {
			status: item.status,
			filedOnDate: item.filedOnDate?.slice(0, 10) ?? '',
			hearingDate: item.hearingDate?.slice(0, 10) ?? '',
			courtName: item.courtName ?? '',
			caseNumber: item.caseNumber ?? '',
			resolution: item.resolution ?? '',
			notes: item.notes ?? ''
		};
		formErrors = {};
		showForm = true;
	}

	function closeForm() {
		showForm = false;
		editingId = null;
		formErrors = {};
	}

	function openEvent(item: EvictionCase) {
		eventTarget = item;
		eventForm = { ...emptyEventForm, eventDate: todayIso() };
		eventFormErrors = {};
	}

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['eviction-cases', leaseId] });
		queryClient.invalidateQueries({ queryKey: ['eviction-case'] });
		queryClient.invalidateQueries({ queryKey: ['lease', leaseId] });
		queryClient.invalidateQueries({ queryKey: ['leases'] });
		queryClient.invalidateQueries({ queryKey: ['units'] });
		queryClient.invalidateQueries({ queryKey: ['dashboard'] });
		queryClient.invalidateQueries({ queryKey: ['reports', portfolioId] });
	}

	const createMut = createMutation(() => ({
		mutationFn: (data: Omit<CreateEvictionCaseRequest, 'leaseId'>) =>
			evictionCases.create({ ...data, leaseId }),
		onSuccess: () => {
			showSuccess('Eviction case filed.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const updateMut = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number; data: Record<string, unknown> }) =>
			evictionCases.update(id, data),
		onSuccess: () => {
			showSuccess('Eviction case updated.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const eventMut = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number; data: CreateEvictionCaseEventRequest }) =>
			evictionCases.addEvent(id, data),
		onSuccess: () => {
			showSuccess('Eviction event added.');
			eventTarget = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const deleteMut = createMutation(() => ({
		mutationFn: (id: number) => evictionCases.remove(id),
		onSuccess: () => {
			showSuccess('Eviction case removed.');
			deleteTarget = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function submit() {
		const result = parseForm(evictionCaseSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		if (editingId != null) {
			updateMut.mutate({ id: editingId, data: result.data });
		} else {
			createMut.mutate(result.data);
		}
	}

	function submitEvent() {
		if (!eventTarget) return;
		const result = parseForm(evictionCaseEventSchema, eventForm);
		if (result.errors) {
			eventFormErrors = result.errors;
			return;
		}
		eventFormErrors = {};
		eventMut.mutate({ id: eventTarget.id, data: result.data });
	}

	const columns: ColumnDef<EvictionCase>[] = [
		{ key: 'status', title: 'Status', sortable: true, mobileRole: 'title', accessor: (item) => labelStatus(item.status) },
		{ key: 'filedOnDate', title: 'Filed', format: 'date', sortable: true, mobileRole: 'meta' },
		{ key: 'hearingDate', title: 'Hearing', format: 'date', sortable: true, mobileRole: 'meta' },
		{ key: 'resolvedOnDate', title: 'Resolved', format: 'date', sortable: true, mobileRole: 'meta' },
		{ key: 'caseNumber', title: 'Case #', sortable: true, mobileRole: 'subtitle', accessor: (item) => item.caseNumber ?? '-' },
		{ key: 'eventCount', title: 'Events', mobileRole: 'metric' },
		{ key: 'latestEventDate', title: 'Latest', format: 'date', mobileRole: 'meta' },
		{ key: 'actions', title: '', align: 'right', width: '7rem', mobileRole: 'hidden', cell: actionsCell }
	];

	function labelStatus(status: EvictionCaseStatus): string {
		return statusOptions.find((item) => item.value === status)?.label ?? status;
	}

	function labelEvent(type: EvictionEventType): string {
		return eventOptions.find((item) => item.value === type)?.label ?? type;
	}

	function todayIso(): string {
		return new Date().toISOString().slice(0, 10);
	}
</script>

{#snippet actionsCell(item: EvictionCase)}
	<div class="flex items-center justify-end gap-1">
		<Button variant="ghost" size="sm" class="h-7 w-7 p-0" onclick={(e) => { e.stopPropagation(); openEvent(item); }} aria-label="Add eviction event">
			<Plus class="h-4 w-4" />
		</Button>
		<Button variant="ghost" size="sm" class="h-7 w-7 p-0" onclick={(e) => { e.stopPropagation(); openEdit(item); }} aria-label="Edit eviction case">
			<Pencil class="h-4 w-4" />
		</Button>
		<Button variant="ghost" size="sm" class="h-7 w-7 p-0 text-destructive" onclick={(e) => { e.stopPropagation(); deleteTarget = item; }} aria-label="Delete eviction case">
			<Trash2 class="h-4 w-4" />
		</Button>
	</div>
{/snippet}

<div data-testid="lease-eviction-cases-section">
	<div class="mb-3 flex flex-wrap items-center justify-between gap-2">
		<h2 class="text-lg font-semibold">Eviction cases</h2>
		<Button class="gap-2 shrink-0" onclick={openAdd} data-testid="eviction-case-add-button">
			<Plus class="h-4 w-4" />
			File Eviction
		</Button>
	</div>
	<DataGrid
		data={list}
		{columns}
		loading={query.isLoading || query.isFetching}
		emptyMessage="No eviction case recorded for this lease."
		getRowKey={(item) => item.id}
		onRowClick={(item) => openEdit(item)}
		pageSize={PAGE_SIZE}
		{page}
		totalCount={totalCount}
		serverSide
		{sort}
		onPageChange={(next) => (page = next)}
		onSortChange={(next) => { sort = next ?? ''; page = 1; }}
		data-testid="lease-eviction-cases-grid"
	/>
</div>

<Dialog.Root open={showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'File Eviction Case' : 'Edit Eviction Case'}</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3" data-testid="eviction-case-form">
			<InlineField label="Status" bind:value={form.status} editing type="select" options={statusOptions} error={formErrors.status} testid="eviction-case-status" />
			<div class="grid grid-cols-2 gap-3">
				<InlineField label="Filed date" bind:value={form.filedOnDate} editing type="date" error={formErrors.filedOnDate} testid="eviction-case-filed-on" />
				<InlineField label="Hearing date" bind:value={form.hearingDate} editing type="date" error={formErrors.hearingDate} testid="eviction-case-hearing-date" />
			</div>
			<div class="grid grid-cols-2 gap-3">
				<InlineField label="Court" bind:value={form.courtName} editing type="text" error={formErrors.courtName} testid="eviction-case-court" />
				<InlineField label="Case number" bind:value={form.caseNumber} editing type="text" error={formErrors.caseNumber} testid="eviction-case-number" />
			</div>
			<InlineField label="Resolution" bind:value={form.resolution} editing type="text" error={formErrors.resolution} testid="eviction-case-resolution" />
			<InlineField label="Notes" bind:value={form.notes} editing type="textarea" error={formErrors.notes} testid="eviction-case-notes" />
			{#if editingId != null}
				<div class="rounded-md border border-border bg-muted/20 p-3" data-testid="eviction-case-timeline">
					<p class="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">Timeline</p>
					{#if detailQuery.isLoading}
						<p class="text-sm text-muted-foreground">Loading timeline...</p>
					{:else if timeline.length === 0}
						<p class="text-sm text-muted-foreground">No events recorded yet.</p>
					{:else}
						<div class="space-y-2">
							{#each timeline as event (event.id)}
								<div class="flex items-start justify-between gap-3 rounded-md bg-background px-3 py-2">
									<div>
										<p class="text-sm font-medium">{labelEvent(event.eventType)}</p>
										{#if event.notes}
											<p class="mt-0.5 text-xs text-muted-foreground">{event.notes}</p>
										{/if}
									</div>
									<p class="shrink-0 text-xs text-muted-foreground">{formatDateOnly(event.eventDate)}</p>
								</div>
							{/each}
						</div>
					{/if}
				</div>
			{/if}
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button variant="outline" onclick={closeForm}>Cancel</Button>
			<Button onclick={submit} disabled={createMut.isPending || updateMut.isPending} data-testid="eviction-case-save-button">
				{(createMut.isPending || updateMut.isPending) ? 'Saving...' : editingId == null ? 'File' : 'Save'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root open={eventTarget !== null} onOpenChange={(v) => { if (!v) eventTarget = null; }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>Add Eviction Event</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3" data-testid="eviction-event-form">
			<InlineField label="Event" bind:value={eventForm.eventType} editing type="select" options={eventOptions} error={eventFormErrors.eventType} testid="eviction-event-type" />
			<InlineField label="Event date" bind:value={eventForm.eventDate} editing type="date" error={eventFormErrors.eventDate} testid="eviction-event-date" />
			<InlineField label="Notes" bind:value={eventForm.notes} editing type="textarea" error={eventFormErrors.notes} testid="eviction-event-notes" />
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button variant="outline" onclick={() => (eventTarget = null)}>Cancel</Button>
			<Button onclick={submitEvent} disabled={eventMut.isPending} data-testid="eviction-event-save-button">
				{eventMut.isPending ? 'Saving...' : 'Add Event'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Remove eviction case"
	message={deleteTarget ? `Remove eviction case ${deleteTarget.caseNumber ?? `#${deleteTarget.id}`}?` : ''}
	busy={deleteMut.isPending}
	testid="eviction-case-delete-confirm"
	onconfirm={() => deleteTarget && deleteMut.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
