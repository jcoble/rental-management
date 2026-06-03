<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { toast } from 'svelte-sonner';
	import { scan, type ScanFieldDto } from '$lib/api/scan';
	import { leases } from '$lib/api/endpoints/leases';
	import { properties } from '$lib/api/endpoints/properties';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Badge } from '$lib/components/ui/badge';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Table from '$lib/components/ui/table';
	import * as Select from '$lib/components/ui/select';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import * as Dialog from '$lib/components/ui/dialog';

	// ScheduleECategory enum values (mirrors RentalCommand.Core.Enums.ScheduleECategory).
	// The submitted value stays the enum name; only the label shown to the landlord is friendly.
	const SCHEDULE_E_CATEGORIES: { value: string; label: string }[] = [
		{ value: 'Advertising', label: 'Advertising' },
		{ value: 'AutoTravel', label: 'Auto & travel' },
		{ value: 'CleaningMaintenance', label: 'Cleaning & maintenance' },
		{ value: 'Commissions', label: 'Commissions' },
		{ value: 'Insurance', label: 'Insurance' },
		{ value: 'LegalProfessional', label: 'Legal & professional fees' },
		{ value: 'ManagementFees', label: 'Management fees' },
		{ value: 'MortgageInterest', label: 'Mortgage interest' },
		{ value: 'Repairs', label: 'Repairs & maintenance' },
		{ value: 'Supplies', label: 'Supplies' },
		{ value: 'Taxes', label: 'Taxes' },
		{ value: 'Utilities', label: 'Utilities' },
		{ value: 'Depreciation', label: 'Depreciation' },
		{ value: 'Other', label: 'Other' }
	];

	// Map enum name → friendly label (falls back to the raw value if unknown).
	const CATEGORY_LABELS: Record<string, string> = Object.fromEntries(
		SCHEDULE_E_CATEGORIES.map((c) => [c.value, c.label])
	);

	function categoryLabel(value: string | undefined | null): string {
		if (!value) return 'Select category';
		return CATEGORY_LABELS[value] ?? value;
	}

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
		// Bounded poll: keep polling while the worker is still reading the document.
		// The worker flips the draft Pending → Processing → Reviewing, so we must
		// poll through BOTH Pending and Processing to catch the final Reviewing
		// state (SignalR also pushes the flip; the poll is a fallback).
		refetchInterval: (q) => {
			const s = q.state.data?.status;
			return s === 'Pending' || s === 'Processing' ? 1500 : false;
		}
	}));

	const data = $derived(draftQuery.data);

	// Whether this draft targets a Payment (rent check) rather than an Expense
	const isPayment = $derived(data?.targetEntityType === 'Payment');
	const isWorkOrder = $derived(data?.targetEntityType === 'WorkOrder');

	// The worker is still reading the document while Pending or Processing.
	const isProcessing = $derived(data?.status === 'Pending' || data?.status === 'Processing');

	// Lease selector state (only used when isPayment).
	// String-backed for the shadcn Select; converted to a number at confirm time.
	let selectedLeaseId = $state<string>('');

	// Optional property selector for Expense drafts. Sends `propertyId` override.
	// 'none' is the sentinel for "no property" (empty string conflicts with the
	// Select's "nothing selected" state in bits-ui).
	const NO_PROPERTY = 'none';
	let selectedPropertyId = $state<string>(NO_PROPERTY);

	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', getCurrentPortfolioId()],
		queryFn: () => leases.list(getCurrentPortfolioId()),
		enabled: isPayment
	}));

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', getCurrentPortfolioId()],
		queryFn: () => properties.list(getCurrentPortfolioId(), { take: 200 }),
		enabled: !isPayment
	}));

	const selectedPropertyLabel = $derived.by(() => {
		if (!selectedPropertyId || selectedPropertyId === NO_PROPERTY) return '— No property —';
		const sel = propertiesQuery.data?.find((p) => String(p.id) === selectedPropertyId);
		return sel ? sel.name : '— No property —';
	});

	function leaseLabel(lease: { leaseNumber: string; tenantName?: string | null; unitNumber?: string | null }): string {
		return `#${lease.leaseNumber}${lease.tenantName ? ` — ${lease.tenantName}` : ''}${lease.unitNumber ? ` · Unit ${lease.unitNumber}` : ''}`;
	}

	const selectedLeaseLabel = $derived.by(() => {
		if (!selectedLeaseId) return '— Select a lease —';
		const sel = leasesQuery.data?.find((l) => String(l.id) === selectedLeaseId);
		return sel ? leaseLabel(sel) : '— Select a lease —';
	});

	// Editable field values (keyed by field name, scalars only)
	let editedFields = $state<Record<string, string>>({});

	// Parse a money-ish string ("$1,234.50") into a number, or null if unparseable.
	function parseAmount(raw: string | undefined | null): number | null {
		if (raw == null) return null;
		const cleaned = String(raw).replace(/[^0-9.\-]/g, '');
		if (cleaned === '' || cleaned === '-' || cleaned === '.') return null;
		const n = Number(cleaned);
		return Number.isFinite(n) ? n : null;
	}

	// The amount the server will use for the expense = edited total, falling back to subtotal.
	// (Mirrors the server: Amount = total ?? subtotal.) Drives the $0 confirm guard.
	const resolvedAmount = $derived.by(() =>
		parseAmount(editedFields['total']) ?? parseAmount(editedFields['subtotal'])
	);

	// Block confirming an expense whose amount is blank/0/negative (server rejects amount <= 0).
	const amountInvalid = $derived(!isPayment && !isWorkOrder && (resolvedAmount == null || resolvedAmount <= 0));

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
			if ((data.targetEntityType === 'WorkOrder' || data.targetEntityType === 'Expense') && selectedPropertyId === NO_PROPERTY) {
				const propertyField = data.fields.find((f) => f.name === 'property_id' || f.name === 'propertyId');
				if (propertyField?.value) selectedPropertyId = propertyField.value;
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
			case 'Pending':
			case 'Processing': return 'Processing';
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
			case 'Processing':
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

	function fieldInputClass(_field: ScanFieldDto): string {
		// Confidence is conveyed by the label text only — no colored outline on the input.
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

	function fieldValue(name: string): string {
		return data?.fields.find((f) => f.name === name)?.value ?? '';
	}

	// Preview goes through a same-origin, cookie-authed SvelteKit route (the API's
	// /file endpoint needs a JWT bearer an <img>/<iframe> can't send).
	const fileUrl = $derived(data ? `/scan-file/${data.id}` : '');

	// Success state — what was just created, so the landlord keeps context
	// instead of being dumped onto /accounting.
	let confirmedRecord = $state<{ type: 'Expense' | 'Payment' | 'WorkOrder'; id: number | null; amount: number | null } | null>(null);
	const linkedRecordHref = $derived((() => {
		const type = confirmedRecord?.type ?? data?.createdEntityType;
		const id = confirmedRecord?.id ?? data?.createdEntityId;
		if (!type || !id) return '/accounting';
		if (type === 'Payment') return `/accounting/payments/${id}`;
		if (type === 'WorkOrder') return `/maintenance/${id}`;
		return `/accounting/expenses/${id}`;
	})());

	function formatUsd(val: number | null): string {
		if (val == null) return '';
		return val.toLocaleString('en-US', { style: 'currency', currency: 'USD' });
	}

	// Confirm mutation
	const confirmMutation = createMutation(() => ({
		mutationFn: () => {
			const overrides = buildOverridesJson();
			return scan.confirm(draftId, overrides);
		},
		onSuccess: (result) => {
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			queryClient.invalidateQueries({ queryKey: ['scan', draftId] });
			// Capture the resolved amount before refetch can mutate editedFields.
			const type = (result.entityType === 'Payment' || result.entityType === 'Expense' || result.entityType === 'WorkOrder')
				? result.entityType
				: isPayment ? 'Payment' : isWorkOrder ? 'WorkOrder' : 'Expense';
			confirmedRecord = {
				type,
				id: result.entityId ?? result.paymentId ?? result.expenseId ?? result.workOrderId ?? null,
				amount: resolvedAmount
			};
			if (isPayment) {
				toast.success('Payment recorded');
			} else if (isWorkOrder) {
				toast.success('Work order created');
			} else {
				toast.success('Expense created');
			}
			// Stay on the page and show a confirmation with a link to view it,
			// rather than silently navigating to /accounting.
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

		// Explicitly send the user-edited amount as a clean number under `total`
		// (the server honors `total`/`amount`). Without this the edited value
		// could be dropped or mis-parsed when typed with a currency symbol/commas.
		if (resolvedAmount != null) {
			overrides['total'] = resolvedAmount;
		}

		if (isPayment) {
			// Payment drafts require leaseId; omit the paid/unpaid toggle (a received check is always paid)
			overrides['leaseId'] = selectedLeaseId ? Number(selectedLeaseId) : null;
		} else if (isWorkOrder) {
			if (selectedPropertyId && selectedPropertyId !== NO_PROPERTY) {
				overrides['propertyId'] = Number(selectedPropertyId);
			}
		} else {
			// Expense drafts: always include the paid/unpaid toggle decision
			overrides['is_paid'] = isPaid;
			// Optional property association (server honors `propertyId`).
			if (selectedPropertyId && selectedPropertyId !== NO_PROPERTY) {
				overrides['propertyId'] = Number(selectedPropertyId);
			}
		}
		return JSON.stringify(overrides);
	}

	// Reject dialog state
	let showRejectDialog = $state(false);
	let rejectReason = $state('');

	function handleReject() {
		rejectReason = '';
		showRejectDialog = true;
	}

	function confirmReject() {
		showRejectDialog = false;
		rejectMutation.mutate(rejectReason);
	}
</script>

<svelte:head>
	<title>Review Scan - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="scan-review">
	{#if draftQuery.isLoading}
		<div class="flex h-48 items-center justify-center">
			<div class="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
		</div>
	{:else if draftQuery.isError}
		<!-- M5: clear error state with retry instead of a perpetual spinner -->
		<div class="mx-auto max-w-md py-12 text-center" data-testid="scan-load-error">
			<div class="mb-3 text-3xl">⚠️</div>
			<h2 class="mb-1 text-lg font-semibold">Couldn't load this scan</h2>
			<p class="mb-4 text-sm text-muted-foreground">
				{draftQuery.error instanceof Error ? draftQuery.error.message : 'Something went wrong fetching the scan draft.'}
			</p>
			<div class="flex items-center justify-center gap-3">
				<Button data-testid="scan-load-retry" onclick={() => draftQuery.refetch()} disabled={draftQuery.isFetching}>
					{draftQuery.isFetching ? 'Retrying…' : 'Try again'}
				</Button>
				<Button variant="outline" href="/scan">Back to scans</Button>
			</div>
		</div>
	{:else if !data}
		<div class="mx-auto max-w-md py-12 text-center">
			<p class="mb-4 text-sm text-muted-foreground">Scan draft not found.</p>
			<Button variant="outline" href="/scan">Back to scans</Button>
		</div>
	{:else}
		<div class="mb-4">
			<PageBreadcrumb
				crumbs={[
					{ label: 'Scans', href: '/scan' },
					{ label: 'Review' },
				]}
			/>
		</div>
		<div class="mb-4 flex items-center gap-3">
			<h1 class="text-xl font-bold">Review Scan #{data.id}</h1>
			<Badge variant="outline" class={statusBadgeClass(data.status)}>
				{statusLabel(data.status)}
			</Badge>
		</div>

		{#if confirmedRecord}
			<!-- design#11: keep context after confirm instead of dumping to /accounting -->
			<div
				class="mb-4 flex flex-col gap-3 rounded-lg border border-green-300 bg-green-50 px-4 py-4 text-sm text-green-900 dark:border-green-700 dark:bg-green-900/20 dark:text-green-200 sm:flex-row sm:items-center sm:justify-between"
				data-testid="scan-confirm-success"
			>
				<div class="flex items-center gap-3">
					<span class="text-2xl leading-none">✓</span>
					<div>
						<p class="font-semibold">
							{confirmedRecord.type === 'Payment' ? 'Payment recorded' : confirmedRecord.type === 'WorkOrder' ? 'Work order created' : 'Expense created'}{confirmedRecord.type !== 'WorkOrder' && confirmedRecord.amount != null ? ` — ${formatUsd(confirmedRecord.amount)}` : ''}
						</p>
						<p class="text-xs opacity-80">{confirmedRecord.type === 'WorkOrder' ? 'It is saved to maintenance.' : 'It is saved to your books.'} You can view it or scan another document.</p>
					</div>
				</div>
				<div class="flex shrink-0 gap-2">
					<Button size="sm" href={linkedRecordHref} data-testid="scan-view-record">View/Edit Record</Button>
					<Button size="sm" variant="outline" href="/scan">Scan another</Button>
				</div>
			</div>
		{/if}

		{#if data.status === 'Failed'}
			<!-- L7: clear "couldn't read this" message; Confirm disabled, Reject available -->
			<div class="mb-4 rounded-lg border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800 dark:border-red-700 dark:bg-red-900/20 dark:text-red-300" data-testid="scan-failed-banner">
				<strong>We couldn't read this document.</strong> The computer wasn't able to pull out the details automatically.
				You can <strong>Reject</strong> it to clear it from your list, then try scanning a clearer photo or PDF.
			</div>
		{/if}

		{#if isProcessing}
			<!-- design#10: prominent, readable "reading your document" state with progress -->
			<div
				class="mb-4 flex items-center gap-4 rounded-lg border border-accent/40 bg-accent/5 px-5 py-5"
				data-testid="scan-processing-banner"
				role="status"
				aria-live="polite"
			>
				<div class="h-9 w-9 shrink-0 animate-spin rounded-full border-[3px] border-accent border-t-transparent"></div>
				<div class="min-w-0">
					<p class="text-base font-semibold text-foreground">Reading your document…</p>
					<p class="text-sm text-muted-foreground">
						The computer is pulling out the vendor, amounts, and dates for you. This usually takes just a few seconds.
					</p>
					<!-- Indeterminate progress bar -->
					<div class="mt-2 h-1.5 w-full max-w-sm overflow-hidden rounded-full bg-accent/20">
						<div class="h-full w-1/3 animate-pulse rounded-full bg-accent"></div>
					</div>
				</div>
			</div>
		{/if}

		{#if data.modelId === 'noop'}
			<div class="mb-4 rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-200" data-testid="scan-noop-banner">
				<strong>AI extraction is off.</strong> No OpenAI API key is configured, so this document's fields
				weren't filled in automatically. Enter them manually below, or set
				<code class="rounded bg-amber-100 px-1 dark:bg-amber-900/40">Assistant:ApiKey</code> and re-scan.
			</div>
		{/if}

		<div class="grid gap-6 lg:grid-cols-2">
			<!-- Left: document preview -->
			<Card.Root class="flex flex-col gap-0 py-0">
				<Card.Header class="border-b border-border px-4 py-3 [.border-b]:pb-3">
					<Card.Title class="text-sm">Document Preview</Card.Title>
				</Card.Header>
				<Card.Content class="flex flex-1 items-center justify-center overflow-hidden p-4">
					{#if isWorkOrder && fieldValue('transcript')}
						<div class="w-full rounded-md border bg-muted/30 p-4">
							<p class="mb-2 text-xs font-semibold uppercase tracking-wider text-muted-foreground">Transcript</p>
							<p class="whitespace-pre-wrap text-sm leading-relaxed">{fieldValue('transcript')}</p>
						</div>
					{:else if fileUrl}
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
					{#if isPayment}
						<!-- Lease selector — required for Payment drafts -->
						<div class="mb-5 rounded-md border border-border bg-muted/30 p-3">
							<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-lease-select">
								Which lease is this payment for? <span class="text-red-500">*</span>
							</label>
							<Select.Root type="single" bind:value={selectedLeaseId}>
								<Select.Trigger id="scan-lease-select" data-testid="scan-lease-select" class="w-full">
									{selectedLeaseLabel}
								</Select.Trigger>
								<Select.Content>
									{#if leasesQuery.data}
										{#each leasesQuery.data as lease (lease.id)}
											<Select.Item value={String(lease.id)} label={leaseLabel(lease)}>
												{leaseLabel(lease)}
											</Select.Item>
										{/each}
									{/if}
								</Select.Content>
							</Select.Root>
							{#if leasesQuery.isLoading}
								<p class="mt-1 text-xs text-muted-foreground">Loading leases…</p>
							{/if}
						</div>
					{:else}
						<!-- Property selector for Expense/WorkOrder drafts (sends propertyId override) -->
						<div class="mb-5 rounded-md border border-border bg-muted/30 p-3">
							<label class="mb-1 block text-xs font-semibold text-foreground" for="scan-property-select">
								Which property is this for? {#if isWorkOrder}<span class="text-red-500">*</span>{:else}<span class="font-normal text-muted-foreground">(optional)</span>{/if}
							</label>
							<Select.Root type="single" bind:value={selectedPropertyId}>
								<Select.Trigger id="scan-property-select" data-testid="scan-property-select" class="w-full">
									{selectedPropertyLabel}
								</Select.Trigger>
								<Select.Content>
									<Select.Item value={NO_PROPERTY} label="— No property —">— No property —</Select.Item>
									{#if propertiesQuery.data}
										{#each propertiesQuery.data as prop (prop.id)}
											<Select.Item value={String(prop.id)} label={prop.name}>
												{prop.name}
											</Select.Item>
										{/each}
									{/if}
								</Select.Content>
							</Select.Root>
							{#if propertiesQuery.isLoading}
								<p class="mt-1 text-xs text-muted-foreground">Loading properties…</p>
							{/if}
							{#if isWorkOrder && selectedPropertyId === NO_PROPERTY}
								<p class="mt-1 text-xs text-amber-600 dark:text-amber-400">Select a property to create this work order.</p>
							{/if}
						</div>
					{/if}
					{#if data.fields.length === 0}
						<p class="text-sm text-muted-foreground">
							{#if isProcessing}
								Fields will appear once extraction completes.
							{:else}
								No fields extracted. Enter values manually below.
							{/if}
						</p>
						<!-- Manual entry fallback: provide common fields -->
						<div class="mt-4 space-y-3">
							<!-- Suppressed while processing so fields don't appear then get replaced by the full extracted set -->
							{#each (isProcessing ? [] : ['vendor_name', 'total', 'subtotal', 'tax', 'transaction_date', 'category', 'payment_method', 'notes']) as fieldName}
								<div>
									<label class="mb-1 block text-xs font-medium text-muted-foreground capitalize" for="field-{fieldName}">
										{fieldName.replace(/_/g, ' ')}
									</label>
									{#if fieldName === 'category'}
										<!-- Category dropdown — friendly labels, enum value submitted -->
										<Select.Root type="single" bind:value={editedFields[fieldName]}>
											<Select.Trigger id="field-{fieldName}" data-testid="scan-field-{fieldName}" class="w-full">
												{categoryLabel(editedFields[fieldName])}
											</Select.Trigger>
											<Select.Content>
												{#each SCHEDULE_E_CATEGORIES as cat}
													<Select.Item value={cat.value} label={cat.label}>{cat.label}</Select.Item>
												{/each}
											</Select.Content>
										</Select.Root>
									{:else}
										<Input
											id="field-{fieldName}"
											data-testid="scan-field-{fieldName}"
											type="text"
											bind:value={editedFields[fieldName]}
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
													<!-- Category dropdown — friendly labels, enum value submitted -->
													<Select.Root type="single" bind:value={editedFields[field.name]}>
														<Select.Trigger id="field-{field.name}" data-testid="scan-field-{field.name}" class="w-full">
															{categoryLabel(editedFields[field.name])}
														</Select.Trigger>
														<Select.Content>
															{#each SCHEDULE_E_CATEGORIES as cat}
																<Select.Item value={cat.value} label={cat.label}>{cat.label}</Select.Item>
															{/each}
														</Select.Content>
													</Select.Root>
												{:else}
													<!-- Raw <input> needed here to support the use:focusFirstLow action (actions cannot be placed on components) -->
													<input
														id="field-{field.name}"
														data-testid="scan-field-{field.name}"
														type="text"
														bind:value={editedFields[field.name]}
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
														<Table.Cell class="px-3 py-2 text-right font-mono tabular-nums">{formatQty(item.quantity)}</Table.Cell>
														<Table.Cell class="px-3 py-2 text-right font-mono tabular-nums">{item.unit_price != null ? formatMoney(item.unit_price) : ''}</Table.Cell>
														<Table.Cell class="px-3 py-2 text-right font-mono tabular-nums">{item.amount != null ? formatMoney(item.amount) : ''}</Table.Cell>
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

				<!-- Paid / Unpaid toggle — hidden for Payment and WorkOrder drafts -->
				{#if !isPayment && !isWorkOrder}
				<div class="border-t border-border px-4 py-3" data-testid="scan-paid-toggle">
					<span class="mb-1.5 block text-xs font-medium text-muted-foreground">Payment status</span>
					<!-- Segmented control: a single bordered track with two equal segments -->
					<div class="inline-flex w-full rounded-md border border-border bg-muted/40 p-0.5" role="group" aria-label="Payment status">
						<button
							type="button"
							data-testid="scan-paid-yes"
							aria-pressed={isPaid}
							onclick={() => { isPaid = true; }}
							class="flex-1 rounded-[5px] px-3 py-1.5 text-sm font-medium transition-colors {isPaid
								? 'bg-background text-foreground shadow-sm'
								: 'text-muted-foreground hover:text-foreground'}"
						>
							Already paid (receipt)
						</button>
						<button
							type="button"
							data-testid="scan-paid-no"
							aria-pressed={!isPaid}
							onclick={() => { isPaid = false; }}
							class="flex-1 rounded-[5px] px-3 py-1.5 text-sm font-medium transition-colors {!isPaid
								? 'bg-background text-foreground shadow-sm'
								: 'text-muted-foreground hover:text-foreground'}"
						>
							Unpaid bill{editedFields['due_date'] ? ` — due ${editedFields['due_date']}` : ''}
						</button>
					</div>
				</div>
				{/if}

				<!-- Action buttons -->
				<Card.Footer class="border-t border-border px-4 py-3 [.border-t]:pt-3">
					{@const isTerminal = data.status === 'Rejected' || data.status === 'Confirmed' || !!confirmedRecord}
					<div class="flex w-full flex-col gap-2">
						<div class="flex gap-3">
							<Button
								data-testid="scan-confirm"
								onclick={() => confirmMutation.mutate()}
								disabled={confirmMutation.isPending || isProcessing || isTerminal || (isPayment && !selectedLeaseId) || (isWorkOrder && selectedPropertyId === NO_PROPERTY) || amountInvalid}
								class="flex-1"
							>
								{confirmMutation.isPending ? 'Confirming…' : isPayment ? 'Create Payment' : isWorkOrder ? 'Create Work Order' : 'Confirm & Create Expense'}
							</Button>
							<Button
								data-testid="scan-reject"
								variant="outline"
								onclick={handleReject}
								disabled={rejectMutation.isPending || isProcessing || isTerminal}
								class="hover:text-destructive"
							>
								{rejectMutation.isPending ? 'Rejecting…' : 'Reject'}
							</Button>
						</div>
						{#if amountInvalid && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-red-500" data-testid="scan-amount-error">
								Enter an amount greater than $0 (under "total") before confirming.
							</p>
						{/if}
						{#if isPayment && !selectedLeaseId && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-amber-600 dark:text-amber-400">Select a lease above to enable payment creation.</p>
						{/if}
						{#if isWorkOrder && selectedPropertyId === NO_PROPERTY && !isTerminal && !isProcessing}
							<p class="text-center text-xs text-amber-600 dark:text-amber-400">Select a property above to enable work order creation.</p>
						{/if}
						{#if data.status === 'Failed' && !isTerminal}
							<p class="text-center text-xs text-muted-foreground">Couldn't read this document — enter the amount manually, or reject it.</p>
						{/if}
						{#if data.status === 'Confirmed' && !confirmedRecord}
							<p class="text-center text-xs text-green-600 dark:text-green-400">
								This scan has already been confirmed.
								<a href={linkedRecordHref} class="underline underline-offset-2">View/edit the created record.</a>
							</p>
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

<Dialog.Root
	open={showRejectDialog}
	onOpenChange={(v) => { if (!v) showRejectDialog = false; }}
>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>Reject scan</Dialog.Title>
			<Dialog.Description>Optionally provide a reason. The scan will be marked as rejected.</Dialog.Description>
		</Dialog.Header>
		<div class="py-2">
			<textarea
				bind:value={rejectReason}
				rows={3}
				placeholder="Reason for rejection (optional)"
				class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 placeholder:text-muted-foreground resize-none"
			></textarea>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={() => { showRejectDialog = false; }}>Cancel</Button>
			<Button variant="destructive" onclick={confirmReject} disabled={rejectMutation.isPending}>
				{rejectMutation.isPending ? 'Rejecting…' : 'Reject'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
