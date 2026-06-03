<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, Pencil, Save, Trash2, X } from '@lucide/svelte';
	import { expenses } from '$lib/api/endpoints/expenses';
	import { properties } from '$lib/api/endpoints/properties';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { expenseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';

	const queryClient = useQueryClient();
	const expenseId = $derived(parseInt($page.params.id ?? '0', 10));
	const portfolioId = $derived(getCurrentPortfolioId());
	const STATUSES = ['Pending', 'Approved', 'Paid', 'Rejected', 'Draft'];
	const CATEGORIES = ['Advertising', 'AutoTravel', 'CleaningMaintenance', 'Commissions', 'Insurance', 'LegalProfessional', 'ManagementFees', 'MortgageInterest', 'Repairs', 'Supplies', 'Taxes', 'Utilities', 'Depreciation', 'Other'];

	let editing = $state(false);
	let form = $state({
		description: '',
		amount: '',
		incurredAt: '',
		category: 'Repairs',
		status: 'Pending',
		propertyId: '',
		vendorId: '',
		dueDate: '',
		paidAt: '',
		billableToOwner: false,
		notes: '',
		subtotal: '',
		taxAmount: '',
		receiptData: ''
	});
	let formErrors = $state<Record<string, string>>({});

	const expenseQuery = createQuery(() => ({ queryKey: ['expense', expenseId], queryFn: () => expenses.get(expenseId), enabled: expenseId > 0 }));
	const propertiesQuery = createQuery(() => ({ queryKey: ['properties', portfolioId], queryFn: () => properties.list(portfolioId, { take: 200 }) }));
	const vendorsQuery = createQuery(() => ({ queryKey: ['vendors', portfolioId], queryFn: () => vendors.list(portfolioId, { take: 200 }) }));

	const expense = $derived(expenseQuery.data);
	const statusOptions = $derived(STATUSES.map((value) => ({ value, label: value })));
	const categoryOptions = $derived(CATEGORIES.map((value) => ({ value, label: value })));
	const propertyOptions = $derived([{ value: '', label: 'No property' }, ...(propertiesQuery.data ?? []).map((p) => ({ value: String(p.id), label: p.name }))]);
	const vendorOptions = $derived([{ value: '', label: 'No vendor' }, ...(vendorsQuery.data ?? []).map((v) => ({ value: String(v.id), label: v.name }))]);

	function startEditing() {
		if (!expense) return;
		form = {
			description: expense.description,
			amount: String(expense.amount),
			incurredAt: expense.incurredAt?.slice(0, 10) ?? '',
			category: expense.category,
			status: expense.status,
			propertyId: expense.propertyId != null ? String(expense.propertyId) : '',
			vendorId: expense.vendorId != null ? String(expense.vendorId) : '',
			dueDate: expense.dueDate?.slice(0, 10) ?? '',
			paidAt: expense.paidAt?.slice(0, 10) ?? '',
			billableToOwner: expense.billableToOwner,
			notes: expense.notes ?? '',
			subtotal: expense.subtotal != null ? String(expense.subtotal) : '',
			taxAmount: expense.taxAmount != null ? String(expense.taxAmount) : '',
			receiptData: expense.receiptData ?? ''
		};
		formErrors = {};
		editing = true;
	}

	function cancelEditing() {
		editing = false;
		formErrors = {};
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => expenses.update(expenseId, data),
		onSuccess: () => {
			showSuccess('Expense updated.');
			editing = false;
			queryClient.invalidateQueries({ queryKey: ['expense', expenseId] });
			queryClient.invalidateQueries({ queryKey: ['expenses', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['accounting-summary', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function saveExpense() {
		const result = parseForm(expenseSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data });
	}

	const deleteMutation = createMutation(() => ({
		mutationFn: () => expenses.delete(expenseId),
		onSuccess: () => {
			showSuccess('Expense deleted.');
			goto('/accounting');
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));
</script>

<svelte:head>
	<title>{expense?.description ?? 'Expense'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="expense-detail-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-3">
		<div class="min-w-0">
			<Button variant="ghost" href="/accounting" class="mb-2 -ml-3"><ArrowLeft class="h-4 w-4" />Accounting</Button>
			<h1 class="truncate text-2xl font-bold">{expense?.description ?? 'Expense'}</h1>
			<p class="text-sm text-muted-foreground">{expense ? `${expense.category} · $${expense.amount} · ${expense.status}` : ''}</p>
		</div>
		{#if expense}
			<div class="flex gap-2">
				{#if editing}
					<Button variant="outline" onclick={cancelEditing} disabled={saveMutation.isPending}><X class="h-4 w-4" />Cancel</Button>
					<Button onclick={saveExpense} disabled={saveMutation.isPending}><Save class="h-4 w-4" />{saveMutation.isPending ? 'Saving...' : 'Save'}</Button>
				{:else}
					<Button variant="outline" onclick={startEditing}><Pencil class="h-4 w-4" />Edit</Button>
					<Button variant="destructive" onclick={() => deleteMutation.mutate()} disabled={deleteMutation.isPending}><Trash2 class="h-4 w-4" />Delete</Button>
				{/if}
			</div>
		{/if}
	</div>

	{#if expenseQuery.isLoading}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Loading expense...</div>
	{:else if !expense}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Expense not found.</div>
	{:else}
		<Card.Root>
			<Card.Header>
				<Card.Title>Expense Details</Card.Title>
				<Card.Description>Schedule E category, vendor/property references, and receipt metadata.</Card.Description>
			</Card.Header>
			<Card.Content>
				<div class="grid gap-4 md:grid-cols-3">
					<InlineField label="Description" bind:value={form.description} display={expense.description} {editing} onedit={startEditing} error={formErrors.description} testid="expense-detail-description" class="md:col-span-2" />
					<InlineField label="Amount" bind:value={form.amount} display={`$${expense.amount}`} {editing} onedit={startEditing} error={formErrors.amount} testid="expense-detail-amount" />
					<InlineField label="Incurred date" bind:value={form.incurredAt} display={new Date(expense.incurredAt).toLocaleDateString()} {editing} onedit={startEditing} type="date" error={formErrors.incurredAt} testid="expense-detail-incurred" />
					<InlineField label="Due date" bind:value={form.dueDate} display={expense.dueDate ? new Date(expense.dueDate).toLocaleDateString() : ''} {editing} onedit={startEditing} type="date" testid="expense-detail-due-date" />
					<InlineField label="Paid date" bind:value={form.paidAt} display={expense.paidAt ? new Date(expense.paidAt).toLocaleDateString() : ''} {editing} onedit={startEditing} type="date" testid="expense-detail-paid-date" />
					<InlineField label="Category" bind:value={form.category} display={expense.category} {editing} onedit={startEditing} type="select" options={categoryOptions} testid="expense-detail-category" />
					<InlineField label="Status" bind:value={form.status} display={expense.status} {editing} onedit={startEditing} type="select" options={statusOptions} testid="expense-detail-status" />
					<InlineField label="Property" bind:value={form.propertyId} display={expense.propertyName ?? 'General'} {editing} onedit={startEditing} type="select" options={propertyOptions} testid="expense-detail-property" />
					<InlineField label="Vendor" bind:value={form.vendorId} display={expense.vendorName ?? 'No vendor'} {editing} onedit={startEditing} type="select" options={vendorOptions} testid="expense-detail-vendor" />
					<InlineField label="Receipt subtotal" bind:value={form.subtotal} display={expense.subtotal} {editing} onedit={startEditing} testid="expense-detail-subtotal" />
					<InlineField label="Receipt tax" bind:value={form.taxAmount} display={expense.taxAmount} {editing} onedit={startEditing} testid="expense-detail-tax" />
					<div data-testid="expense-detail-billable-field">
						<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-detail-billable-input">Billable to owner</label>
						{#if editing}
							<label class="flex h-10 items-center gap-2 rounded-md border border-border bg-background px-3 text-sm">
								<input id="expense-detail-billable-input" data-testid="expense-detail-billable-input" type="checkbox" bind:checked={form.billableToOwner} />
								<span>Billable</span>
							</label>
						{:else}
							<button
								type="button"
								class="min-h-10 w-full rounded-md border border-transparent py-2 text-left text-sm font-medium transition-colors hover:border-border hover:bg-muted/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/30"
								onclick={startEditing}
							>
								{expense.billableToOwner ? 'Yes' : 'No'}
							</button>
						{/if}
					</div>
					<InlineField label="Notes" bind:value={form.notes} display={expense.notes} {editing} onedit={startEditing} type="textarea" testid="expense-detail-notes" class="md:col-span-3" />
					<InlineField label="Receipt details JSON" bind:value={form.receiptData} display={expense.receiptData} {editing} onedit={startEditing} type="textarea" testid="expense-detail-receipt-data" class="md:col-span-3" />
				</div>
			</Card.Content>
		</Card.Root>
	{/if}
</div>
