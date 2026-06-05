<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, Pencil, Save, Trash2, X, ReceiptText, Tags, FileText, ChevronDown } from '@lucide/svelte';
	import { expenses } from '$lib/api/endpoints/expenses';
	import { properties } from '$lib/api/endpoints/properties';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { expenseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
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

	// --- Scan extraction (saved in Expense.ReceiptData as a JSON string) ---
	// The scan→draft→confirm flow stashes the full extraction (line items, card,
	// payment method, document kind, vendor contact) here. We surface it as a
	// readable table + labeled fields; the raw expander below stays as the full
	// fallback. Parse defensively — the value may be empty, null, or malformed.
	type ScanLineItem = {
		description?: string | null;
		quantity?: number | null;
		unitPrice?: number | null;
		amount?: number | null;
	};
	type ScanReceipt = {
		documentKind?: string | null;
		paymentMethod?: string | null;
		cardLast4?: string | null;
		vendor?: {
			address?: string | null;
			phone?: string | null;
			website?: string | null;
			taxId?: string | null;
		} | null;
		lineItems?: ScanLineItem[] | null;
	};

	const scan = $derived.by<ScanReceipt | null>(() => {
		const raw = expense?.receiptData;
		if (!raw || typeof raw !== 'string' || raw.trim() === '') return null;
		try {
			const parsed = JSON.parse(raw);
			return parsed && typeof parsed === 'object' ? (parsed as ScanReceipt) : null;
		} catch {
			return null;
		}
	});

	const scanLineItems = $derived(
		(scan?.lineItems ?? []).filter(
			(li): li is ScanLineItem =>
				!!li &&
				typeof li === 'object' &&
				(!!li.description || li.amount != null || li.quantity != null || li.unitPrice != null)
		)
	);
	const scanLineItemsTotal = $derived(
		scanLineItems.reduce((sum, li) => sum + (typeof li.amount === 'number' ? li.amount : 0), 0)
	);
	// Only show a footer total when at least one line item carried a numeric amount,
	// so we never display a misleading "$0.00" when amounts were absent/non-numeric.
	const scanHasLineItemTotal = $derived(
		scanLineItems.some((li) => typeof li.amount === 'number' && !Number.isNaN(li.amount))
	);
	const scanHasFields = $derived(
		!!(
			scan &&
			(scan.cardLast4 ||
				scan.paymentMethod ||
				scan.documentKind ||
				scan.vendor?.phone ||
				scan.vendor?.address)
		)
	);

	const currencyFmt = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });
	const numberFmt = new Intl.NumberFormat('en-US');
	const usd = (n: number | null | undefined) =>
		typeof n === 'number' && !Number.isNaN(n) ? currencyFmt.format(n) : '';
	const qty = (n: number | null | undefined) =>
		typeof n === 'number' && !Number.isNaN(n) ? numberFmt.format(n) : '';

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

<!-- Inline date field: mirrors InlineField's edit/display structure (same testids) but
     uses the shared DatePicker when editing. value is bound `yyyy-MM-dd`. -->
{#snippet dateField(opts: {
	label: string;
	value: string;
	setValue: (v: string) => void;
	display: string;
	testid: string;
	error?: string;
})}
	<div data-testid={`${opts.testid}-field`}>
		<label class="mb-1 block text-xs font-medium text-muted-foreground" for={`${opts.testid}-input`}>{opts.label}</label>
		{#if editing}
			<DatePicker
				id={`${opts.testid}-input`}
				testid={`${opts.testid}-input`}
				value={opts.value}
				onchange={opts.setValue}
				placeholder={opts.label}
			/>
			{#if opts.error}<p class="mt-1 text-xs text-destructive" data-testid={`${opts.testid}-error`}>{opts.error}</p>{/if}
		{:else}
			<p
				class="min-h-10 rounded-md py-2 text-sm font-medium text-foreground"
				data-testid={`${opts.testid}-value`}
			>
				{opts.display === '' ? '-' : opts.display}
			</p>
		{/if}
	</div>
{/snippet}

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
		<div class="grid gap-6 lg:grid-cols-2">
			<DetailCard title="Expense" icon={ReceiptText} accent="primary" testid="expense-card-main" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Description" bind:value={form.description} display={expense.description} {editing} error={formErrors.description} testid="expense-detail-description" class="sm:col-span-2" />
				<InlineField label="Amount" bind:value={form.amount} display={`$${expense.amount}`} {editing} error={formErrors.amount} testid="expense-detail-amount" />
				<InlineField label="Status" bind:value={form.status} display={expense.status} {editing} type="select" options={statusOptions} testid="expense-detail-status" />
				{@render dateField({ label: 'Incurred date', value: form.incurredAt, setValue: (v) => (form.incurredAt = v), display: new Date(expense.incurredAt).toLocaleDateString(), error: formErrors.incurredAt, testid: 'expense-detail-incurred' })}
				{@render dateField({ label: 'Due date', value: form.dueDate, setValue: (v) => (form.dueDate = v), display: expense.dueDate ? new Date(expense.dueDate).toLocaleDateString() : '', testid: 'expense-detail-due-date' })}
				{@render dateField({ label: 'Paid date', value: form.paidAt, setValue: (v) => (form.paidAt = v), display: expense.paidAt ? new Date(expense.paidAt).toLocaleDateString() : '', testid: 'expense-detail-paid-date' })}
			</DetailCard>

			<DetailCard title="Categorization & references" icon={Tags} accent="muted" testid="expense-card-references" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Category" bind:value={form.category} display={expense.category} {editing} type="select" options={categoryOptions} testid="expense-detail-category" />
				<InlineField label="Property" bind:value={form.propertyId} display={expense.propertyName ?? 'General'} {editing} type="select" options={propertyOptions} testid="expense-detail-property" />
				<InlineField label="Vendor" bind:value={form.vendorId} display={expense.vendorName ?? 'No vendor'} {editing} type="select" options={vendorOptions} testid="expense-detail-vendor" />
				<div data-testid="expense-detail-billable-field">
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="expense-detail-billable-input">Billable to owner</label>
					{#if editing}
						<label class="flex h-10 items-center gap-2 rounded-md border border-border bg-background px-3 text-sm">
							<input id="expense-detail-billable-input" data-testid="expense-detail-billable-input" type="checkbox" bind:checked={form.billableToOwner} />
							<span>Billable</span>
						</label>
					{:else}
						<p class="min-h-10 rounded-md py-2 text-sm font-medium" data-testid="expense-detail-billable-value">
							{expense.billableToOwner ? 'Yes' : 'No'}
						</p>
					{/if}
				</div>
			</DetailCard>

			<DetailCard title="Receipt details" icon={FileText} accent="muted" testid="expense-card-receipt" class="lg:col-span-2" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Receipt subtotal" bind:value={form.subtotal} display={expense.subtotal} {editing} testid="expense-detail-subtotal" />
				<InlineField label="Receipt tax" bind:value={form.taxAmount} display={expense.taxAmount} {editing} testid="expense-detail-tax" />
				<InlineField label="Notes" bind:value={form.notes} display={expense.notes} {editing} type="textarea" testid="expense-detail-notes" class="sm:col-span-2" />

				<!-- Scan extraction, surfaced. The scan→draft→confirm flow already saved
				     line items + card/payment/document-kind/vendor into ReceiptData; we
				     present them as readable rows instead of burying them in raw JSON.
				     Each piece only renders when it actually has a value. -->
				{#if scanLineItems.length > 0}
					<div class="sm:col-span-2" data-testid="expense-detail-line-items">
						<p class="mb-1 text-xs font-medium text-muted-foreground">Line items</p>
						<div class="overflow-hidden rounded-md border border-border bg-background/40">
							<table class="w-full text-sm">
								<thead>
									<tr class="border-b border-border text-xs text-muted-foreground">
										<th class="px-3 py-2 text-left font-medium">Description</th>
										<th class="px-3 py-2 text-right font-medium">Qty</th>
										<th class="px-3 py-2 text-right font-medium">Unit price</th>
										<th class="px-3 py-2 text-right font-medium">Amount</th>
									</tr>
								</thead>
								<tbody>
									{#each scanLineItems as li}
										<tr class="border-b border-border/60 last:border-b-0">
											<td class="px-3 py-2 text-foreground">{li.description ?? '-'}</td>
											<td class="px-3 py-2 text-right font-mono tabular-nums text-muted-foreground">{qty(li.quantity) || '-'}</td>
											<td class="px-3 py-2 text-right font-mono tabular-nums text-muted-foreground">{usd(li.unitPrice) || '-'}</td>
											<td class="px-3 py-2 text-right font-mono tabular-nums font-medium text-foreground">{usd(li.amount) || '-'}</td>
										</tr>
									{/each}
								</tbody>
								<tfoot>
									<tr class="border-t border-border bg-muted/30">
										<td class="px-3 py-2 text-xs text-muted-foreground" colspan="3">
											{scanLineItems.length} {scanLineItems.length === 1 ? 'item' : 'items'}
										</td>
										<td class="px-3 py-2 text-right font-mono tabular-nums font-semibold text-foreground" data-testid="expense-detail-line-items-total">
											{scanHasLineItemTotal ? usd(scanLineItemsTotal) : ''}
										</td>
									</tr>
								</tfoot>
							</table>
						</div>
					</div>
				{/if}

				{#if scanHasFields}
					{#if scan?.cardLast4}
						<div data-testid="expense-detail-scan-card-last4-field">
							<p class="mb-1 block text-xs font-medium text-muted-foreground">Card last 4</p>
							<p class="min-h-10 rounded-md py-2 text-sm font-medium text-foreground" data-testid="expense-detail-scan-card-last4-value">•••• {scan.cardLast4}</p>
						</div>
					{/if}
					{#if scan?.paymentMethod}
						<div data-testid="expense-detail-scan-payment-method-field">
							<p class="mb-1 block text-xs font-medium text-muted-foreground">Payment method</p>
							<p class="min-h-10 rounded-md py-2 text-sm font-medium text-foreground" data-testid="expense-detail-scan-payment-method-value">{scan.paymentMethod}</p>
						</div>
					{/if}
					{#if scan?.documentKind}
						<div data-testid="expense-detail-scan-document-kind-field">
							<p class="mb-1 block text-xs font-medium text-muted-foreground">Document kind</p>
							<p class="min-h-10 rounded-md py-2 text-sm font-medium text-foreground" data-testid="expense-detail-scan-document-kind-value">{scan.documentKind}</p>
						</div>
					{/if}
					{#if scan?.vendor?.phone}
						<div data-testid="expense-detail-scan-vendor-phone-field">
							<p class="mb-1 block text-xs font-medium text-muted-foreground">Vendor phone</p>
							<p class="min-h-10 rounded-md py-2 text-sm font-medium text-foreground" data-testid="expense-detail-scan-vendor-phone-value">{scan.vendor.phone}</p>
						</div>
					{/if}
					{#if scan?.vendor?.address}
						<div class="sm:col-span-2" data-testid="expense-detail-scan-vendor-address-field">
							<p class="mb-1 block text-xs font-medium text-muted-foreground">Vendor address</p>
							<p class="min-h-10 rounded-md py-2 text-sm font-medium text-foreground" data-testid="expense-detail-scan-vendor-address-value">{scan.vendor.address}</p>
						</div>
					{/if}
				{/if}

				<!-- Raw receipt JSON is meaningless to a non-technical landlord, so it's tucked
				     behind a collapsed-by-default disclosure (same feel as the accounting report
				     expanders). It stays fully editable once opened. -->
				<details class="group rounded-md border border-border bg-background/40 sm:col-span-2">
					<summary
						class="flex cursor-pointer list-none items-center justify-between gap-3 px-3 py-2 text-sm font-medium text-foreground"
						data-testid="expense-detail-receipt-data-toggle"
					>
						<span>Receipt details (raw)</span>
						<ChevronDown class="h-4 w-4 shrink-0 text-muted-foreground transition-transform group-open:rotate-180" />
					</summary>
					<div class="border-t border-border px-3 py-3">
						<InlineField label="Receipt details JSON" bind:value={form.receiptData} display={expense.receiptData} {editing} type="textarea" testid="expense-detail-receipt-data" />
					</div>
				</details>
			</DetailCard>
		</div>
	{/if}
</div>
