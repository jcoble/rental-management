<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { pushState, replaceState } from '$app/navigation';
	import { page } from '$app/state';
	import { onMount } from 'svelte';
	import type { UnitDashboard } from '$lib/types';
	import type { ScanContext } from '$lib/scan/scan-context';
	import { expenses as expensesApi } from '$lib/api/endpoints/expenses';
	import { properties } from '$lib/api/endpoints/properties';
	import { loans } from '$lib/api/endpoints/loans';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { expenseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { businessDateOrToday } from '$lib/utils/business-date';
	import { money, unitMoneySectionGates } from '../money';
	import {
		EXPENSE_CATEGORY_OPTIONS,
		formatExpenseCategory
	} from '$lib/accounting/expense-categories';
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import StepperNextButton from '$lib/components/shared/StepperNextButton.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import ExpenseDetail from '$lib/components/records/ExpenseDetail.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { Plus, X, ScanLine, ArrowLeft } from '@lucide/svelte';

	let {
		dashboard,
		businessDate = null,
		businessDatePending = false,
		onScan,
		tabQuery = 'expenses',
		ledgerQuery,
	}: {
		dashboard: UnitDashboard;
		businessDate?: string | null;
		businessDatePending?: boolean;
		onScan: (context?: Partial<ScanContext>) => void;
		tabQuery?: string;
		ledgerQuery?: string;
	} = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const unitId = $derived(dashboard.unit.id);
	const propertyId = $derived(dashboard.unit.propertyId);

	const EXPENSE_STATUSES = ['Pending', 'Approved', 'Paid'];
	const EXPENSE_PAGE_SIZE = 20;

	// A selected expense folds its full detail inline (?expense=<id> on the unit URL); otherwise the list shows.
	let selectedExpense = $state(Number(page.url.searchParams.get('expense')) || null);

	onMount(() => {
		const syncSelection = () => {
			selectedExpense = Number(new URL(window.location.href).searchParams.get('expense')) || null;
		};
		window.addEventListener('popstate', syncSelection);
		return () => window.removeEventListener('popstate', syncSelection);
	});

	function unitUrl(params: Record<string, string | number | null | undefined> = {}) {
		const url = new URL(`/units/${unitId}`, page.url.origin);
		url.searchParams.set('tab', tabQuery);
		if (ledgerQuery) url.searchParams.set('view', ledgerQuery === 'expenses' ? 'operating-costs' : 'tenant-account');
		for (const [key, value] of Object.entries(params)) {
			if (value !== null && value !== undefined && value !== '') {
				url.searchParams.set(key, String(value));
			}
		}
		return `${url.pathname}${url.search}`;
	}

	// Selecting a row is a real navigation step (no replaceState) so Back returns to the list.
	function openExpense(id: number) {
		selectedExpense = id;
		pushState(unitUrl({ expense: id }), {
			...(page.state ?? {}),
			unitTab: 'money',
			unitView: 'operating-costs',
			unitExpenseId: id,
			unitPaymentId: null,
		});
	}

	// Clearing the selection drops ?expense= (replaceState — peer of the list, not a new history step).
	function clearSelection() {
		selectedExpense = null;
		replaceState(unitUrl(), {
			...(page.state ?? {}),
			unitTab: 'money',
			unitView: 'operating-costs',
			unitExpenseId: null,
		});
	}

	const propertyQuery = createQuery(() => ({
		queryKey: ['property', propertyId],
		enabled: propertyId > 0,
		queryFn: () => properties.get(propertyId)
	}));
	const rentalStructure = $derived(propertyQuery.data?.rentalStructure);
	const moneySections = $derived(unitMoneySectionGates({
		rentalStructure,
		propertyId,
		tenantAccountId: dashboard.tenantAccountId
	}));

	// Unit-relevant expenses: tied to the unit OR to one of its work orders (DB-side correlated filter).
	let expensePage = $state(1);
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
	});

	const list = $derived(expensesQuery.data?.items ?? []);

	let propertyExpensePage = $state(1);
	const propertyExpensesQuery = createQuery(() => ({
		queryKey: ['property-expenses', portfolioId, propertyId, propertyExpensePage, EXPENSE_PAGE_SIZE],
		enabled: portfolioId > 0 && moneySections.propertyExpenses,
		queryFn: () => expensesApi.listPage(portfolioId, {
			operationalScope: 'Property',
			propertyId,
			skip: (propertyExpensePage - 1) * EXPENSE_PAGE_SIZE,
			take: EXPENSE_PAGE_SIZE,
			sort: '-incurredAt'
		})
	}));

	let financingPage = $state(1);
	const financingQuery = createQuery(() => ({
		queryKey: ['property-financing', propertyId, financingPage, EXPENSE_PAGE_SIZE],
		enabled: moneySections.financing,
		queryFn: () => loans.listPage({
			propertyId,
			skip: (financingPage - 1) * EXPENSE_PAGE_SIZE,
			take: EXPENSE_PAGE_SIZE,
			sort: '-startDate'
		})
	}));

	function invalidate() {
		expensePage = 1;
		propertyExpensePage = 1;
		financingPage = 1;
		queryClient.invalidateQueries({ queryKey: ['unit-expenses', portfolioId, unitId] });
		queryClient.invalidateQueries({ queryKey: ['property-expenses', portfolioId, propertyId] });
		queryClient.invalidateQueries({ queryKey: ['expenses', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['unit-dashboard', unitId] });
		queryClient.invalidateQueries({ queryKey: ['unit-timeline', unitId] });
	}

	// ── Inline "add expense" form (reuses expenseSchema + the app's form conventions; sets UnitId) ──
	const moneyDate = $derived(businessDateOrToday(businessDate));
	const emptyCreate = () => ({ description: '', amount: '', incurredAt: moneyDate, category: 'Repairs', status: 'Pending' });
	const createSteps: FormStepperStep[] = [
		{ id: 'details', label: 'Details', description: 'Amount and date' },
		{ id: 'context', label: 'Context', description: 'Category and status' },
	];
	let showCreate = $state(false);
	let createForm = $state(emptyCreate());
	let createErrors = $state<Record<string, string>>({});
	let createStep = $state(0);
	let completedCreateSteps = $state<number[]>([]);

	function clearCreateError(field: string) {
		if (!createErrors[field]) return;
		const next = { ...createErrors };
		delete next[field];
		createErrors = next;
	}

	function openCreate() {
		createForm = emptyCreate();
		createErrors = {};
		createStep = 0;
		completedCreateSteps = [];
		showCreate = true;
	}
	function closeCreate() {
		showCreate = false;
		createErrors = {};
		createStep = 0;
		completedCreateSteps = [];
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

	function validateCreateStep() {
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
		const fields = createStep === 0 ? ['description', 'amount', 'incurredAt'] : ['category', 'status'];
		const nextErrors: Record<string, string> = {};
		if (result.errors) {
			for (const field of fields) {
				if (result.errors[field]) nextErrors[field] = result.errors[field];
			}
		}
		const retainedErrors = { ...createErrors };
		for (const field of fields) delete retainedErrors[field];
		createErrors = { ...retainedErrors, ...nextErrors };
		return Object.keys(nextErrors).length === 0;
	}

	function nextCreateStep() {
		if (!validateCreateStep()) return;
		if (!completedCreateSteps.includes(createStep)) {
			completedCreateSteps = [...completedCreateSteps, createStep];
		}
		createStep = Math.min(createStep + 1, createSteps.length - 1);
	}
</script>

<div class="space-y-4" data-testid="unit-expenses-tab">
{#if selectedExpense}
	<!-- Folded expense detail: the same <ExpenseDetail> the generic /accounting/expenses/[id] page mounts. -->
	<Button variant="outline" size="sm" class="gap-1" onclick={clearSelection} data-testid="expense-back-to-list">
		<ArrowLeft class="h-4 w-4" /> Back to expenses
	</Button>
	<ExpenseDetail
		expenseId={selectedExpense}
		onDeleted={clearSelection}
		expectedUnitId={unitId}
		onUnitMismatch={clearSelection}
	/>
{:else}
	<div class="flex flex-wrap justify-end gap-2">
		<Button
			class="gap-2"
			disabled={!showCreate && businessDatePending}
			aria-busy={!showCreate && businessDatePending}
			onclick={() => (showCreate ? closeCreate() : openCreate())}
			data-testid="expenses-create"
		>
			{#if showCreate}<X class="h-4 w-4" /> Cancel{:else}<Plus class="h-4 w-4" /> Add expense{/if}
		</Button>
		<Button variant="outline" class="gap-2" onclick={() => onScan({ type: 'Expense', returnTo: unitUrl() })} data-testid="expenses-scan">
			<ScanLine class="h-4 w-4" /> Scan receipt
		</Button>
	</div>

	<!-- Inline add-expense form. -->
	{#if showCreate}
		<div class="rounded-xl border border-border bg-muted/20 p-4" data-testid="expenses-create-form">
			<h3 class="mb-3 text-sm font-semibold">Add expense</h3>
			<FormStepper steps={createSteps} bind:currentStep={createStep} completedSteps={completedCreateSteps} testid="expenses-create-stepper">
				<div class="grid gap-3 sm:grid-cols-2">
					{#if createStep === 0}
						<div class="sm:col-span-2">
							<label class="mb-1 block text-xs font-medium text-muted-foreground" for="exp-desc">Description</label>
							<Input id="exp-desc" data-testid="expenses-description-input" bind:value={createForm.description} oninput={() => clearCreateError('description')} placeholder="e.g. Dishwasher repair" />
							{#if createErrors.description}<p class="mt-1 text-xs text-destructive" data-testid="expenses-description-error">{createErrors.description}</p>{/if}
						</div>
						<div>
							<label class="mb-1 block text-xs font-medium text-muted-foreground" for="exp-amount">Amount</label>
							<Input id="exp-amount" data-testid="expenses-amount-input" type="text" inputmode="decimal" mask="currency" bind:value={createForm.amount} oninput={() => clearCreateError('amount')} placeholder="0.00" />
							{#if createErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="expenses-amount-error">{createErrors.amount}</p>{/if}
						</div>
						<div>
							<label class="mb-1 block text-xs font-medium text-muted-foreground" for="exp-date">Incurred</label>
							<DatePicker
								id="exp-date"
								testid="expenses-date-input"
								bind:value={createForm.incurredAt}
								onchange={() => clearCreateError('incurredAt')}
							/>
							{#if createErrors.incurredAt}<p class="mt-1 text-xs text-destructive" data-testid="expenses-date-error">{createErrors.incurredAt}</p>{/if}
						</div>
					{:else}
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
								<Select.Trigger id="exp-status" class="w-full" data-testid="expenses-status-input">{formatStatusLabel(createForm.status)}</Select.Trigger>
								<Select.Content>
									{#each EXPENSE_STATUSES as s}<Select.Item value={s} label={formatStatusLabel(s)}>{formatStatusLabel(s)}</Select.Item>{/each}
								</Select.Content>
							</Select.Root>
						</div>
					{/if}
				</div>
			</FormStepper>
			<div class="mt-3 flex justify-end gap-2">
				<Button variant="outline" size="sm" onclick={closeCreate}>Cancel</Button>
				{#if createStep > 0}
					<Button variant="outline" size="sm" onclick={() => (createStep = Math.max(createStep - 1, 0))}>Back</Button>
				{/if}
				{#if createStep < createSteps.length - 1}
					<StepperNextButton
						testid="expenses-create-next"
						onclick={nextCreateStep}
						complete={completedCreateSteps.includes(createStep)}
					/>
				{:else}
					<Button size="sm" disabled={createMut.isPending} onclick={submitCreate} data-testid="expenses-create-submit">
						{createMut.isPending ? 'Adding…' : 'Add expense'}
					</Button>
				{/if}
			</div>
		</div>
	{/if}

	{#if expensesQuery.isLoading}
		<LoadingState label="Loading Unit expenses" testid="unit-expenses-loading" />
	{:else if expensesQuery.isError}
		<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="unit-expenses-error">
			<p class="text-sm font-medium text-destructive">Unit expenses could not be loaded.</p>
			<Button class="mt-3" variant="outline" size="sm" onclick={() => expensesQuery.refetch()}>Try again</Button>
		</div>
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
		{#if expensesQuery.data && expensesQuery.data.totalCount > EXPENSE_PAGE_SIZE}
			<div class="flex items-center justify-between" data-testid="unit-expenses-paging">
				<Button variant="outline" size="sm" onclick={() => (expensePage -= 1)} disabled={expensePage === 1 || expensesQuery.isFetching}>Previous</Button>
				<span class="text-sm text-muted-foreground">{expensesQuery.data.totalCount} Unit costs</span>
				<Button variant="outline" size="sm" onclick={() => (expensePage += 1)} disabled={expensePage * EXPENSE_PAGE_SIZE >= expensesQuery.data.totalCount || expensesQuery.isFetching}>Next</Button>
			</div>
		{/if}
	{/if}

	{#if propertyQuery.isLoading}
		<section class="border-t pt-6" data-testid="property-money-sections-loading">
			<LoadingState label="Loading Property money details" testid="property-money-loading" />
		</section>
	{:else if propertyQuery.isError}
		<section class="border-t pt-6" data-testid="property-money-sections-error">
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert">
				<p class="text-sm font-medium text-destructive">Property money details could not be loaded.</p>
				<Button class="mt-3" variant="outline" size="sm" onclick={() => propertyQuery.refetch()}>Try again</Button>
			</div>
		</section>
	{/if}

	{#if !propertyQuery.isLoading && !propertyQuery.isError && moneySections.propertyExpenses}
		<section class="space-y-3 border-t pt-6" data-testid="property-expenses-section">
			<div>
				<h3 class="font-semibold">Property expenses</h3>
				<p class="text-sm text-muted-foreground">Costs are recorded for this single-rental property instead of its unit.</p>
			</div>
			{#if propertyExpensesQuery.isLoading}
				<LoadingState label="Loading Property expenses" testid="property-expenses-loading" />
			{:else if propertyExpensesQuery.isError}
				<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="property-expenses-error">
					<p class="text-sm font-medium text-destructive">Property expenses could not be loaded.</p>
					<Button class="mt-3" variant="outline" size="sm" onclick={() => propertyExpensesQuery.refetch()}>Try again</Button>
				</div>
			{:else if (propertyExpensesQuery.data?.items.length ?? 0) === 0}
				<p class="rounded-xl border bg-card p-4 text-sm text-muted-foreground">No Property expenses.</p>
			{:else}
				<ul class="space-y-2">
					{#each propertyExpensesQuery.data?.items ?? [] as e (e.id)}
						<li class="rounded-xl border bg-card p-3">
							<div class="flex items-center justify-between gap-3">
								<span class="truncate text-sm font-medium">{e.description}</span>
								<span class="font-semibold">{money(e.amount)}</span>
							</div>
						</li>
					{/each}
				</ul>
				{#if propertyExpensesQuery.data && propertyExpensesQuery.data.totalCount > EXPENSE_PAGE_SIZE}
					<div class="flex items-center justify-between">
						<Button variant="outline" size="sm" onclick={() => (propertyExpensePage -= 1)} disabled={propertyExpensePage === 1 || propertyExpensesQuery.isFetching}>Previous</Button>
						<span class="text-sm text-muted-foreground">{propertyExpensesQuery.data.totalCount} Property expenses</span>
						<Button variant="outline" size="sm" onclick={() => (propertyExpensePage += 1)} disabled={propertyExpensePage * EXPENSE_PAGE_SIZE >= propertyExpensesQuery.data.totalCount || propertyExpensesQuery.isFetching}>Next</Button>
					</div>
				{/if}
			{/if}
		</section>
	{/if}

	{#if !propertyQuery.isLoading && !propertyQuery.isError && moneySections.financing}
		<section class="space-y-3 border-t pt-6" data-testid="financing-section">
			<div>
				<h3 class="font-semibold">Financing</h3>
				<p class="text-sm text-muted-foreground">Property debt remains separate from operating costs.</p>
			</div>
			{#if financingQuery.isLoading}
				<LoadingState label="Loading financing" testid="financing-loading" />
			{:else if financingQuery.isError}
				<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="financing-error">
					<p class="text-sm font-medium text-destructive">Financing could not be loaded.</p>
					<Button class="mt-3" variant="outline" size="sm" onclick={() => financingQuery.refetch()}>Try again</Button>
				</div>
			{:else if (financingQuery.data?.items.length ?? 0) === 0}
				<p class="rounded-xl border bg-card p-4 text-sm text-muted-foreground">No financing recorded.</p>
			{:else}
				<ul class="space-y-2">
					{#each financingQuery.data?.items ?? [] as loan (loan.id)}
						<li class="rounded-xl border bg-card p-3">
							<div class="flex items-center justify-between gap-3">
								<span class="truncate text-sm font-medium">{loan.lender}</span>
								<span class="font-semibold">{money(loan.currentBalance)}</span>
							</div>
							<p class="text-xs text-muted-foreground">{money(loan.monthlyPrincipalInterest + loan.monthlyEscrow)}/month · {formatStatusLabel(loan.status)}</p>
						</li>
					{/each}
				</ul>
				{#if financingQuery.data && financingQuery.data.totalCount > EXPENSE_PAGE_SIZE}
					<div class="flex items-center justify-between">
						<Button variant="outline" size="sm" onclick={() => (financingPage -= 1)} disabled={financingPage === 1 || financingQuery.isFetching}>Previous</Button>
						<span class="text-sm text-muted-foreground">{financingQuery.data.totalCount} loans</span>
						<Button variant="outline" size="sm" onclick={() => (financingPage += 1)} disabled={financingPage * EXPENSE_PAGE_SIZE >= financingQuery.data.totalCount || financingQuery.isFetching}>Next</Button>
					</div>
				{/if}
			{/if}
		</section>
	{/if}
{/if}
</div>
