<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { toast } from 'svelte-sonner';
	import { scan, type ScanFieldDto } from '$lib/api/scan';
	import * as Card from '$lib/components/ui/card';
	import { Badge } from '$lib/components/ui/badge';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Table from '$lib/components/ui/table';

	// ScheduleECategory enum values (mirrors RentalCommand.Core.Enums.ScheduleECategory)
	const SCHEDULE_E_CATEGORIES = [
		'Advertising',
		'AutoTravel',
		'CleaningMaintenance',
		'Commissions',
		'Insurance',
		'LegalProfessional',
		'ManagementFees',
		'MortgageInterest',
		'Repairs',
		'Supplies',
		'Taxes',
		'Utilities',
		'Depreciation',
		'Other'
	] as const;

	// Grouped scalar field definitions (in display order within each group)
	const FIELD_GROUPS: { label: string; fields: string[] }[] = [
		{
			label: 'Vendor',
			fields: ['vendor_name', 'vendor_address', 'vendor_phone', 'vendor_website', 'vendor_tax_id', 'receipt_number']
		},
		{
			label: 'Amounts',
			fields: ['subtotal', 'tax', 'tax_rate', 'tip', 'discount', 'shipping', 'total', 'payment_method', 'card_last4', 'due_date']
		},
		{
			label: 'Details',
			fields: ['document_kind', 'category', 'notes']
		}
	];

	// All known scalar field names (excluding line_items)
	const KNOWN_SCALAR_FIELDS = new Set(FIELD_GROUPS.flatMap((g) => g.fields));

	const LINE_ITEMS_FIELD = 'line_items';

	interface LineItem {
		description?: string | null;
		quantity?: number | null;
		unit_price?: number | null;
		amount?: number | null;
	}

	const queryClient = useQueryClient();

	const draftId = $derived(parseInt($page.params.draftId ?? '0', 10));

	const draftQuery = createQuery(() => ({
		queryKey: ['scan', draftId],
		queryFn: () => scan.get(draftId),
		// Bounded poll: only while Pending (SignalR flips to Reviewing; poll is a fallback)
		refetchInterval: (q) => (q.state.data?.status === 'Pending' ? 1500 : false)
	}));

	const data = $derived(draftQuery.data);
	const isScanLocked = $derived(data?.status === 'Confirmed' || data?.status === 'Rejected');

	// Editable field values (keyed by field name, scalars only)
	let editedFields = $state<Record<string, string>>({});

	// Paid / Unpaid toggle — true = already paid (receipt), false = unpaid bill
	let isPaid = $state(true);
	let isPaidInitialized = $state(false);

	function defaultIsPaidFromKind(kind: string | undefined): boolean {
		if (!kind) return true;
		return !['Bill', 'Invoice', 'UtilityBill', 'PropertyTax'].includes(kind);
	}

	// Initialize editable fields when data arrives
	$effect(() => {
		if (data?.fields) {
			const initial: Record<string, string> = {};
			for (const f of data.fields) {
				if (f.name === LINE_ITEMS_FIELD) continue;
				if (!(f.name in editedFields)) {
					initial[f.name] = f.value;
				}
			}
			if (Object.keys(initial).length > 0) {
				editedFields = { ...editedFields, ...initial };
			}
			// Initialize isPaid from document_kind once on first data arrival
			if (!isPaidInitialized) {
				const kindField = data.fields.find((f) => f.name === 'document_kind');
				isPaid = defaultIsPaidFromKind(kindField?.value);
				isPaidInitialized = true;
			}
		}
	});

	// Svelte action to focus the first low-confidence field
	let firstLowFocused = $state(false);
	function focusFirstLow(node: HTMLElement, isFirstLow: boolean) {
		if (isFirstLow && !firstLowFocused) {
			firstLowFocused = true;
			// defer to next tick so the DOM is ready
			setTimeout(() => node.focus(), 0);
		}
		return {};
	}

	function confidenceLevel(confidence: number): 'high' | 'medium' | 'low' {
		if (confidence >= 0.8) return 'high';
		if (confidence >= 0.5) return 'medium';
		return 'low';
	}

	function confidenceLabel(confidence: number): string {
		const level = confidenceLevel(confidence);
		if (level === 'medium') return 'Medium confidence';
		if (level === 'low') return 'Low confidence';
		return '';
	}

	// User-facing status wording. "Reviewing" really means "waiting for you to review".
	function statusLabel(s: string): string {
		switch (s) {
			case 'Pending': return 'Processing';
			case 'Reviewing': return 'Ready to review';
			case 'Confirmed': return 'Confirmed';
			case 'Failed': return 'Extraction failed';
			case 'Rejected': return 'Rejected';
			default: return s;
		}
	}

	function statusBadgeClass(status: string): string {
		switch (status) {
			case 'Pending':
				return 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300 border-amber-200 dark:border-amber-800';
			case 'Reviewing':
				return 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300 border-blue-200 dark:border-blue-800';
			case 'Confirmed':
				return 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300 border-green-200 dark:border-green-800';
			case 'Failed':
			case 'Rejected':
				return 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300 border-red-200 dark:border-red-800';
			default:
				return '';
		}
	}

	function fieldInputClass(field: ScanFieldDto): string {
		const level = confidenceLevel(field.confidence);
		// Extra border colour classes layered on top of the Input component's base styles
		if (level === 'low') return 'border-red-400 dark:border-red-500';
		if (level === 'medium') return 'text-muted-foreground';
		return '';
	}

	function isFirstLowField(fields: ScanFieldDto[], field: ScanFieldDto): boolean {
		const firstLow = fields.filter((f) => f.name !== LINE_ITEMS_FIELD).find((f) => confidenceLevel(f.confidence) === 'low');
		return firstLow?.name === field.name;
	}

	// Build grouped sections from the extracted fields
	function buildGroups(fields: ScanFieldDto[]): { label: string; fields: ScanFieldDto[] }[] {
		const fieldMap = new Map(fields.filter((f) => f.name !== LINE_ITEMS_FIELD).map((f) => [f.name, f]));
		const result: { label: string; fields: ScanFieldDto[] }[] = [];

		for (const group of FIELD_GROUPS) {
			const present = group.fields.map((name) => fieldMap.get(name)).filter((f): f is ScanFieldDto => !!f);
			if (present.length > 0) {
				result.push({ label: group.label, fields: present });
			}
		}

		// "Other" group: any scalar fields not in KNOWN_SCALAR_FIELDS
		const otherFields = [...fieldMap.values()].filter((f) => !KNOWN_SCALAR_FIELDS.has(f.name));
		if (otherFields.length > 0) {
			result.push({ label: 'Other', fields: otherFields });
		}

		return result;
	}

	// Parse line_items field value
	function parseLineItems(fields: ScanFieldDto[]): LineItem[] {
		const f = fields.find((f) => f.name === LINE_ITEMS_FIELD);
		if (!f?.value) return [];
		try {
			const parsed = JSON.parse(f.value);
			if (Array.isArray(parsed)) return parsed as LineItem[];
			return [];
		} catch {
			return [];
		}
	}

	function formatMoney(val: number | null | undefined): string {
		if (val == null) return '';
		return val.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
	}

	function formatQty(val: number | null | undefined): string {
		if (val == null) return '';
		return String(val);
	}

	const isImage = $derived(() => {
		const url = data?.fileUrl ?? '';
		// Heuristic: if the file URL is for an image content type
		// We check the field list for a hint, otherwise rely on extension
		// The API serves /api/v1/scans/{id}/file — we use an img tag and let it fail gracefully
		return true; // default to image; <img> won't crash on PDFs
	});

	// Preview goes through a same-origin, cookie-authed SvelteKit route (the API's
	// /file endpoint needs a JWT bearer an <img>/<iframe> can't send).
	const fileUrl = $derived(data ? `/scan-file/${data.id}` : '');
	const linkedExpenseId = $derived(data?.createdEntityType === 'Expense' ? data.createdEntityId : null);
	const linkedExpenseHref = $derived(linkedExpenseId ? `/accounting/expenses/${linkedExpenseId}` : null);

	// Confirm mutation
	const confirmMutation = createMutation(() => ({
		mutationFn: () => {
			const overrides = buildOverridesJson();
			return scan.confirm(draftId, overrides);
		},
		onSuccess: (result) => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			queryClient.invalidateQueries({ queryKey: ['scan', draftId] });
			queryClient.invalidateQueries({ queryKey: ['expenses'] });
			toast.success('Expense created');
			goto(result.expenseId ? `/accounting/expenses/${result.expenseId}` : '/accounting');
		},
		onError: (err) => {
			toast.error(err instanceof Error ? err.message : 'Confirm failed');
		}
	}));

	// Reject mutation
	const rejectMutation = createMutation(() => ({
		mutationFn: (reason: string) => scan.reject(draftId, reason),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			toast.success('Scan rejected');
			goto('/scan');
		},
		onError: (err) => {
			toast.error(err instanceof Error ? err.message : 'Reject failed');
		}
	}));

	function buildOverridesJson(): string {
		// Send edited scalar fields only (line_items excluded).
		// Map legacy snake_case names that the API still expects in camelCase;
		// all new expanded fields are sent as-is (API accepts snake_case override keys).
		const keyMap: Record<string, string> = {
			vendor_name: 'vendorName',
			amount: 'amount',
			transaction_date: 'transactionDate',
			category: 'category',
			notes: 'notes'
		};
		const overrides: Record<string, unknown> = {};
		for (const [name, value] of Object.entries(editedFields)) {
			if (name === LINE_ITEMS_FIELD) continue;
			overrides[keyMap[name] ?? name] = value;
		}
		// Always include the paid/unpaid toggle decision
		overrides['is_paid'] = isPaid;
		return JSON.stringify(overrides);
	}

	function handleReject() {
		const reason = window.prompt('Enter a reason for rejection (optional):') ?? '';
		rejectMutation.mutate(reason);
	}
</script>

<svelte:head>
	<title>Review Scan - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="scan-review">
	{#if draftQuery.isLoading}
		<div class="flex h-48 items-center justify-center">
			<div class="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
		</div>
	{:else if !data}
		<p class="text-sm text-muted-foreground">Scan draft not found.</p>
	{:else}
		<div class="mb-4 flex items-center gap-3">
			<a href="/scan" class="text-sm text-muted-foreground hover:text-foreground">&larr; Back to Scans</a>
			<span class="text-muted-foreground">/</span>
			<h1 class="text-xl font-bold">Review Scan #{data.id}</h1>
			<Badge variant="outline" class={statusBadgeClass(data.status)}>
				{statusLabel(data.status)}
			</Badge>
		</div>

		{#if data.status === 'Failed'}
			<div class="mb-4 rounded-lg border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800 dark:border-red-700 dark:bg-red-900/20 dark:text-red-300">
				<strong>Extraction failed.</strong> You can still manually enter the field values below and confirm.
			</div>
		{/if}

		{#if data.status === 'Pending'}
			<div class="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300">
				Processing document… fields will appear once extraction completes.
			</div>
		{/if}

		{#if data.status === 'Confirmed'}
			<div class="mb-4 flex flex-col gap-3 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800 dark:border-green-800 dark:bg-green-900/20 dark:text-green-300 sm:flex-row sm:items-center sm:justify-between">
				<p>This scan is confirmed and locked. Edit the created expense instead.</p>
				{#if linkedExpenseHref}
					<Button data-testid="scan-view-expense" href={linkedExpenseHref} size="sm">View/Edit Expense</Button>
				{/if}
			</div>
		{/if}

		<div class="grid gap-6 lg:grid-cols-2">
			<!-- Left: document preview -->
			<Card.Root class="flex flex-col gap-0 py-0">
				<Card.Header class="border-b border-border px-4 py-3 [.border-b]:pb-3">
					<Card.Title class="text-sm">Document Preview</Card.Title>
				</Card.Header>
				<Card.Content class="flex flex-1 items-center justify-center overflow-hidden p-4">
					{#if fileUrl}
						<!-- Try img first; for PDFs it won't render but we also show an iframe/link -->
						<div class="w-full">
							<img
								src={fileUrl}
								alt="Scanned document"
								class="max-h-[60vh] w-full rounded object-contain"
								onerror={(e) => {
									// If image fails (it's a PDF), hide img and show iframe
									(e.currentTarget as HTMLImageElement).style.display = 'none';
									(e.currentTarget.nextElementSibling as HTMLElement | null)?.removeAttribute('hidden');
								}}
							/>
							<iframe
								hidden
								src={fileUrl}
								title="Scanned document"
								class="h-[60vh] w-full rounded border-0"
							></iframe>
							<div class="mt-2 text-center">
								<Button variant="link" href={fileUrl} target="_blank" rel="noopener noreferrer" class="h-auto p-0 text-xs">
									Open in new tab
								</Button>
							</div>
						</div>
					{:else}
						<p class="text-sm text-muted-foreground">No preview available.</p>
					{/if}
				</Card.Content>
			</Card.Root>

			<!-- Right: extracted fields form -->
			<Card.Root class="flex flex-col gap-0 py-0">
				<Card.Header class="border-b border-border px-4 py-3 [.border-b]:pb-3">
					<div class="flex items-baseline gap-1 flex-wrap">
						<Card.Title class="text-sm">Extracted Fields</Card.Title>
						{#if data.modelId}
							<span class="text-xs text-muted-foreground">via {data.modelId}</span>
						{/if}
						{#if data.tokensUsed != null}
							<span class="text-xs text-muted-foreground">· {data.tokensUsed.toLocaleString()} tokens</span>
						{/if}
						{#if data.costUsd != null}
							<span class="text-xs text-muted-foreground">· ~${data.costUsd.toFixed(4)}</span>
						{/if}
					</div>
				</Card.Header>
				<Card.Content class="flex-1 overflow-y-auto p-4">
					{#if data.fields.length === 0}
						<p class="text-sm text-muted-foreground">
							{#if data.status === 'Pending'}
								Fields will appear once extraction completes.
							{:else}
								No fields extracted. Enter values manually below.
							{/if}
						</p>
						<!-- Manual entry fallback: provide common fields -->
						<div class="mt-4 space-y-3">
							{#each ['vendor_name', 'total', 'subtotal', 'tax', 'transaction_date', 'category', 'payment_method', 'notes'] as fieldName}
								<div>
									<label class="mb-1 block text-xs font-medium text-muted-foreground capitalize" for="field-{fieldName}">
										{fieldName.replace(/_/g, ' ')}
									</label>
									{#if fieldName === 'category'}
										<!-- Native <select> kept for clean bind:value -->
										<select
											id="field-{fieldName}"
											data-testid="scan-field-{fieldName}"
											bind:value={editedFields[fieldName]}
											disabled={isScanLocked}
											class="w-full rounded border border-border bg-background px-3 py-2 text-sm text-foreground focus:outline-none focus:ring-1 focus:ring-ring"
										>
											<option value="">Select category</option>
											{#each SCHEDULE_E_CATEGORIES as cat}
												<option value={cat}>{cat}</option>
											{/each}
										</select>
									{:else}
										<Input
											id="field-{fieldName}"
											data-testid="scan-field-{fieldName}"
											type="text"
											bind:value={editedFields[fieldName]}
											disabled={isScanLocked}
										/>
									{/if}
								</div>
							{/each}
						</div>
					{:else}
						{@const groups = buildGroups(data.fields)}
						{@const lineItems = parseLineItems(data.fields)}
						<div class="space-y-6">
							{#each groups as group}
								<section>
									<h2 class="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">{group.label}</h2>
									<div class="grid grid-cols-2 gap-x-4 gap-y-3">
										{#each group.fields as field (field.name)}
											{@const level = confidenceLevel(field.confidence)}
											<div class="{field.name === 'vendor_address' || field.name === 'notes' ? 'col-span-2' : ''}">
												<div class="mb-1 flex items-center justify-between">
													<label
														class="text-xs font-medium capitalize {level === 'medium' ? 'text-muted-foreground' : 'text-foreground'}"
														for="field-{field.name}"
													>
														{field.name.replace(/_/g, ' ')}
													</label>
													{#if level !== 'high'}
														<span class="text-xs {level === 'low' ? 'text-red-500' : 'text-muted-foreground'}">
															{confidenceLabel(field.confidence)}
														</span>
													{/if}
												</div>
												{#if field.name === 'category'}
													<!-- Native <select> kept for clean bind:value -->
													<select
														id="field-{field.name}"
														data-testid="scan-field-{field.name}"
														bind:value={editedFields[field.name]}
														disabled={isScanLocked}
														class="w-full rounded border bg-background px-3 py-2 text-sm text-foreground focus:outline-none focus:ring-1 focus:ring-ring
															{level === 'low' ? 'border-red-400 dark:border-red-500' : 'border-border'}
															{level === 'medium' ? 'text-muted-foreground' : ''}"
													>
														<option value="">Select category</option>
														{#each SCHEDULE_E_CATEGORIES as cat}
															<option value={cat}>{cat}</option>
														{/each}
													</select>
												{:else}
													<!-- Raw <input> needed here to support the use:focusFirstLow action (actions cannot be placed on components) -->
													<input
														id="field-{field.name}"
														data-testid="scan-field-{field.name}"
														type="text"
														bind:value={editedFields[field.name]}
														disabled={isScanLocked}
														class="border-input bg-background selection:bg-primary dark:bg-input/30 selection:text-primary-foreground ring-offset-background placeholder:text-muted-foreground flex h-9 w-full min-w-0 rounded-md border px-3 py-1 text-base shadow-xs transition-[color,box-shadow] outline-none disabled:cursor-not-allowed disabled:opacity-50 md:text-sm focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px] {fieldInputClass(field)}"
														use:focusFirstLow={level === 'low' && isFirstLowField(data.fields, field)}
													/>
												{/if}
											</div>
										{/each}
									</div>
								</section>
							{/each}

							<!-- Line items table (read-only) -->
							<section>
								<h2 class="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">Line Items</h2>
								{#if lineItems.length === 0}
									<p class="text-sm text-muted-foreground">No line items.</p>
								{:else}
									<div data-testid="scan-line-items">
										<Table.Root>
											<Table.Header>
												<Table.Row class="bg-muted/40">
													<Table.Head class="px-3 py-2 text-xs">Description</Table.Head>
													<Table.Head class="px-3 py-2 text-right text-xs">Qty</Table.Head>
													<Table.Head class="px-3 py-2 text-right text-xs">Unit Price</Table.Head>
													<Table.Head class="px-3 py-2 text-right text-xs">Amount</Table.Head>
												</Table.Row>
											</Table.Header>
											<Table.Body>
												{#each lineItems as item, i}
													<Table.Row class={i % 2 === 1 ? 'bg-muted/20' : ''}>
														<Table.Cell class="px-3 py-2">{item.description ?? ''}</Table.Cell>
														<Table.Cell class="px-3 py-2 text-right">{formatQty(item.quantity)}</Table.Cell>
														<Table.Cell class="px-3 py-2 text-right">{item.unit_price != null ? formatMoney(item.unit_price) : ''}</Table.Cell>
														<Table.Cell class="px-3 py-2 text-right">{item.amount != null ? formatMoney(item.amount) : ''}</Table.Cell>
													</Table.Row>
												{/each}
											</Table.Body>
										</Table.Root>
									</div>
								{/if}
							</section>
						</div>
					{/if}
				</Card.Content>

				<!-- Paid / Unpaid toggle -->
				<div class="border-t border-border px-4 py-3" data-testid="scan-paid-toggle">
					<div class="flex items-center gap-3">
						<span class="text-xs font-medium text-muted-foreground">Payment status:</span>
						<Button
							type="button"
							size="sm"
							variant={isPaid ? 'default' : 'outline'}
							onclick={() => { isPaid = true; }}
							disabled={isScanLocked}
							class="rounded-r-none"
						>
							Already paid (receipt)
						</Button>
						<Button
							type="button"
							size="sm"
							variant={!isPaid ? 'default' : 'outline'}
							onclick={() => { isPaid = false; }}
							disabled={isScanLocked}
							class="-ml-3 rounded-l-none border-l-0"
						>
							Unpaid bill{editedFields['due_date'] ? ` — due ${editedFields['due_date']}` : ''}
						</Button>
					</div>
				</div>

				<!-- Action buttons -->
				<Card.Footer class="border-t border-border px-4 py-3 [.border-t]:pt-3">
					<div class="flex w-full flex-col gap-2">
						<div class="flex gap-3">
							<Button
								data-testid="scan-confirm"
								onclick={() => confirmMutation.mutate()}
								disabled={confirmMutation.isPending || data.status === 'Rejected' || data.status === 'Confirmed'}
								class="flex-1"
							>
								{confirmMutation.isPending ? 'Confirming…' : 'Confirm & Create Expense'}
							</Button>
							<Button
								data-testid="scan-reject"
								variant="outline"
								onclick={handleReject}
								disabled={rejectMutation.isPending || data.status === 'Rejected' || data.status === 'Confirmed'}
								class="hover:text-destructive"
							>
								{rejectMutation.isPending ? 'Rejecting…' : 'Reject'}
							</Button>
						</div>
						{#if data.status === 'Confirmed'}
							<div class="flex flex-col items-center gap-2 text-center text-xs text-green-600 dark:text-green-400">
								<p>This scan has already been confirmed.</p>
								{#if linkedExpenseHref}
									<Button data-testid="scan-footer-view-expense" href={linkedExpenseHref} variant="link" class="h-auto p-0 text-xs">View/Edit created expense</Button>
								{/if}
							</div>
						{/if}
						{#if data.status === 'Rejected'}
							<p class="text-center text-xs text-muted-foreground">This scan has been rejected.</p>
						{/if}
					</div>
				</Card.Footer>
			</Card.Root>
		</div>
	{/if}
</div>
