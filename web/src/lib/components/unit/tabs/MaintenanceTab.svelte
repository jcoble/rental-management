<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { untrack } from 'svelte';
	import type { UnitDashboard, WorkOrder, Expense } from '$lib/types';
	import type { ScanContext } from '$lib/scan/scan-context';
	import { workOrders as workOrdersApi } from '$lib/api/endpoints/workOrders';
	import { expenses as expensesApi } from '$lib/api/endpoints/expenses';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { workOrderSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
import { formatDateOnly } from '$lib/utils/date';
import { formatStatusLabel } from '$lib/utils/status-labels';
	import {
		defaultWorkOrderReceiptScanContext,
		workOrderReceiptScanContext
	} from '$lib/components/unit/maintenance-actions';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import StepperNextButton from '$lib/components/shared/StepperNextButton.svelte';
	import WorkOrderDetail from '$lib/components/records/WorkOrderDetail.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { Wrench, Receipt, Plus, X, ExternalLink, ArrowLeft } from '@lucide/svelte';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';

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

	// A selected work order folds its full detail inline (?wo=<id> on the unit URL); otherwise the list shows.
	const selectedWo = $derived(Number(page.url.searchParams.get('wo')) || null);

	const WO_PRIORITIES = ['Low', 'Normal', 'High', 'Emergency'];
	const TAB_PAGE_SIZE = 20;

	// Work orders for this unit (DB-side ?unitId= filter).
	let workOrderPage = $state(1);
	let workOrderItems = $state<WorkOrder[]>([]);
	const workOrdersQuery = createQuery(() => ({
		queryKey: ['unit-work-orders', portfolioId, unitId, workOrderPage, TAB_PAGE_SIZE],
		enabled: portfolioId > 0 && unitId > 0,
		queryFn: () => workOrdersApi.listPage(portfolioId, {
			unitId,
			skip: (workOrderPage - 1) * TAB_PAGE_SIZE,
			take: TAB_PAGE_SIZE,
			sort: '-requestedAt',
		}),
	}));

	// Expenses tied to this unit's work orders only; the Expenses tab shows the full unit set.
	let receiptPage = $state(1);
	let receiptItems = $state<Expense[]>([]);
	const expensesQuery = createQuery(() => ({
		queryKey: ['unit-work-order-receipts', portfolioId, unitId, receiptPage, TAB_PAGE_SIZE],
		enabled: portfolioId > 0 && unitId > 0,
		queryFn: () => expensesApi.listPage(portfolioId, {
			unitId,
			workOrderLinkedOnly: true,
			skip: (receiptPage - 1) * TAB_PAGE_SIZE,
			take: TAB_PAGE_SIZE,
			sort: '-incurredAt',
		}),
	}));

	$effect(() => {
		void unitId;
		workOrderPage = 1;
		receiptPage = 1;
		workOrderItems = [];
		receiptItems = [];
	});

	$effect(() => {
		const page = workOrdersQuery.data;
		if (!page) return;
		if (page.skip === 0) {
			workOrderItems = page.items;
			return;
		}
		const currentItems = untrack(() => workOrderItems);
		const seen = new Set(currentItems.map((w) => w.id));
		workOrderItems = [...currentItems, ...page.items.filter((w) => !seen.has(w.id))];
	});

	$effect(() => {
		const page = expensesQuery.data;
		if (!page) return;
		if (page.skip === 0) {
			receiptItems = page.items;
			return;
		}
		const currentItems = untrack(() => receiptItems);
		const seen = new Set(currentItems.map((e) => e.id));
		receiptItems = [...currentItems, ...page.items.filter((e) => !seen.has(e.id))];
	});

	const workOrderList = $derived(workOrderItems);
	const workOrderReceipts = $derived(receiptItems);
	const totalWorkOrders = $derived(workOrdersQuery.data?.totalCount ?? workOrderItems.length);
	const totalReceipts = $derived(expensesQuery.data?.totalCount ?? receiptItems.length);
	const hasMoreWorkOrders = $derived(workOrderItems.length < totalWorkOrders);
	const hasMoreReceipts = $derived(receiptItems.length < totalReceipts);

	function invalidate() {
		workOrderItems = [];
		receiptItems = [];
		workOrderPage = 1;
		receiptPage = 1;
		queryClient.invalidateQueries({ queryKey: ['unit-work-orders', portfolioId, unitId] });
		queryClient.invalidateQueries({ queryKey: ['unit-work-order-receipts', portfolioId, unitId] });
		queryClient.invalidateQueries({ queryKey: ['work-orders', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['unit-dashboard', unitId] });
		queryClient.invalidateQueries({ queryKey: ['unit-timeline', unitId] });
	}

	function loadMoreWorkOrders() {
		if (workOrdersQuery.isFetching || !hasMoreWorkOrders) return;
		workOrderPage += 1;
	}

	function loadMoreReceipts() {
		if (expensesQuery.isFetching || !hasMoreReceipts) return;
		receiptPage += 1;
	}

	// Selecting a row is a real navigation step (no replaceState) so Back returns to the list.
	function openWorkOrder(id: number) {
		goto('/units/' + unitId + '?tab=maintenance&view=work-orders&wo=' + id, { keepFocus: true, noScroll: true });
	}

	// Clearing the selection drops ?wo= (replaceState — it's a peer of the list, not a new history step).
	function clearSelection() {
		goto('/units/' + unitId + '?tab=maintenance&view=work-orders', { replaceState: true, keepFocus: true, noScroll: true });
	}

	// ── Inline "new work order" form (reuses workOrderSchema + the app's form conventions) ──
	const emptyCreate = () => ({ title: '', description: '', priority: 'Normal', category: 'General' });
	const createSteps: FormStepperStep[] = [
		{ id: 'issue', label: 'Issue', description: 'Title and details' },
		{ id: 'triage', label: 'Triage', description: 'Priority and category' },
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
		mutationFn: (data: Record<string, unknown>) => workOrdersApi.create(data),
		onSuccess: () => {
			showSuccess('Repair created.');
			closeCreate();
			invalidate();
		},
		onError: (e) => showError(apiErrorMessage(e)),
	}));

	function submitCreate() {
		// The unit fixes the property + unit; validate the user-entered fields against the app schema.
		const result = parseForm(workOrderSchema, {
			...createForm,
			propertyId: String(propertyId),
			unitId: String(unitId),
		});
		if (result.errors) {
			createErrors = result.errors;
			return;
		}
		createErrors = {};
		// result.data already carries propertyId + unitId (validated by the schema); add scope + defaults.
		createMut.mutate({
			portfolioId,
			status: 'New',
			requestedAt: new Date().toISOString(),
			...result.data,
		});
	}

	function validateCreateStep() {
		const result = parseForm(workOrderSchema, {
			...createForm,
			propertyId: String(propertyId),
			unitId: String(unitId),
		});
		const fields = createStep === 0 ? ['title', 'description'] : ['priority', 'category'];
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

<div class="space-y-4" data-testid="unit-maintenance-tab">
{#if selectedWo}
	<!-- Folded work-order detail: the same <WorkOrderDetail> the generic /maintenance/[id] page mounts. -->
	<Button variant="outline" size="sm" class="gap-1" onclick={clearSelection} data-testid="wo-back-to-list">
		<ArrowLeft class="h-4 w-4" /> Back to repairs
	</Button>
	<WorkOrderDetail
		workOrderId={selectedWo}
		onDeleted={clearSelection}
		expectedUnitId={unitId}
		onUnitMismatch={clearSelection}
	/>
{:else}
	<div class="flex flex-wrap justify-end gap-2">
		<Button class="gap-2" onclick={() => (showCreate ? closeCreate() : openCreate())} data-testid="maintenance-create">
			{#if showCreate}<X class="h-4 w-4" /> Cancel{:else}<Plus class="h-4 w-4" /> New repair{/if}
		</Button>
	</div>

	<!-- Inline new-ticket form. -->
	{#if showCreate}
		<div class="rounded-xl border border-border bg-muted/20 p-4" data-testid="maintenance-create-form">
			<h3 class="mb-3 text-sm font-semibold">New repair</h3>
				<FormStepper steps={createSteps} bind:currentStep={createStep} completedSteps={completedCreateSteps} testid="maintenance-create-stepper">
					<div class="space-y-3">
						{#if createStep === 0}
							<div>
								<label class="mb-1 block text-xs font-medium text-muted-foreground" for="wo-title">Title</label>
								<Input id="wo-title" data-testid="maintenance-title-input" bind:value={createForm.title} oninput={() => clearCreateError('title')} placeholder="Issue title" />
								{#if createErrors.title}<p class="mt-1 text-xs text-destructive" data-testid="maintenance-title-error">{createErrors.title}</p>{/if}
							</div>
							<div>
								<label class="mb-1 block text-xs font-medium text-muted-foreground" for="wo-desc">Description</label>
								<textarea id="wo-desc" data-testid="maintenance-description-input" bind:value={createForm.description} oninput={() => clearCreateError('description')} rows={3} class="w-full rounded border border-border bg-background px-3 py-2 text-sm" placeholder="What needs fixing?"></textarea>
								{#if createErrors.description}<p class="mt-1 text-xs text-destructive" data-testid="maintenance-description-error">{createErrors.description}</p>{/if}
							</div>
						{:else}
						<div class="grid grid-cols-2 gap-3">
							<div>
								<label class="mb-1 block text-xs font-medium text-muted-foreground" for="wo-priority">Priority</label>
								<Select.Root type="single" bind:value={createForm.priority}>
									<Select.Trigger id="wo-priority" class="w-full" data-testid="maintenance-priority-input">{formatStatusLabel(createForm.priority)}</Select.Trigger>
									<Select.Content>
										{#each WO_PRIORITIES as p}<Select.Item value={p} label={formatStatusLabel(p)}>{formatStatusLabel(p)}</Select.Item>{/each}
									</Select.Content>
								</Select.Root>
							</div>
							<div>
								<label class="mb-1 block text-xs font-medium text-muted-foreground" for="wo-cat">Category</label>
								<Input id="wo-cat" data-testid="maintenance-category-input" bind:value={createForm.category} oninput={() => clearCreateError('category')} placeholder="Category" />
								{#if createErrors.category}<p class="mt-1 text-xs text-destructive" data-testid="maintenance-category-error">{createErrors.category}</p>{/if}
							</div>
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
						testid="maintenance-create-next"
						onclick={nextCreateStep}
						complete={completedCreateSteps.includes(createStep)}
					/>
				{:else}
					<Button size="sm" disabled={createMut.isPending} onclick={submitCreate} data-testid="maintenance-create-submit">
						{createMut.isPending ? 'Creating…' : 'Create'}
					</Button>
				{/if}
			</div>
		</div>
	{/if}

	<!-- Work orders as compact cards; the full edit/assign/close lives on the WO detail page. -->
	{#if workOrdersQuery.isLoading}
		<LoadingState label="Loading repairs" testid="unit-work-orders-loading" />
	{:else if workOrdersQuery.isError}
		<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="unit-work-orders-error">
			<p class="text-sm font-medium text-destructive">Repairs could not be loaded.</p>
			<Button class="mt-3" variant="outline" size="sm" onclick={() => workOrdersQuery.refetch()}>Try again</Button>
		</div>
	{:else if workOrderList.length === 0}
		<DetailCard title="No repairs" icon={Wrench} accent="muted" testid="maintenance-empty">
			<p class="text-sm text-muted-foreground">Create a ticket when something needs fixing, or scan a vendor invoice.</p>
		</DetailCard>
	{:else}
		<ul class="space-y-2" data-testid="maintenance-work-orders">
			{#each workOrderList as w (w.id)}
				<li class="rounded-xl border bg-card" data-testid="maintenance-wo-{w.id}">
					<div class="p-3">
						<div class="flex items-center justify-between gap-2">
							<span class="min-w-0 truncate text-sm font-medium">{w.title}</span>
							<span class="flex shrink-0 items-center gap-2"><StatusBadge status={w.priority} /><StatusBadge status={w.status} /></span>
						</div>
						<p class="mt-2 line-clamp-2 text-sm text-muted-foreground">{w.description}</p>
						<dl class="mt-2 grid grid-cols-2 gap-2 text-sm sm:grid-cols-3">
							<div><dt class="text-muted-foreground">Category</dt><dd>{w.category}</dd></div>
							<div><dt class="text-muted-foreground">Requested</dt><dd>{formatDateOnly(w.requestedAt)}</dd></div>
							<div><dt class="text-muted-foreground">Cost</dt><dd>{formatAccountingCurrency(w.actualCost ?? w.estimatedCost ?? 0)}</dd></div>
						</dl>
						<div class="mt-3 flex flex-wrap justify-end gap-2">
							<Button
								variant="outline"
								size="sm"
								onclick={() => onScan(workOrderReceiptScanContext(unitId, w.id))}
								data-testid="maintenance-scan-receipt-{w.id}"
							>
								Scan receipt
							</Button>
							<Button variant="outline" size="sm" class="gap-1" onclick={() => openWorkOrder(w.id)} data-testid="maintenance-open-{w.id}">
								Open repair <ExternalLink class="h-3 w-3" />
							</Button>
						</div>
					</div>
				</li>
			{/each}
		</ul>
		{#if hasMoreWorkOrders}
			<div class="flex justify-center">
				<Button variant="outline" size="sm" onclick={loadMoreWorkOrders} disabled={workOrdersQuery.isFetching} data-testid="maintenance-load-more">
					{workOrdersQuery.isFetching ? 'Loading…' : 'Load more'}
				</Button>
			</div>
		{/if}
	{/if}

	<DetailCard title="Receipts on these jobs" icon={Receipt} accent="muted" testid="maintenance-receipts">
		{#snippet actions()}
			<button
				type="button"
				class="text-xs text-primary hover:underline"
				onclick={() => onScan(defaultWorkOrderReceiptScanContext(unitId, workOrderList.map((w) => w.id)))}
				data-testid="maintenance-scan-receipt"
			>Scan receipt</button>
		{/snippet}
		{#if expensesQuery.isLoading}
			<LoadingState label="Loading work-order receipts" testid="maintenance-receipts-loading" />
		{:else if expensesQuery.isError}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="maintenance-receipts-error">
				<p class="text-sm font-medium text-destructive">Work-order receipts could not be loaded.</p>
				<Button class="mt-3" variant="outline" size="sm" onclick={() => expensesQuery.refetch()}>Try again</Button>
			</div>
		{:else if workOrderReceipts.length === 0}
			<p class="text-sm text-muted-foreground">No work-order receipts yet. Snap a receipt to link it to a job.</p>
		{:else}
			<ul class="divide-y text-sm">
				{#each workOrderReceipts as e (e.id)}
					<li class="flex items-center justify-between py-1.5">
						<span class="truncate">{formatDateOnly(e.incurredAt)} · {e.description}</span>
						<span class="flex shrink-0 items-center gap-2"><StatusBadge status={e.status} />{formatAccountingCurrency(e.amount)}</span>
					</li>
				{/each}
			</ul>
			{#if hasMoreReceipts}
				<div class="mt-3 flex justify-center">
					<Button variant="outline" size="sm" onclick={loadMoreReceipts} disabled={expensesQuery.isFetching} data-testid="maintenance-receipts-load-more">
						{expensesQuery.isFetching ? 'Loading…' : 'Load more'}
					</Button>
				</div>
			{/if}
		{/if}
	</DetailCard>
{/if}
</div>
