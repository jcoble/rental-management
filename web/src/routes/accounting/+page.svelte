<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { payments } from '$lib/api/endpoints/payments';
	import { expenses } from '$lib/api/endpoints/expenses';
	import { leases } from '$lib/api/endpoints/leases';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { properties } from '$lib/api/endpoints/properties';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const paymentsQuery = createQuery(() => ({
		queryKey: ['payments', portfolioId],
		queryFn: () => payments.list(portfolioId),
	}));
	const expensesQuery = createQuery(() => ({
		queryKey: ['expenses', portfolioId],
		queryFn: () => expenses.list(portfolioId),
	}));
	const paymentSummaryQuery = createQuery(() => ({
		queryKey: ['payment-summary', portfolioId],
		queryFn: () => payments.summary(portfolioId),
	}));
	const expenseSummaryQuery = createQuery(() => ({
		queryKey: ['expense-summary', portfolioId],
		queryFn: () => expenses.summary(portfolioId),
	}));
	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId],
		queryFn: () => leases.list(portfolioId),
	}));
	const vendorsQuery = createQuery(() => ({
		queryKey: ['vendors', portfolioId],
		queryFn: () => vendors.list(portfolioId),
	}));
	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId),
	}));

	let paymentForm = $state({ leaseId: '', amount: '', dueDate: '', type: 'Rent', status: 'Scheduled' });
	let expenseForm = $state({ category: 'Repairs', description: '', amount: '', incurredAt: '', propertyId: '', vendorId: '', status: 'Pending' });

	const createPaymentMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => payments.create(data),
		onSuccess: () => {
			paymentForm = { leaseId: '', amount: '', dueDate: '', type: 'Rent', status: 'Scheduled' };
			queryClient.invalidateQueries({ queryKey: ['payments', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['payment-summary', portfolioId] });
		},
	}));

	const createExpenseMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => expenses.create(data),
		onSuccess: () => {
			expenseForm = { category: 'Repairs', description: '', amount: '', incurredAt: '', propertyId: '', vendorId: '', status: 'Pending' };
			queryClient.invalidateQueries({ queryKey: ['expenses', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['expense-summary', portfolioId] });
		},
	}));

	const markPaidMutation = createMutation(() => ({
		mutationFn: (id: number) => payments.markPaid(id, {}),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['payments', portfolioId] }),
	}));

	function submitPayment() {
		if (!paymentForm.leaseId || !paymentForm.amount || !paymentForm.dueDate) return;
		createPaymentMutation.mutate({
			portfolioId,
			leaseId: Number(paymentForm.leaseId),
			type: paymentForm.type,
			status: paymentForm.status,
			amount: Number(paymentForm.amount),
			dueDate: paymentForm.dueDate,
		});
	}

	function submitExpense() {
		if (!expenseForm.description || !expenseForm.amount || !expenseForm.incurredAt) return;
		createExpenseMutation.mutate({
			portfolioId,
			category: expenseForm.category,
			description: expenseForm.description,
			amount: Number(expenseForm.amount),
			incurredAt: expenseForm.incurredAt,
			status: expenseForm.status,
			propertyId: expenseForm.propertyId ? Number(expenseForm.propertyId) : null,
			vendorId: expenseForm.vendorId ? Number(expenseForm.vendorId) : null,
		});
	}

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(value || 0);
	}
</script>

<svelte:head>
	<title>Accounting - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Accounting</h1>
		<p class="text-sm text-text-secondary">Rent ledger, receivables, expenses, and owner-facing books.</p>
	</div>

	<div class="mb-5 grid gap-4 md:grid-cols-4">
		<div class="rounded-lg border border-border bg-surface p-4"><p class="text-xs text-text-secondary">Collected</p><p class="text-2xl font-bold">{money((paymentSummaryQuery.data as any)?.totalCollected || 0)}</p></div>
		<div class="rounded-lg border border-border bg-surface p-4"><p class="text-xs text-text-secondary">Outstanding</p><p class="text-2xl font-bold">{money((paymentSummaryQuery.data as any)?.totalOutstanding || 0)}</p></div>
		<div class="rounded-lg border border-border bg-surface p-4"><p class="text-xs text-text-secondary">Overdue</p><p class="text-2xl font-bold">{money((paymentSummaryQuery.data as any)?.overdueTotal || 0)}</p></div>
		<div class="rounded-lg border border-border bg-surface p-4"><p class="text-xs text-text-secondary">Expenses</p><p class="text-2xl font-bold">{money((expenseSummaryQuery.data as any)?.thisMonth || 0)}</p></div>
	</div>

	<div class="mb-5 grid gap-4 lg:grid-cols-2">
		<div class="rounded-lg border border-border bg-surface p-4">
			<h2 class="mb-3 font-semibold">Add Payment Charge</h2>
			<div class="space-y-2">
				<select bind:value={paymentForm.leaseId} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm">
					<option value="">Select lease</option>
					{#each leasesQuery.data || [] as lease}
						<option value={lease.id}>{lease.leaseNumber} · {lease.tenantName}</option>
					{/each}
				</select>
				<div class="grid grid-cols-2 gap-2">
					<input bind:value={paymentForm.amount} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Amount" />
					<input type="date" bind:value={paymentForm.dueDate} class="rounded border border-border bg-bg px-3 py-2 text-sm" />
				</div>
				<div class="grid grid-cols-2 gap-2">
					<select bind:value={paymentForm.type} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option>Rent</option><option>SecurityDeposit</option><option>LateFee</option><option>Utility</option><option>Other</option></select>
					<select bind:value={paymentForm.status} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option>Scheduled</option><option>Paid</option><option>Partial</option><option>Late</option><option>Waived</option></select>
				</div>
				<button onclick={submitPayment} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createPaymentMutation.isPending}>Save Payment</button>
			</div>
		</div>

		<div class="rounded-lg border border-border bg-surface p-4">
			<h2 class="mb-3 font-semibold">Add Expense</h2>
			<div class="space-y-2">
				<input bind:value={expenseForm.description} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Description" />
				<div class="grid grid-cols-2 gap-2">
					<input bind:value={expenseForm.amount} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Amount" />
					<input type="date" bind:value={expenseForm.incurredAt} class="rounded border border-border bg-bg px-3 py-2 text-sm" />
				</div>
				<div class="grid grid-cols-2 gap-2">
					<select bind:value={expenseForm.category} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option>Repairs</option><option>Utilities</option><option>Landscaping</option><option>Cleaning</option><option>Management</option><option>Other</option></select>
					<select bind:value={expenseForm.status} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option>Pending</option><option>Approved</option><option>Paid</option></select>
				</div>
				<div class="grid grid-cols-2 gap-2">
					<select bind:value={expenseForm.propertyId} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option value="">No property</option>{#each propertiesQuery.data || [] as property}<option value={property.id}>{property.name}</option>{/each}</select>
					<select bind:value={expenseForm.vendorId} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option value="">No vendor</option>{#each vendorsQuery.data || [] as vendor}<option value={vendor.id}>{vendor.name}</option>{/each}</select>
				</div>
				<button onclick={submitExpense} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createExpenseMutation.isPending}>Save Expense</button>
			</div>
		</div>
	</div>

	<div class="grid gap-4 lg:grid-cols-2">
		<div class="rounded-lg border border-border bg-surface">
			<div class="border-b border-border px-4 py-3 font-semibold">Payments</div>
			<div class="max-h-[40vh] overflow-y-auto p-3 space-y-2">
				{#each paymentsQuery.data || [] as payment}
					<div class="rounded border border-border bg-bg p-3 text-sm">
						<div class="flex items-center justify-between">
							<p class="font-medium">{payment.tenantName || payment.leaseNumber}</p>
							<span>{payment.status}</span>
						</div>
						<p class="text-xs text-text-secondary">{payment.type} · {money(payment.amount)} · Due {new Date(payment.dueDate).toLocaleDateString()}</p>
						{#if payment.status !== 'Paid'}
							<button class="mt-2 rounded border border-border px-2 py-1 text-xs" onclick={() => markPaidMutation.mutate(payment.id)}>Mark Paid</button>
						{/if}
					</div>
				{/each}
			</div>
		</div>

		<div class="rounded-lg border border-border bg-surface">
			<div class="border-b border-border px-4 py-3 font-semibold">Expenses</div>
			<div class="max-h-[40vh] overflow-y-auto p-3 space-y-2">
				{#each expensesQuery.data || [] as expense}
					<div class="rounded border border-border bg-bg p-3 text-sm">
						<div class="flex items-center justify-between">
							<p class="font-medium">{expense.description}</p>
							<span>{expense.status}</span>
						</div>
						<p class="text-xs text-text-secondary">{expense.category} · {money(expense.amount)} · {expense.propertyName || 'General'}</p>
					</div>
				{/each}
			</div>
		</div>
	</div>
</div>
