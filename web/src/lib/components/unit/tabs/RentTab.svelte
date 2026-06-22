<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { untrack } from 'svelte';
	import type { UnitDashboard, Payment } from '$lib/types';
	import { payments as paymentsApi } from '$lib/api/endpoints/payments';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { paymentSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { money } from '../money';
	import { formatDateOnly } from '$lib/utils/date';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { DollarSign, Plus, X, ScanLine, ChevronDown, ChevronRight } from '@lucide/svelte';

	let {
		dashboard,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onScan: () => void;
	} = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const leaseId = $derived(dashboard.currentLease?.id);

	const PAYMENT_TYPES = ['Rent', 'SecurityDeposit', 'LateFee', 'Utility', 'Other'];
	const PAYMENT_STATUSES = ['Scheduled', 'Paid', 'Partial', 'Late', 'Waived', 'Failed', 'Refunded'];
	const PAYMENT_PAGE_SIZE = 20;
	const today = () => new Date().toISOString().slice(0, 10);

	// Payments for the unit's current lease (the dominant case). The filter is DB-side (?leaseId=).
	let paymentPage = $state(1);
	let paymentItems = $state<Payment[]>([]);
	const paymentsQuery = createQuery(() => ({
		queryKey: ['payments', portfolioId, { leaseId }, paymentPage, PAYMENT_PAGE_SIZE],
		enabled: !!leaseId && portfolioId > 0,
		queryFn: () => paymentsApi.listPage(portfolioId, {
			leaseId,
			skip: (paymentPage - 1) * PAYMENT_PAGE_SIZE,
			take: PAYMENT_PAGE_SIZE,
			sort: 'dueDate',
		}),
	}));

	$effect(() => {
		void leaseId;
		paymentPage = 1;
		paymentItems = [];
	});

	$effect(() => {
		const page = paymentsQuery.data;
		if (!page) return;
		if (page.skip === 0) {
			paymentItems = page.items;
			return;
		}
		const currentItems = untrack(() => paymentItems);
		const seen = new Set(currentItems.map((p) => p.id));
		paymentItems = [...currentItems, ...page.items.filter((p) => !seen.has(p.id))];
	});

	const list = $derived(paymentItems);
	const totalPayments = $derived(paymentsQuery.data?.totalCount ?? paymentItems.length);
	const hasMorePayments = $derived(paymentItems.length < totalPayments);

	function invalidate() {
		paymentItems = [];
		paymentPage = 1;
		queryClient.invalidateQueries({ queryKey: ['payments', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['unit-dashboard', dashboard.unit.id] });
		queryClient.invalidateQueries({ queryKey: ['unit-timeline', dashboard.unit.id] });
	}

	function loadMorePayments() {
		if (paymentsQuery.isFetching || !hasMorePayments) return;
		paymentPage += 1;
	}

	// ── Inline "post payment" create form (reuses the app's paymentSchema + form conventions) ──
	const emptyCreate = () => ({ amount: '', dueDate: today(), paymentType: 'Rent', status: 'Paid' });
	let showCreate = $state(false);
	let createForm = $state(emptyCreate());
	let createErrors = $state<Record<string, string>>({});

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
		mutationFn: (data: Record<string, unknown>) => paymentsApi.create(data),
		onSuccess: () => {
			showSuccess('Payment posted.');
			closeCreate();
			invalidate();
		},
		onError: (e) => showError(apiErrorMessage(e)),
	}));

	function submitCreate() {
		if (!leaseId) return;
		const result = parseForm(paymentSchema, { ...createForm, leaseId: String(leaseId) });
		if (result.errors) {
			createErrors = result.errors;
			return;
		}
		createErrors = {};
		// result.data already carries leaseId (validated by the schema); add the portfolio scope.
		const data: Record<string, unknown> = { portfolioId, ...result.data };
		// A payment marked Paid with no explicit paid date defaults to its due date.
		if (createForm.status === 'Paid' && !data.paidDate) data.paidDate = createForm.dueDate;
		createMut.mutate(data);
	}

	// ── Expandable payment cards with on-card view/edit (mirrors the lease-detail edit pattern) ──
	let expandedId = $state<number | null>(null);
	let editingId = $state<number | null>(null);
	let editForm = $state<Record<string, string>>({});
	let editErrors = $state<Record<string, string>>({});

	function toggleExpand(p: Payment) {
		if (expandedId === p.id) {
			expandedId = null;
			editingId = null;
			return;
		}
		expandedId = p.id;
		editingId = null;
	}

	function startEdit(p: Payment) {
		editForm = {
			amount: String(p.amount),
			dueDate: p.dueDate?.slice(0, 10) ?? '',
			paymentType: p.paymentType,
			status: p.status,
			paidDate: p.paidDate?.slice(0, 10) ?? '',
			method: p.method ?? '',
			notes: p.notes ?? '',
		};
		editErrors = {};
		editingId = p.id;
	}
	function cancelEdit() {
		editingId = null;
		editErrors = {};
	}

	const editMut = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number; data: Record<string, unknown> }) => paymentsApi.update(id, data),
		onSuccess: () => {
			showSuccess('Payment updated.');
			editingId = null;
			invalidate();
		},
		onError: (e) => showError(apiErrorMessage(e)),
	}));

	function submitEdit(p: Payment) {
		const result = parseForm(paymentSchema, { ...editForm, leaseId: String(p.leaseId) });
		if (result.errors) {
			editErrors = result.errors;
			return;
		}
		editErrors = {};
		editMut.mutate({ id: p.id, data: { ...result.data } });
	}
</script>

<div class="space-y-4" data-testid="unit-rent-tab">
	<div class="flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card p-4">
		<div>
			<p class="text-sm text-muted-foreground">Outstanding balance</p>
			<p class="text-2xl font-bold">{money(dashboard.header.outstandingRentBalance)}</p>
		</div>
		<div class="flex flex-wrap gap-2">
			{#if leaseId}
				<Button class="gap-2" onclick={() => (showCreate ? closeCreate() : openCreate())} data-testid="rent-post-payment">
					{#if showCreate}<X class="h-4 w-4" /> Cancel{:else}<Plus class="h-4 w-4" /> Post payment{/if}
				</Button>
			{/if}
			<Button variant="outline" class="gap-2" onclick={() => onScan()} data-testid="rent-scan">
				<ScanLine class="h-4 w-4" /> Scan receipt
			</Button>
		</div>
	</div>

	<!-- Inline post-payment form (revealed below the header, not a drawer/modal). -->
	{#if showCreate && leaseId}
		<div class="rounded-xl border border-border bg-muted/20 p-4" data-testid="rent-create-form">
			<h3 class="mb-3 text-sm font-semibold">Post a payment</h3>
			<div class="grid gap-3 sm:grid-cols-2">
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="rent-amount">Amount</label>
					<Input id="rent-amount" data-testid="rent-amount-input" type="number" step="0.01" bind:value={createForm.amount} placeholder="0.00" />
					{#if createErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="rent-amount-error">{createErrors.amount}</p>{/if}
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="rent-due">Date</label>
					<Input id="rent-due" data-testid="rent-due-input" type="date" bind:value={createForm.dueDate} />
					{#if createErrors.dueDate}<p class="mt-1 text-xs text-destructive" data-testid="rent-due-error">{createErrors.dueDate}</p>{/if}
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="rent-type">Type</label>
					<Select.Root type="single" bind:value={createForm.paymentType}>
						<Select.Trigger id="rent-type" class="w-full" data-testid="rent-type-input">{createForm.paymentType}</Select.Trigger>
						<Select.Content>
							{#each PAYMENT_TYPES as t}<Select.Item value={t} label={t}>{t}</Select.Item>{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div>
					<label class="mb-1 block text-xs font-medium text-muted-foreground" for="rent-status">Status</label>
					<Select.Root type="single" bind:value={createForm.status}>
						<Select.Trigger id="rent-status" class="w-full" data-testid="rent-status-input">{createForm.status}</Select.Trigger>
						<Select.Content>
							{#each PAYMENT_STATUSES as s}<Select.Item value={s} label={s}>{s}</Select.Item>{/each}
						</Select.Content>
					</Select.Root>
				</div>
			</div>
			<div class="mt-3 flex justify-end gap-2">
				<Button variant="outline" size="sm" onclick={closeCreate}>Cancel</Button>
				<Button size="sm" disabled={createMut.isPending} onclick={submitCreate} data-testid="rent-create-submit">
					{createMut.isPending ? 'Posting…' : 'Post payment'}
				</Button>
			</div>
		</div>
	{/if}

	{#if !leaseId}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">
			No lease on this unit yet — rent activity appears once a lease exists.
		</p>
	{:else if paymentsQuery.isLoading}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">Loading payments…</p>
	{:else if list.length === 0}
		<p class="rounded-xl border bg-card p-6 text-center text-sm text-muted-foreground">No payments yet. Post a payment or scan a rent check.</p>
	{:else}
		<!-- Payment list as expandable cards (view ↔ edit on the same card). -->
		<ul class="space-y-2" data-testid="rent-payments">
			{#each list as p (p.id)}
				<li class="rounded-xl border bg-card" data-testid="rent-payment-{p.id}">
					<button type="button" class="flex w-full items-center justify-between gap-2 p-3 text-left" onclick={() => toggleExpand(p)}>
						<span class="flex items-center gap-2 text-sm">
							{#if expandedId === p.id}<ChevronDown class="h-4 w-4 text-muted-foreground" />{:else}<ChevronRight class="h-4 w-4 text-muted-foreground" />{/if}
							<span class="font-medium">{formatDateOnly(p.dueDate)}</span>
							<span class="text-muted-foreground">· {p.paymentType}</span>
						</span>
						<span class="flex items-center gap-2"><StatusBadge status={p.status} /><span class="font-semibold">{money(p.amount)}</span></span>
					</button>

					{#if expandedId === p.id}
						<div class="border-t p-3">
							{#if editingId === p.id}
								<div class="grid gap-3 sm:grid-cols-2" data-testid="rent-edit-form">
									<InlineField label="Amount" bind:value={editForm.amount} editing type="number" error={editErrors.amount} testid="rent-edit-amount" />
									<InlineField label="Due date" bind:value={editForm.dueDate} editing type="date" error={editErrors.dueDate} testid="rent-edit-due" />
									<InlineField label="Type" bind:value={editForm.paymentType} editing type="select" options={PAYMENT_TYPES.map((t) => ({ value: t, label: t }))} testid="rent-edit-type" />
									<InlineField label="Status" bind:value={editForm.status} editing type="select" options={PAYMENT_STATUSES.map((s) => ({ value: s, label: s }))} testid="rent-edit-status" />
									<InlineField label="Paid date" bind:value={editForm.paidDate} editing type="date" testid="rent-edit-paid" />
									<InlineField label="Method" bind:value={editForm.method} editing type="text" testid="rent-edit-method" />
								</div>
								<div class="mt-3 flex justify-end gap-2">
									<Button variant="outline" size="sm" onclick={cancelEdit} disabled={editMut.isPending}>Cancel</Button>
									<Button size="sm" onclick={() => submitEdit(p)} disabled={editMut.isPending} data-testid="rent-edit-save">
										{editMut.isPending ? 'Saving…' : 'Save'}
									</Button>
								</div>
							{:else}
								<dl class="grid grid-cols-2 gap-2 text-sm sm:grid-cols-3">
									<div><dt class="text-muted-foreground">Amount</dt><dd class="font-medium">{money(p.amount)}</dd></div>
									<div><dt class="text-muted-foreground">Due</dt><dd>{formatDateOnly(p.dueDate)}</dd></div>
									<div><dt class="text-muted-foreground">Paid</dt><dd>{p.paidDate ? formatDateOnly(p.paidDate) : '—'}</dd></div>
									<div><dt class="text-muted-foreground">Type</dt><dd>{p.paymentType}</dd></div>
									<div><dt class="text-muted-foreground">Status</dt><dd><StatusBadge status={p.status} /></dd></div>
									{#if p.method}<div><dt class="text-muted-foreground">Method</dt><dd>{p.method}</dd></div>{/if}
								</dl>
								<div class="mt-3 flex justify-end">
									<Button variant="outline" size="sm" onclick={() => startEdit(p)} data-testid="rent-edit-{p.id}">Edit</Button>
								</div>
							{/if}
						</div>
					{/if}
				</li>
			{/each}
		</ul>
		{#if hasMorePayments}
			<div class="flex justify-center">
				<Button variant="outline" size="sm" onclick={loadMorePayments} disabled={paymentsQuery.isFetching} data-testid="rent-load-more">
					{paymentsQuery.isFetching ? 'Loading…' : 'Load more'}
				</Button>
			</div>
		{/if}
	{/if}
</div>
