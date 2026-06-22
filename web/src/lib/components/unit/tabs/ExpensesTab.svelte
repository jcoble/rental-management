<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { untrack } from 'svelte';
	import type { UnitDashboard, Expense } from '$lib/types';
	import type { ScanContext } from '$lib/scan/scan-context';
	import { expenses as expensesApi } from '$lib/api/endpoints/expenses';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { expenseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { money } from '../money';
	import { formatDateOnly } from '$lib/utils/date';
	import {
		EXPENSE_CATEGORIES,
		EXPENSE_CATEGORY_OPTIONS,
		formatExpenseCategory
	} from '$lib/accounting/expense-categories';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { Receipt, Plus, X, ScanLine, ChevronDown, ChevronRight } from '@lucide/svelte';

	let {
		dashboard,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onScan: (context?: Partial<ScanContext>) => void;
	} = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const unitId = $derived(dashboard.unit.id);
	const propertyId = $derived(dashboard.unit.propertyId);

	const EXPENSE_STATUSES = ['Pending', 'Approved', 'Paid'];
	const EXPENSE_PAGE_SIZE = 20;
	const today = () => new Date().toISOString().slice(0, 10);

	// Unit-relevant expenses: tied to the unit OR to one of its work orders (DB-side correlated filter).
	let expensePage = $state(1);
	let expenseItems = $state<Expense[]>([]);
	const expensesQuery = createQuery(() => ({
		queryKey: ['unit-expenses', portfolioId, unitId, expensePage, EXPENSE_PAGE_SIZE],
		enabled: portfolioId > 0 && unitId > 0,
		queryFn: () => expensesApi.listPage(portfolioId, {
			unitId,
			skip: (expensePage - 1) * EXPENSE_PAGE_SIZE,
			take: EXPENSE_PAGE_SIZE,
			sort: '-incurredAt',
		}),
	}));

	$effect(() => {
		void unitId;
		expensePage = 1;
		expenseItems = [];
	});

	$effect(() => {
		const page = expensesQuery.data;
		if (!page) return;
		if (page.skip === 0) {
			expenseItems = page.items;
			return;
		}
		const currentItems = untrack(() => expenseItems);
		const seen = new Set(currentItems.map((e) => e.id));
		expenseItems = [...currentItems, ...page.items.filter((e) => !seen.has(e.id))];
	});

	const list = $derived(expenseItems);
	const totalExpenses = $derived(expensesQuery.data?.totalCount ?? expenseItems.length);
	const hasMoreExpenses = $derived(expenseItems.length < totalExpenses);

	let expandedId = $state<number | null>(null);
	function toggleExpand(id: number) {
		expandedId = expandedId === id ? null : id;
		if (expandedId !== id) editingId = null;
	}

	function invalidate() {
		expenseItems = [];
		expensePage = 1;
		queryClient.invalidateQueries({ queryKey: ['unit-expenses', portfolioId, unitId] });
		queryClient.invalidateQueries({ queryKey: ['expenses', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['unit-dashboard', unitId] });
		queryClient.invalidateQueries({ queryKey: ['unit-timeline', unitId] });
	}

	function loadMoreExpenses() {
		if (expensesQuery.isFetching || !hasMoreExpenses) return;
		expensePage += 1;
	}

	// ── Inline "add expense" form (reuses expenseSchema + the app's form conventions; sets UnitId) ──
	const emptyCreate = () => ({ description: '', amount: '', incurredAt: today(), category: 'Repairs', status: 'Pending' });
	let showCreate = $state(false);
	let createForm = $state(emptyCreate());
	let createErrors = $state<Record<string, string>>({});

	function clearCreateError(field: string) {
		if (!createErrors[field]) return;
		const next = { ...createErrors };
		delete next[field];
		createErrors = next;
	}

	function openCreate() {
		createForm = emptyCreate();
		createErrors = {};
		showCreate = true;
	}
	function closeCreate() {
		showCreate = false;
		createErrors = {};
	}

	const createMut = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => expensesApi.create(data),
		onSuccess: () => {
			showSuccess('Expense added.');
			closeCreate();
			invalidate();
		},
		onError: (e) => showError(apiErrorMessage(e)),
	}));

	function submitCreate() {
		// expenseSchema carries optional propertyId/vendorId/workOrderId as idString — pass blanks for those.
		const result = parseForm(expenseSchema, {
			...createForm,
			propertyId: String(propertyId),
			vendorId: '',
			workOrderId: '',
			dueDate: '',
			paidAt: '',
			subtotal: '',
			taxAmount: '',
			billableToOwner: false,
			notes: '',
		});
		if (result.errors) {
			createErrors = result.errors;
			return;
		}
		createErrors = {};
		createMut.mutate({ portfolioId, unitId, propertyId, ...result.data });
	}

	// ── Expandable expense cards with on-card view/edit ──
	let editingId = $state<number | null>(null);
	let editForm = $state<Record<string, string>>({});
	let editErrors = $state<Record<string, string>>({});

	function startEdit(e: Expense) {
		editForm = {
			description: e.description,
			amount: String(e.amount),
			incurredAt: e.incurredAt?.slice(0, 10) ?? '',
			category: e.category,
			status: e.status,
		};
		editErrors = {};
		editingId = e.id;
	}
	function cancelEdit() {
		editingId = null;
		editErrors = {};
	}

	const editMut = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number; data: Record<string, unknown> }) => expensesApi.update(id, data),
		onSuccess: () => {
			showSuccess('Expense updated.');
			editingId = null;
			invalidate();
		},
		onError: (e) => showError(apiErrorMessage(e)),
	}));

	function submitEdit(e: Expense) {
		// Validate only the fields the inline card edits (description/amount/date/category/status).
		const partial = {
			description: editForm.description,
			amount: editForm.amount,
			incurredAt: editForm.incurredAt,
			category: editForm.category,
			status: editForm.status,
			propertyId: '', vendorId: '', workOrderId: '', dueDate: '', paidAt: '',
			subtotal: '', taxAmount: '', billableToOwner: false, notes: '',
		};
		const result = parseForm(expenseSchema, partial);
		if (result.errors) {
			editErrors = result.errors;
			return;
		}
		editErrors = {};
		editMut.mutate({
			id: e.id,
			data: {
				description: editForm.description,
				amount: Number(editForm.amount),
				incurredAt: editForm.incurredAt,
				category: editForm.category,
				status: editForm.status,
			},
		});
	}
</script>

<div class="space-y-4" data-testid="unit-expenses-tab">
	<div class="flex flex-wrap justify-end gap-2">
		<Button class="gap-2" onclick={() => (showCreate ? closeCreate() : openCreate())} data-testid="expenses-create">
			{#if showCreate}<X class="h-4 w-4" /> Cancel{:else}<Plus class="h-4 w-4" /> Add expense{/if}
		</Button>
		<Button variant="outline" class="gap-2" onclick={() => onScan({ type: 'Expense', returnTo: `/units/${unitId}?tab=expenses` })} data-testid="expenses-scan">
			<ScanLine class="h-4 w-4" /> Scan receipt
		</Button>
	</div>

	<!-- Inline add-expense form. -->
	{#if showCreate}
		<div class="rounded-xl border border-border bg-muted/20 p-4" data-testid="expenses-create-form">
			<h3 class="mb-3 text-sm font-semibold">Add expense</h3>
			<div class="grid gap-3 sm:grid-cols-2">
				<div class="sm:col-span-2">
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="exp-desc">Description</label>
					<Input id="exp-desc" data-testid="expenses-description-input" bind:value={createForm.description} oninput={() => clearCreateError('description')} placeholder="e.g. Dishwasher repair" />
					{#if createErrors.description}<p class="mt-1 text-xs text-destructive" data-testid="expenses-description-error">{createErrors.description}</p>{/if}
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="exp-amount">Amount</label>
					<Input id="exp-amount" data-testid="expenses-amount-input" type="number" step="0.01" bind:value={createForm.amount} oninput={() => clearCreateError('amount')} placeholder="0.00" />
					{#if createErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="expenses-amount-error">{createErrors.amount}</p>{/if}
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="exp-date">Incurred</label>
					<Input id="exp-date" data-testid="expenses-date-input" type="date" bind:value={createForm.incurredAt} oninput={() => clearCreateError('incurredAt')} />
					{#if createErrors.incurredAt}<p class="mt-1 text-xs text-destructive" data-testid="expenses-date-error">{createErrors.incurredAt}</p>{/if}
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="exp-cat">Category</label>
					<Select.Root type="single" bind:value={createForm.category}>
						<Select.Trigger id="exp-cat" class="w-full" data-testid="expenses-category-input">{formatExpenseCategory(createForm.category)}</Select.Trigger>
						<Select.Content>
							{#each EXPENSE_CATEGORY_OPTIONS as option}<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="exp-status">Status</label>
					<Select.Root type="single" bind:value={createForm.status}>
						<Select.Trigger id="exp-status" class="w-full" data-testid="expenses-status-input">{createForm.status}</Select.Trigger>
						<Select.Content>
							{#each EXPENSE_STATUSES as s}<Select.Item value={s} label={s}>{s}</Select.Item>{/each}
						</Select.Content>
					</Select.Root>
				</div>
			</div>
			<div class="mt-3 flex justify-end gap-2">
				<Button variant="outline" size="sm" onclick={closeCreate}>Cancel</Button>
				<Button size="sm" disabled={createMut.isPending} onclick={submitCreate} data-testid="expenses-create-submit">
					{createMut.isPending ? 'Adding…' : 'Add expense'}
				</Button>
			</div>
		</div>
	{/if}

	{#if expensesQuery.isLoading}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">Loading expenses…</p>
	{:else if list.length === 0}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">
			No expenses yet. Add a unit cost (appliance, permit, repair) or snap a receipt.
		</p>
	{:else}
		<ul class="space-y-2" data-testid="expenses-list">
			{#each list as e (e.id)}
				<li class="rounded-xl border bg-card" data-testid="expense-{e.id}">
					<button type="button" class="flex w-full items-center justify-between gap-2 p-3 text-left" onclick={() => toggleExpand(e.id)}>
						<span class="flex min-w-0 items-center gap-2 text-sm">
							{#if expandedId === e.id}<ChevronDown class="h-4 w-4 shrink-0 text-muted-foreground" />{:else}<ChevronRight class="h-4 w-4 shrink-0 text-muted-foreground" />{/if}
							<span class="truncate font-medium">{e.description}</span>
							<span class="shrink-0 text-muted-foreground">· {formatExpenseCategory(e.category)}</span>
						</span>
						<span class="flex shrink-0 items-center gap-2"><StatusBadge status={e.status} /><span class="font-semibold">{money(e.amount)}</span></span>
					</button>
					{#if expandedId === e.id}
						<div class="border-t p-3">
							{#if editingId === e.id}
								<div class="grid gap-3 sm:grid-cols-2" data-testid="expenses-edit-form">
									<InlineField label="Description" bind:value={editForm.description} editing type="text" error={editErrors.description} testid="expenses-edit-description" class="sm:col-span-2" />
									<InlineField label="Amount" bind:value={editForm.amount} editing type="number" error={editErrors.amount} testid="expenses-edit-amount" />
									<InlineField label="Incurred" bind:value={editForm.incurredAt} editing type="date" error={editErrors.incurredAt} testid="expenses-edit-date" />
									<InlineField label="Category" bind:value={editForm.category} editing type="select" options={EXPENSE_CATEGORY_OPTIONS} testid="expenses-edit-category" />
									<InlineField label="Status" bind:value={editForm.status} editing type="select" options={EXPENSE_STATUSES.map((s) => ({ value: s, label: s }))} testid="expenses-edit-status" />
								</div>
								<div class="mt-3 flex justify-end gap-2">
									<Button variant="outline" size="sm" onclick={cancelEdit} disabled={editMut.isPending}>Cancel</Button>
									<Button size="sm" onclick={() => submitEdit(e)} disabled={editMut.isPending} data-testid="expenses-edit-save">
										{editMut.isPending ? 'Saving…' : 'Save'}
									</Button>
								</div>
							{:else}
								<dl class="grid grid-cols-2 gap-2 text-sm sm:grid-cols-3">
									<div><dt class="text-muted-foreground">Amount</dt><dd class="font-medium">{money(e.amount)}</dd></div>
									<div><dt class="text-muted-foreground">Incurred</dt><dd>{formatDateOnly(e.incurredAt)}</dd></div>
									<div><dt class="text-muted-foreground">Category</dt><dd>{formatExpenseCategory(e.category)}</dd></div>
									<div><dt class="text-muted-foreground">Status</dt><dd><StatusBadge status={e.status} /></dd></div>
									{#if e.workOrderId}<div><dt class="text-muted-foreground">Work order</dt><dd>#{e.workOrderId}</dd></div>{/if}
								</dl>
								<div class="mt-3 flex justify-end">
									<Button variant="outline" size="sm" onclick={() => startEdit(e)} data-testid="expenses-edit-{e.id}">Edit</Button>
								</div>
							{/if}
						</div>
					{/if}
				</li>
			{/each}
		</ul>
		{#if hasMoreExpenses}
			<div class="flex justify-center">
				<Button variant="outline" size="sm" onclick={loadMoreExpenses} disabled={expensesQuery.isFetching} data-testid="expenses-load-more">
					{expensesQuery.isFetching ? 'Loading…' : 'Load more'}
				</Button>
			</div>
		{/if}
	{/if}
</div>
