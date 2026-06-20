<script lang="ts">
	import { createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import type { UnitDashboard } from '$lib/types';
	import { payments as paymentsApi } from '$lib/api/endpoints/payments';
	import { expenses as expensesApi } from '$lib/api/endpoints/expenses';
	import { workOrders as workOrdersApi } from '$lib/api/endpoints/workOrders';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Drawer from '$lib/components/ui/drawer';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import type { UnitDrawerAction } from './drawer-actions';

	let {
		open = $bindable(false),
		action,
		dashboard,
	}: {
		open: boolean;
		/** Which action form to show; null closes/clears the drawer. */
		action: UnitDrawerAction | null;
		dashboard: UnitDashboard;
	} = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const unit = $derived(dashboard.unit);
	const leaseId = $derived(dashboard.currentLease?.id);

	const titles: Record<UnitDrawerAction, string> = {
		'post-payment': 'Post payment',
		'add-expense': 'Add expense',
		'create-work-order': 'Create work order',
		'upload-document': 'Upload document',
		scan: 'Scan / upload',
	};

	const today = () => new Date().toISOString().slice(0, 10);

	// --- Form state (reset whenever the action changes) ---
	let payment = $state({ amount: '', dueDate: today(), paymentType: 'Rent' });
	let expense = $state({ description: '', amount: '', incurredAt: today(), category: 'Repairs' });
	let workOrder = $state({ title: '', description: '', priority: 'Normal', category: 'General' });

	$effect(() => {
		// Reset the relevant form each time a new action opens.
		if (action === 'post-payment') payment = { amount: '', dueDate: today(), paymentType: 'Rent' };
		if (action === 'add-expense') expense = { description: '', amount: '', incurredAt: today(), category: 'Repairs' };
		if (action === 'create-work-order') workOrder = { title: '', description: '', priority: 'Normal', category: 'General' };
	});

	function close() {
		open = false;
	}

	function invalidateUnit() {
		queryClient.invalidateQueries({ queryKey: ['unit-dashboard', unit.id] });
		queryClient.invalidateQueries({ queryKey: ['unit-timeline', unit.id] });
	}

	const postPayment = createMutation(() => ({
		mutationFn: () =>
			paymentsApi.create({
				portfolioId,
				leaseId,
				amount: Number(payment.amount),
				dueDate: payment.dueDate,
				paymentType: payment.paymentType,
				status: 'Paid',
				paidDate: payment.dueDate,
			}),
		onSuccess: () => {
			showSuccess('Payment posted.');
			queryClient.invalidateQueries({ queryKey: ['payments', portfolioId] });
			invalidateUnit();
			close();
		},
		onError: (e) => showError(apiErrorMessage(e)),
	}));

	const addExpense = createMutation(() => ({
		mutationFn: () =>
			expensesApi.create({
				portfolioId,
				unitId: unit.id,
				propertyId: unit.propertyId,
				description: expense.description,
				amount: Number(expense.amount),
				incurredAt: expense.incurredAt,
				category: expense.category,
				status: 'Pending',
			}),
		onSuccess: () => {
			showSuccess('Expense added.');
			queryClient.invalidateQueries({ queryKey: ['unit-expenses', portfolioId, unit.id] });
			queryClient.invalidateQueries({ queryKey: ['expenses', portfolioId] });
			invalidateUnit();
			close();
		},
		onError: (e) => showError(apiErrorMessage(e)),
	}));

	const createWorkOrder = createMutation(() => ({
		mutationFn: () =>
			workOrdersApi.create({
				portfolioId,
				propertyId: unit.propertyId,
				unitId: unit.id,
				title: workOrder.title,
				description: workOrder.description,
				priority: workOrder.priority,
				category: workOrder.category,
				status: 'New',
				requestedAt: new Date().toISOString(),
			}),
		onSuccess: () => {
			showSuccess('Work order created.');
			queryClient.invalidateQueries({ queryKey: ['unit-work-orders', portfolioId, unit.id] });
			queryClient.invalidateQueries({ queryKey: ['work-orders', portfolioId] });
			invalidateUnit();
			close();
		},
		onError: (e) => showError(apiErrorMessage(e)),
	}));

	// Scan / upload-document route into the existing scan→draft→confirm flow.
	function goScan() {
		close();
		goto('/scan');
	}
</script>

<Drawer.Root bind:open direction="right">
	<Drawer.Content
		class="fixed inset-y-0 right-0 z-50 flex h-full w-full max-w-md flex-col rounded-l-xl border-l bg-background p-5"
		data-testid="unit-command-drawer"
	>
		<Drawer.Header class="px-0">
			<Drawer.Title>{action ? titles[action] : ''}</Drawer.Title>
			<Drawer.Description class="text-xs text-muted-foreground">
				Unit {unit.unitNumber} · {dashboard.propertyName}
			</Drawer.Description>
		</Drawer.Header>

		<div class="flex-1 overflow-y-auto py-2">
			{#if action === 'post-payment'}
				{#if !leaseId}
					<p class="text-sm text-muted-foreground">This unit has no lease, so rent can't be posted yet.</p>
				{:else}
					<div class="space-y-3" data-testid="drawer-payment-form">
						<div><label class="mb-1 block text-sm font-medium" for="pay-amount">Amount</label><Input id="pay-amount" type="number" step="0.01" bind:value={payment.amount} placeholder="1200.00" /></div>
						<div><label class="mb-1 block text-sm font-medium" for="pay-due">Date</label><Input id="pay-due" type="date" bind:value={payment.dueDate} /></div>
						<div><label class="mb-1 block text-sm font-medium" for="pay-type">Type</label><Input id="pay-type" bind:value={payment.paymentType} /></div>
					</div>
				{/if}
			{:else if action === 'add-expense'}
				<div class="space-y-3" data-testid="drawer-expense-form">
					<div><label class="mb-1 block text-sm font-medium" for="exp-desc">Description</label><Input id="exp-desc" bind:value={expense.description} placeholder="e.g. Dishwasher repair" /></div>
					<div><label class="mb-1 block text-sm font-medium" for="exp-amount">Amount</label><Input id="exp-amount" type="number" step="0.01" bind:value={expense.amount} placeholder="0.00" /></div>
					<div><label class="mb-1 block text-sm font-medium" for="exp-date">Incurred</label><Input id="exp-date" type="date" bind:value={expense.incurredAt} /></div>
					<div><label class="mb-1 block text-sm font-medium" for="exp-cat">Category</label><Input id="exp-cat" bind:value={expense.category} /></div>
				</div>
			{:else if action === 'create-work-order'}
				<div class="space-y-3" data-testid="drawer-work-order-form">
					<div><label class="mb-1 block text-sm font-medium" for="wo-title">Title</label><Input id="wo-title" bind:value={workOrder.title} placeholder="e.g. Leaking faucet" /></div>
					<div><label class="mb-1 block text-sm font-medium" for="wo-desc">Description</label><Input id="wo-desc" bind:value={workOrder.description} placeholder="What needs fixing?" /></div>
					<div><label class="mb-1 block text-sm font-medium" for="wo-cat">Category</label><Input id="wo-cat" bind:value={workOrder.category} /></div>
					<div><label class="mb-1 block text-sm font-medium" for="wo-prio">Priority</label><Input id="wo-prio" bind:value={workOrder.priority} /></div>
				</div>
			{:else if action === 'scan' || action === 'upload-document'}
				<div class="space-y-3" data-testid="drawer-scan">
					<p class="text-sm text-muted-foreground">
						Scan or upload a document for this unit — the computer reads the fields, you confirm a draft, and the record is created.
					</p>
				</div>
			{/if}
		</div>

		<Drawer.Footer class="flex-row justify-end gap-2 px-0">
			<Button variant="outline" onclick={close}>Cancel</Button>
			{#if action === 'post-payment'}
				<Button disabled={!leaseId || postPayment.isPending} onclick={() => postPayment.mutate()} data-testid="drawer-submit-payment">Post payment</Button>
			{:else if action === 'add-expense'}
				<Button disabled={addExpense.isPending} onclick={() => addExpense.mutate()} data-testid="drawer-submit-expense">Add expense</Button>
			{:else if action === 'create-work-order'}
				<Button disabled={createWorkOrder.isPending} onclick={() => createWorkOrder.mutate()} data-testid="drawer-submit-work-order">Create</Button>
			{:else if action === 'scan' || action === 'upload-document'}
				<Button onclick={goScan} data-testid="drawer-go-scan">Go to scan</Button>
			{/if}
		</Drawer.Footer>
	</Drawer.Content>
</Drawer.Root>
