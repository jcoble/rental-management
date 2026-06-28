<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { untrack } from 'svelte';
	import type { UnitDashboard, Expense } from '$lib/types';
	import type { ScanContext } from '$lib/scan/scan-context';
	import { expenses as expensesApi } from '$lib/api/endpoints/expenses';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { expenseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { money } from '../money';
	import {
		EXPENSE_CATEGORY_OPTIONS,
		formatExpenseCategory
	} from '$lib/accounting/expense-categories';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ExpenseDetail from '$lib/components/records/ExpenseDetail.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { Plus, X, ScanLine, ArrowLeft } from '@lucide/svelte';

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

	// A selected expense folds its full detail inline (?expense=<id> on the unit URL); otherwise the list shows.
	const selectedExpense = $derived(Number(page.url.searchParams.get('expense')) || null);

	// Selecting a row is a real navigation step (no replaceState) so Back returns to the list.
	function openExpense(id: number) {
		goto('/units/' + unitId + '?tab=expenses&expense=' + id, { keepFocus: true, noScroll: true });
	}

	// Clearing the selection drops ?expense= (replaceState — peer of the list, not a new history step).
	function clearSelection() {
		goto('/units/' + unitId + '?tab=expenses', { replaceState: true, keepFocus: true, noScroll: true });
	}

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
		const pageData = expensesQuery.data;
		if (!pageData) return;
		if (pageData.skip === 0) {
			expenseItems = pageData.items;
			return;
		}
		const currentItems = untrack(() => expenseItems);
		const seen = new Set(currentItems.map((e) => e.id));
		expenseItems = [...currentItems, ...pageData.items.filter((e) => !seen.has(e.id))];
	});

	const list = $derived(expenseItems);
	const totalExpenses = $derived(expensesQuery.data?.totalCount ?? expenseItems.length);
	const hasMoreExpenses = $derived(expenseItems.length < totalExpenses);

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
</script>

<div class="space-y-4" data-testid="unit-expenses-tab">
{#if selectedExpense}
	<!-- Folded expense detail: the same <ExpenseDetail> the generic /accounting/expenses/[id] page mounts. -->
	<Button variant="outline" size="sm" class="gap-1" onclick={clearSelection} data-testid="expense-back-to-list">
		<ArrowLeft class="h-4 w-4" /> Back to expenses
	</Button>
	<ExpenseDetail expenseId={selectedExpense} onDeleted={clearSelection} />
{:else}
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
					<button type="button" class="flex w-full items-center justify-between gap-2 p-3 text-left" onclick={() => openExpense(e.id)} data-testid="expense-open-{e.id}">
						<span class="flex min-w-0 items-center gap-2 text-sm">
							<span class="truncate font-medium">{e.description}</span>
							<span class="shrink-0 text-muted-foreground">· {formatExpenseCategory(e.category)}</span>
						</span>
						<span class="flex shrink-0 items-center gap-2"><StatusBadge status={e.status} /><span class="font-semibold">{money(e.amount)}</span></span>
					</button>
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
{/if}
</div>
