<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { recurringExpenses, type RecurringExpense } from '$lib/api/endpoints/recurring-expenses';
	import { recurringExpenseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import {
		EXPENSE_CATEGORY_OPTIONS,
		formatExpenseCategory
	} from '$lib/accounting/expense-categories';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import RangeDatePicker from '$lib/components/shared/RangeDatePicker.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';

	let { propertyId, canManage = false }: { propertyId: number; canManage?: boolean } = $props();

	const queryClient = useQueryClient();
	const PAGE_SIZE = 10;

	let recurringPage = $state(1);
	let recurringSort = $state('nextRunDate');
	let recurringFrom = $state('');
	let recurringTo = $state('');

	let recurringFilterResetPrimed = false;
	$effect(() => {
		propertyId;
		recurringFrom;
		recurringTo;
		if (!recurringFilterResetPrimed) {
			recurringFilterResetPrimed = true;
			return;
		}
		recurringPage = 1;
	});

	const query = createQuery(() => ({
		queryKey: ['recurring-expenses', propertyId, 'page', recurringPage, recurringSort, recurringFrom, recurringTo, PAGE_SIZE],
		queryFn: () => recurringExpenses.listPage({
			propertyId,
			skip: (recurringPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
			sort: recurringSort || undefined,
			from: recurringFrom || undefined,
			to: recurringTo || undefined
		}),
		enabled: !isNaN(propertyId) && propertyId > 0
	}));
	const list = $derived(query.data?.items ?? []);
	const totalCount = $derived(query.data?.totalCount ?? 0);

	const categoryOptions = EXPENSE_CATEGORY_OPTIONS;
	const frequencyOptions = [
		{ value: 'Monthly', label: 'Monthly' },
		{ value: 'Quarterly', label: 'Quarterly' },
		{ value: 'Annual', label: 'Annual' }
	];

	const emptyForm = {
		category: 'Insurance',
		description: '',
		amount: '',
		frequency: 'Monthly',
		startDate: '',
		notes: ''
	};

	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...emptyForm });
	let formErrors = $state<Record<string, string>>({});

	function openAdd() {
		if (!canManage) return;
		editingId = null;
		form = { ...emptyForm };
		formErrors = {};
		showForm = true;
	}

	function openEdit(t: RecurringExpense) {
		if (!canManage) return;
		editingId = t.id;
		form = {
			category: t.category,
			description: t.description,
			amount: String(t.amount),
			frequency: t.frequency,
			startDate: t.startDate ? t.startDate.slice(0, 10) : '',
			notes: t.notes ?? ''
		};
		formErrors = {};
		showForm = true;
	}

	function closeForm() {
		showForm = false;
		editingId = null;
		formErrors = {};
	}

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['recurring-expenses', propertyId] });
	}

	const createMut = createMutation(() => ({
		// Spread `data` first, then pin `propertyId` so the real id wins: the form schema carries a
		// null `propertyId`, which would otherwise clobber the prop and save the expense unassigned.
		mutationFn: (data: Record<string, unknown>) => recurringExpenses.create({ ...data, propertyId }),
		onSuccess: () => {
			showSuccess('Recurring expense added.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const updateMut = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number; data: Record<string, unknown> }) => recurringExpenses.update(id, data),
		onSuccess: () => {
			showSuccess('Recurring expense updated.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	let deleteTarget = $state<RecurringExpense | null>(null);
	const deleteMut = createMutation(() => ({
		mutationFn: (id: number) => recurringExpenses.remove(id),
		onSuccess: () => {
			showSuccess('Recurring expense removed.');
			deleteTarget = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function submit() {
		const result = parseForm(recurringExpenseSchema, form);
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

	const columns: ColumnDef<RecurringExpense>[] = [
		{ key: 'description', title: 'Description', sortable: true, mobileRole: 'title' },
		{ key: 'category', title: 'Category', sortable: true, mobileRole: 'subtitle', accessor: (t) => formatExpenseCategory(t.category) },
		{ key: 'amount', title: 'Amount', format: 'currency', sortable: true, mobileRole: 'metric' },
		{ key: 'frequency', title: 'Frequency', mobileRole: 'meta' },
		{ key: 'nextRunDate', title: 'Next run', format: 'date', sortable: true, mobileRole: 'meta' },
		{ key: 'actions', title: '', align: 'right', width: '5rem', mobileRole: 'hidden', cell: actionsCell }
	];
</script>

{#snippet actionsCell(t: RecurringExpense)}
	{#if canManage}
	<div class="flex items-center justify-end gap-1">
		<Button variant="ghost" size="sm" class="h-7 w-7 p-0" onclick={(e) => { e.stopPropagation(); openEdit(t); }} aria-label="Edit recurring expense">
			<Pencil class="h-4 w-4" />
		</Button>
		<Button variant="ghost" size="sm" class="h-7 w-7 p-0 text-destructive" onclick={(e) => { e.stopPropagation(); deleteTarget = t; }} aria-label="Delete recurring expense">
			<Trash2 class="h-4 w-4" />
		</Button>
	</div>
	{/if}
{/snippet}

<div class="mb-6" data-testid="property-detail-recurring-expenses">
	<h2 class="mb-1 text-lg font-semibold">Recurring expenses</h2>
	<p class="mb-3 text-sm text-muted-foreground">Enter a standing cost once (insurance, taxes, HOA) and it's booked automatically each period.</p>
	<DataGrid
		data={list}
		{columns}
		loading={query.isLoading || query.isFetching}
		emptyMessage="No recurring expenses yet."
		getRowKey={(t) => t.id}
		onRowClick={canManage ? (t) => openEdit(t) : undefined}
		pageSize={PAGE_SIZE}
		page={recurringPage}
		totalCount={totalCount}
		serverSide
		sort={recurringSort}
		onPageChange={(page) => (recurringPage = page)}
		onSortChange={(sort) => { recurringSort = sort ?? ''; recurringPage = 1; }}
		data-testid="property-recurring-expenses-grid"
	>
		{#snippet toolbar()}
			<div class="flex flex-1"></div>
			<RangeDatePicker
				bind:start={recurringFrom}
				bind:end={recurringTo}
				presets
				placeholder="Next run dates"
				align="end"
				testid="property-recurring-expenses-date-range"
			/>
			{#if canManage}
			<Button class="gap-2 shrink-0" onclick={openAdd} data-testid="recurring-expense-add-button">
				<Plus class="h-4 w-4" />
				Add Recurring Expense
			</Button>
			{/if}
		{/snippet}
	</DataGrid>
</div>

<Dialog.Root open={canManage && showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-w-sm">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'Add Recurring Expense' : 'Edit Recurring Expense'}</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3" data-testid="recurring-expense-form">
			<InlineField label="Description" bind:value={form.description} editing type="text" error={formErrors.description} testid="recurring-expense-description" />
			<InlineField label="Category" bind:value={form.category} editing type="select" options={categoryOptions} error={formErrors.category} testid="recurring-expense-category" />
			<div class="grid grid-cols-2 gap-3">
				<InlineField label="Amount" bind:value={form.amount} editing type="number" error={formErrors.amount} testid="recurring-expense-amount" />
				<InlineField label="Frequency" bind:value={form.frequency} editing type="select" options={frequencyOptions} error={formErrors.frequency} testid="recurring-expense-frequency" />
			</div>
			<InlineField label="Start date" bind:value={form.startDate} editing type="date" error={formErrors.startDate} testid="recurring-expense-start" />
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button variant="outline" onclick={closeForm}>Cancel</Button>
			<Button onclick={submit} disabled={createMut.isPending || updateMut.isPending} data-testid="recurring-expense-save-button">
				{(createMut.isPending || updateMut.isPending) ? 'Saving…' : editingId == null ? 'Add' : 'Save'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={canManage && deleteTarget !== null}
	title="Remove recurring expense"
	message={deleteTarget ? `Remove "${deleteTarget.description}"? Already-generated expenses are kept.` : ''}
	busy={deleteMut.isPending}
	testid="recurring-expense-delete-confirm"
	onconfirm={() => deleteTarget && deleteMut.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
