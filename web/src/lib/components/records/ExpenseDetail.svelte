<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { Pencil, Save, Trash2, X, ReceiptText, Tags, FileText, ChevronDown, Plus } from '@lucide/svelte';
	import { expenses } from '$lib/api/endpoints/expenses';
	import { properties } from '$lib/api/endpoints/properties';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { expenseDetailSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import {
		EXPENSE_CATEGORY_OPTIONS,
		formatExpenseCategory
	} from '$lib/accounting/expense-categories';
	import { formatExpenseMoney, receiptGrandTotal } from '$lib/accounting/expense-receipt-display';
	import {
		confirmExpenseDelete,
		expenseDeleteConfirmMessage,
		formatExpenseUnitReference,
		requestExpenseDelete,
		type ExpenseDeleteTarget
	} from '$lib/accounting/expense-detail-actions';
	import { buildExpenseReceiptDataForSave } from '$lib/accounting/expense-receipt-data';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Table from '$lib/components/ui/table';
	import { isMismatchedUnitSelection } from '$lib/unit/unit-membership-guard';

	let {
		expenseId,
		onDeleted,
		expectedUnitId,
		onUnitMismatch,
	}: {
		expenseId: number;
		onDeleted: () => void;
		expectedUnitId?: number;
		onUnitMismatch?: () => void;
	} = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const STATUSES = ['Pending', 'Approved', 'Paid', 'Rejected', 'Draft'];

	let editing = $state(false);
	let deleteTarget = $state<ExpenseDeleteTarget | null>(null);
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

	// --- Editable line items (mirrors the scan review page pattern) ---
	// Numeric cells kept as strings; parsed on save. Key is a stable client-side id
	// so add/remove don't mis-bind the {#each} rows.
	interface EditableLineItem {
		key: number;
		description: string;
		quantity: string;
		unitPrice: string;
		amount: string;
	}
	let editedLineItems = $state<EditableLineItem[]>([]);
	let lineItemKeySeq = 0;

	// Parse a money/number string ("$1,234.50") → number or null.
	function parseAmount(raw: string | undefined | null): number | null {
		if (raw == null) return null;
		const cleaned = String(raw).replace(/[^0-9.\-]/g, '');
		if (cleaned === '' || cleaned === '-' || cleaned === '.') return null;
		const n = Number(cleaned);
		return Number.isFinite(n) ? n : null;
	}

	function addLineItem() {
		editedLineItems = [
			...editedLineItems,
			{ key: lineItemKeySeq++, description: '', quantity: '', unitPrice: '', amount: '' }
		];
	}

	function removeLineItem(key: number) {
		editedLineItems = editedLineItems.filter((li) => li.key !== key);
	}

	// Running total of per-row amount inputs — echoed below the table.
	const editLineItemsTotal = $derived(
		editedLineItems.reduce((sum, li) => sum + (parseAmount(li.amount) ?? 0), 0)
	);
	const editLineItemsHaveAmounts = $derived(
		editedLineItems.some((li) => parseAmount(li.amount) != null)
	);

	const expenseQuery = createQuery(() => ({ queryKey: ['expense', expenseId], queryFn: () => expenses.get(expenseId), enabled: expenseId > 0 }));
	const propertiesQuery = createQuery(() => ({ queryKey: ['properties', portfolioId], queryFn: () => properties.list(portfolioId, { take: 200 }) }));
	const vendorsQuery = createQuery(() => ({ queryKey: ['vendors', portfolioId], queryFn: () => vendors.list(portfolioId, { take: 200 }) }));

	const expense = $derived(expenseQuery.data);

	$effect(() => {
		if (isMismatchedUnitSelection(expense, expectedUnitId)) onUnitMismatch?.();
	});

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

	// Read-mode line items: prefer typed rows from the API (expense.lineItems),
	// fall back to receiptData.lineItems for expenses created before typed rows existed.
	const readLineItems = $derived.by<ScanLineItem[]>(() => {
		const typed = expense?.lineItems;
		if (typed && typed.length > 0) {
			return typed.map((li) => ({
				description: li.description,
				quantity: li.quantity,
				unitPrice: li.unitPrice,
				amount: li.amount,
			}));
		}
		// Fallback: receipData.lineItems for legacy expenses
		return (scan?.lineItems ?? []).filter(
			(li): li is ScanLineItem =>
				!!li &&
				typeof li === 'object' &&
				(!!li.description || li.amount != null || li.quantity != null || li.unitPrice != null)
		);
	});

	const readLineItemsTotal = $derived(
		readLineItems.reduce((sum, li) => sum + (typeof li.amount === 'number' ? li.amount : 0), 0)
	);
	const readHasLineItemTotal = $derived(
		readLineItems.some((li) => typeof li.amount === 'number' && !Number.isNaN(li.amount))
	);
	const receiptGrandTotalValue = $derived(expense ? receiptGrandTotal(expense) : 0);
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
	function formatMoney(val: number): string {
		return val.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
	}

	const statusOptions = $derived(STATUSES.map((value) => ({ value, label: value })));
	const categoryOptions = $derived(EXPENSE_CATEGORY_OPTIONS);
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
		// Seed editable line items from typed rows, falling back to receiptData.lineItems.
		lineItemKeySeq = 0;
		const source = readLineItems;
		editedLineItems = source.map((li) => ({
			key: lineItemKeySeq++,
			description: li.description ?? '',
			quantity: li.quantity != null ? String(li.quantity) : '',
			unitPrice: li.unitPrice != null ? String(li.unitPrice) : '',
			amount: li.amount != null ? String(li.amount) : '',
		}));
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
		const result = parseForm(expenseDetailSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};

		// Build the line items payload: parse numeric strings, drop fully-empty rows.
		const lineItems = editedLineItems
			.map((li) => ({
				description: li.description.trim(),
				quantity: parseAmount(li.quantity),
				unitPrice: parseAmount(li.unitPrice),
				amount: parseAmount(li.amount),
			}))
			.filter(
				(li) =>
					li.description !== '' ||
					li.quantity != null ||
					li.unitPrice != null ||
					li.amount != null
			);

		const receiptPayload = buildExpenseReceiptDataForSave({
			rawReceiptData: form.receiptData,
			lineItems: editedLineItems,
		});
		saveMutation.mutate({ portfolioId, ...result.data, ...receiptPayload, lineItems });
	}

	const deleteMutation = createMutation(() => ({
		mutationFn: () => expenses.delete(expenseId),
		onSuccess: () => {
			showSuccess('Expense deleted.');
			deleteTarget = null;
			onDeleted();
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
			<h1 class="truncate text-2xl font-bold">{expense?.description ?? 'Expense'}</h1>
			<p class="text-sm text-muted-foreground">{expense ? `${formatExpenseCategory(expense.category)} · $${expense.amount} · ${expense.status}` : ''}</p>
		</div>
		{#if expense}
			<div class="flex gap-2">
				{#if editing}
					<Button variant="outline" onclick={cancelEditing} disabled={saveMutation.isPending}><X class="h-4 w-4" />Cancel</Button>
					<Button onclick={saveExpense} disabled={saveMutation.isPending}><Save class="h-4 w-4" />{saveMutation.isPending ? 'Saving...' : 'Save'}</Button>
				{:else}
					<Button variant="outline" onclick={startEditing}><Pencil class="h-4 w-4" />Edit</Button>
					<Button variant="destructive" onclick={() => (deleteTarget = requestExpenseDelete(expense))} disabled={deleteMutation.isPending}><Trash2 class="h-4 w-4" />Delete</Button>
				{/if}
			</div>
		{/if}
	</div>

	{#if editing && Object.keys(formErrors).length > 0}
		<!-- Belt-and-suspenders: if Save fails validation on a field whose inline control is
		     hidden or unwired, this banner guarantees the user sees *something* — Save can
		     never silently no-op (TSK-198). -->
		<div
			class="mb-4 rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive"
			data-testid="expense-detail-form-errors"
			role="alert"
		>
			<p class="font-medium">Please fix the highlighted fields before saving:</p>
			<ul class="mt-1 list-disc pl-5">
				{#each Object.values(formErrors) as message}
					<li>{message}</li>
				{/each}
			</ul>
		</div>
	{/if}

	{#if expenseQuery.isLoading}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Loading expense...</div>
	{:else if !expense}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Expense not found.</div>
	{:else}
		<div class="grid gap-6 lg:grid-cols-2">
			<DetailCard title="Expense" icon={ReceiptText} accent="primary" testid="expense-card-main" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Description" bind:value={form.description} display={expense.description} {editing} error={formErrors.description} testid="expense-detail-description" class="sm:col-span-2" />
				<InlineField label="Amount" bind:value={form.amount} display={formatExpenseMoney(expense.amount)} {editing} type="number" error={formErrors.amount} testid="expense-detail-amount" />
				<InlineField label="Status" bind:value={form.status} display={expense.status} {editing} type="select" options={statusOptions} testid="expense-detail-status" />
				{@render dateField({ label: 'Incurred date', value: form.incurredAt, setValue: (v) => (form.incurredAt = v), display: formatDateOnly(expense.incurredAt), error: formErrors.incurredAt, testid: 'expense-detail-incurred' })}
				{@render dateField({ label: 'Due date', value: form.dueDate, setValue: (v) => (form.dueDate = v), display: expense.dueDate ? formatDateOnly(expense.dueDate) : '', testid: 'expense-detail-due-date' })}
				{@render dateField({ label: 'Paid date', value: form.paidAt, setValue: (v) => (form.paidAt = v), display: expense.paidAt ? formatDateOnly(expense.paidAt) : '', testid: 'expense-detail-paid-date' })}
			</DetailCard>

			<DetailCard title="Categorization & references" icon={Tags} accent="muted" testid="expense-card-references" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="Category" bind:value={form.category} display={formatExpenseCategory(expense.category)} {editing} type="select" options={categoryOptions} testid="expense-detail-category" />
				<InlineField label="Property" bind:value={form.propertyId} display={expense.propertyName ?? 'General'} {editing} type="select" options={propertyOptions} testid="expense-detail-property" />
				<div data-testid="expense-detail-unit-field">
					<p class="mb-1 text-xs font-medium text-muted-foreground">Unit</p>
					{#if expense.unitId}
						<a
							href="/units/{expense.unitId}?tab=expenses"
							class="block min-h-10 rounded-md py-2 text-sm font-medium text-primary underline-offset-4 hover:underline"
							data-testid="expense-detail-unit-link"
						>
							{formatExpenseUnitReference(expense)}
						</a>
					{:else}
						<p class="min-h-10 rounded-md py-2 text-sm font-medium text-foreground" data-testid="expense-detail-unit-value">
							{formatExpenseUnitReference(expense)}
						</p>
					{/if}
				</div>
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

			<!-- The original scanned document this expense was created from. The scan→draft→confirm
			     flow re-keys the uploaded StoredFile to this expense, so the API can serve it at
			     /expenses/{id}/receipt; we reach it through the same-origin /expense-file proxy
			     (cookie→bearer) so the <img>/link is authenticated. Renders only when a file exists. -->
			{#if expense.hasReceipt}
				<DetailCard title="Scanned document" icon={ReceiptText} accent="muted" testid="expense-card-scanned-document" class="lg:col-span-2">
					<div class="flex flex-col items-start gap-2">
						<p class="text-xs text-muted-foreground">The original document this expense was created from.</p>
						{#if expense.receiptIsImage}
							<a
								href="/expense-file/{expense.id}"
								target="_blank"
								rel="noopener noreferrer"
								data-testid="expense-detail-scanned-document-link"
								aria-label="View scanned document full size"
								class="group inline-block"
							>
								<img
									src="/expense-file/{expense.id}?thumb=true"
									alt="Scanned document preview"
									class="max-h-80 w-auto rounded-md border border-border object-contain transition group-hover:ring-2 group-hover:ring-primary"
									loading="lazy"
								/>
								<span class="mt-1 block text-xs text-primary underline underline-offset-2 group-hover:text-primary/80">Open full size</span>
							</a>
						{:else}
							<a
								href="/expense-file/{expense.id}"
								target="_blank"
								rel="noopener noreferrer"
								data-testid="expense-detail-scanned-document-link"
								class="inline-flex items-center gap-2 rounded-md border border-border bg-background px-4 py-3 text-sm font-medium text-foreground transition hover:border-primary hover:text-primary"
							>
								<FileText class="h-5 w-5 shrink-0" />
								<span>View scanned document</span>
							</a>
						{/if}
					</div>
				</DetailCard>
			{/if}

			<DetailCard title="Receipt details" icon={FileText} accent="muted" testid="expense-card-receipt" class="lg:col-span-2" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-3">
				<InlineField label="Receipt subtotal" bind:value={form.subtotal} display={expense.subtotal != null ? formatExpenseMoney(expense.subtotal) : ''} {editing} type="number" error={formErrors.subtotal} testid="expense-detail-subtotal" />
				<InlineField label="Receipt tax" bind:value={form.taxAmount} display={expense.taxAmount != null ? formatExpenseMoney(expense.taxAmount) : ''} {editing} type="number" error={formErrors.taxAmount} testid="expense-detail-tax" />
				<div data-testid="expense-detail-receipt-amount-field">
					<p class="mb-1 block text-xs font-medium text-muted-foreground">Amount</p>
					<p
						class="min-h-10 rounded-md bg-primary/10 px-3 py-2 font-mono text-sm font-semibold tabular-nums text-foreground"
						data-testid="expense-detail-receipt-amount"
					>
						{formatExpenseMoney(receiptGrandTotalValue)}
					</p>
				</div>
				<InlineField label="Notes" bind:value={form.notes} display={expense.notes} {editing} type="textarea" testid="expense-detail-notes" class="sm:col-span-3" />

				<!-- Line items: read-only table in view mode, editable table with add/remove in edit mode. -->
				<div class="sm:col-span-3" data-testid="expense-detail-line-items">
					{#if editing}
						<!-- Edit mode: editable line items (mirrors scan review page) -->
						<div class="mb-2 flex items-center justify-between">
							<p class="text-xs font-medium text-muted-foreground">Line items</p>
							<Button
								type="button"
								variant="ghost"
								size="sm"
								class="h-7 gap-1 px-2 text-xs"
								data-testid="expense-detail-line-item-add"
								onclick={addLineItem}
							>
								<Plus class="h-3 w-3" /> Add line item
							</Button>
						</div>
						{#if editedLineItems.length === 0}
							<p class="text-sm text-muted-foreground">
								No line items.
								<button type="button" class="text-accent underline-offset-2 hover:underline" onclick={addLineItem}>Add one</button>
								if the receipt itemizes charges.
							</p>
						{:else}
							<div class="overflow-hidden rounded-md border border-border">
								<Table.Root>
									<Table.Header>
										<Table.Row class="bg-muted/40">
											<Table.Head class="px-3 py-2 text-xs">Description</Table.Head>
											<Table.Head class="w-16 px-2 py-2 text-right text-xs">Qty</Table.Head>
											<Table.Head class="w-24 px-2 py-2 text-right text-xs">Unit price</Table.Head>
											<Table.Head class="w-24 px-2 py-2 text-right text-xs">Amount</Table.Head>
											<Table.Head class="w-9 px-1 py-2"><span class="sr-only">Remove</span></Table.Head>
										</Table.Row>
									</Table.Header>
									<Table.Body>
										{#each editedLineItems as item, i (item.key)}
											<Table.Row class={i % 2 === 1 ? 'bg-muted/20' : ''}>
												<Table.Cell class="px-2 py-1.5">
													<Input
														type="text"
														placeholder="Description"
														bind:value={item.description}
														data-testid="expense-detail-line-item-description-{i}"
														class="h-8 text-sm"
													/>
												</Table.Cell>
												<Table.Cell class="px-2 py-1.5">
													<Input
														type="text"
														inputmode="decimal"
														mask="decimal"
														bind:value={item.quantity}
														data-testid="expense-detail-line-item-quantity-{i}"
														class="h-8 text-right font-mono text-sm tabular-nums"
													/>
												</Table.Cell>
												<Table.Cell class="px-2 py-1.5">
													<Input
														type="text"
														inputmode="decimal"
														mask="currency"
														bind:value={item.unitPrice}
														data-testid="expense-detail-line-item-unit-price-{i}"
														class="h-8 text-right font-mono text-sm tabular-nums"
													/>
												</Table.Cell>
												<Table.Cell class="px-2 py-1.5">
													<Input
														type="text"
														inputmode="decimal"
														mask="currency"
														bind:value={item.amount}
														data-testid="expense-detail-line-item-amount-{i}"
														class="h-8 text-right font-mono text-sm tabular-nums"
													/>
												</Table.Cell>
												<Table.Cell class="px-1 py-1.5 text-center">
													<button
														type="button"
														onclick={() => removeLineItem(item.key)}
														data-testid="expense-detail-line-item-remove-{i}"
														class="text-muted-foreground transition-colors hover:text-destructive"
														aria-label="Remove line item {i + 1}"
													>✕</button>
												</Table.Cell>
											</Table.Row>
										{/each}
									</Table.Body>
								</Table.Root>
							</div>
							<!-- Running total echo -->
							<div class="mt-2 flex items-center justify-between px-1 text-xs">
								<span class="text-muted-foreground">Line items total</span>
								<span
									class="font-mono tabular-nums text-foreground"
									data-testid="expense-detail-line-items-edit-total"
								>{editLineItemsHaveAmounts ? `$${formatMoney(editLineItemsTotal)}` : ''}</span>
							</div>
						{/if}
					{:else}
						<!-- Read mode: read-only table (same as before, sourced from typed rows + fallback) -->
						{#if readLineItems.length > 0}
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
										{#each readLineItems as li}
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
												{readLineItems.length} {readLineItems.length === 1 ? 'item' : 'items'} subtotal
											</td>
											<td class="px-3 py-2 text-right font-mono tabular-nums font-semibold text-foreground" data-testid="expense-detail-line-items-total">
												{readHasLineItemTotal ? usd(readLineItemsTotal) : ''}
											</td>
										</tr>
										<tr class="border-t border-border bg-primary/10">
											<td class="px-3 py-2 text-xs font-semibold text-foreground" colspan="3">
												Grand total
											</td>
											<td class="px-3 py-2 text-right font-mono tabular-nums font-bold text-foreground" data-testid="expense-detail-line-items-grand-total">
												{formatExpenseMoney(receiptGrandTotalValue)}
											</td>
										</tr>
									</tfoot>
								</table>
							</div>
						{/if}
					{/if}
				</div>

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

		<!-- Per-record audit history -->
		<div class="mt-6 rounded-lg border border-border bg-card p-4" data-testid="expense-history-section">
			<h2 class="mb-1 text-base font-semibold">History</h2>
			<p class="mb-3 text-sm text-muted-foreground">Every recorded change to this expense — who, what, and when.</p>
			<RecordHistory entityType="Expense" entityId={expenseId} />
		</div>
	{/if}
</div>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete expense"
	message={expenseDeleteConfirmMessage(deleteTarget)}
	busy={deleteMutation.isPending}
	testid="expense-detail-delete"
	onconfirm={() => confirmExpenseDelete(deleteTarget, () => deleteMutation.mutate())}
	oncancel={() => (deleteTarget = null)}
/>
