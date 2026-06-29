<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { securityDeposits } from '$lib/api/endpoints/securityDeposits';
	import { leases } from '$lib/api/endpoints/leases';
	import type { SecurityDepositHolding } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { newDepositHoldingSchema, depositDeductionSchema, parseForm } from '$lib/schemas';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { Info, Plus } from '@lucide/svelte';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;

	const initialParams = page.url.searchParams;
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));

	$effect(() => {
		syncGridUrl({ sort: gridSort, page: gridPage }, { page: 1 });
	});

	// --- Queries ---
	const depositsQuery = createQuery(() => ({
		queryKey: ['deposits', portfolioId, 'page', gridSort, gridPage, PAGE_SIZE],
		queryFn: () => securityDeposits.listPage({
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		}),
	}));

	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId],
		queryFn: () => leases.list(portfolioId, { take: 200 }),
	}));

	function invalidateDeposits() {
		queryClient.invalidateQueries({ queryKey: ['deposits', portfolioId] });
	}

	// --- New holding dialog ---
	let showNewHolding = $state(false);
	let newHoldingLeaseId = $state('');
	let newHoldingAmount = $state('');
	let newHoldingNotes = $state('');
	let newHoldingErrors = $state<Record<string, string>>({});

	function clearNewHoldingError(field: string) {
		if (!newHoldingErrors[field]) return;
		const next = { ...newHoldingErrors };
		delete next[field];
		newHoldingErrors = next;
	}

	$effect(() => {
		if (newHoldingLeaseId) clearNewHoldingError('leaseId');
	});
	$effect(() => {
		if (newHoldingAmount) clearNewHoldingError('amount');
	});

	function openNewHolding() {
		newHoldingLeaseId = '';
		newHoldingAmount = '';
		newHoldingNotes = '';
		newHoldingErrors = {};
		showNewHolding = true;
	}
	function closeNewHolding() {
		newHoldingErrors = {};
		showNewHolding = false;
	}

	const createMut = createMutation(() => ({
		mutationFn: (body: { leaseId: number; amount?: number; notes?: string }) =>
			securityDeposits.create(body),
		onSuccess: () => {
			showSuccess('Security deposit holding created.');
			closeNewHolding();
			invalidateDeposits();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitNewHolding() {
		const result = parseForm(newDepositHoldingSchema, {
			leaseId: newHoldingLeaseId,
			amount: newHoldingAmount,
			notes: newHoldingNotes,
		});
		if (result.errors) {
			newHoldingErrors = result.errors;
			return;
		}
		newHoldingErrors = {};
		const { leaseId, amount, notes } = result.data;
		const body: { leaseId: number; amount?: number; notes?: string } = { leaseId };
		if (amount != null) body.amount = amount;
		if (notes) body.notes = notes;
		createMut.mutate(body);
	}

	const selectedLeaseLabel = $derived(
		(leasesQuery.data ?? []).find((l) => String(l.id) === newHoldingLeaseId)
			? `${(leasesQuery.data ?? []).find((l) => String(l.id) === newHoldingLeaseId)!.leaseNumber} · ${(leasesQuery.data ?? []).find((l) => String(l.id) === newHoldingLeaseId)!.tenantName}`
			: null
	);

	// --- Add deduction dialog ---
	let deductionTarget = $state<SecurityDepositHolding | null>(null);
	let deductionReason = $state('');
	let deductionAmount = $state('');
	let deductionNotes = $state('');
	let deductionErrors = $state<Record<string, string>>({});

	function openDeduction(deposit: SecurityDepositHolding) {
		if (deposit.status !== 'Held') return;
		deductionTarget = deposit;
		deductionReason = '';
		deductionAmount = '';
		deductionNotes = '';
		deductionErrors = {};
	}
	function closeDeduction() {
		deductionErrors = {};
		deductionTarget = null;
	}

	const deductionMut = createMutation(() => ({
		mutationFn: ({ id, body }: { id: number; body: { reason: string; amount: number; notes?: string } }) =>
			securityDeposits.addDeduction(id, body),
		onSuccess: () => {
			showSuccess('Deduction added.');
			closeDeduction();
			invalidateDeposits();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitDeduction() {
		if (!deductionTarget) return;
		const result = parseForm(depositDeductionSchema, {
			reason: deductionReason,
			amount: deductionAmount,
			notes: deductionNotes,
		});
		if (result.errors) {
			deductionErrors = result.errors;
			return;
		}
		deductionErrors = {};
		const { reason, amount, notes } = result.data;
		const body: { reason: string; amount: number; notes?: string } = { reason, amount };
		if (notes) body.notes = notes;
		deductionMut.mutate({ id: deductionTarget.id, body });
	}

	// --- Process return confirm dialog ---
	let returnTarget = $state<SecurityDepositHolding | null>(null);
	let returnNotes = $state('');
	let showReturnDialog = $state(false);

	function openReturn(deposit: SecurityDepositHolding) {
		returnTarget = deposit;
		returnNotes = '';
		showReturnDialog = true;
	}
	function closeReturn() {
		showReturnDialog = false;
		returnTarget = null;
	}

	const returnMut = createMutation(() => ({
		mutationFn: ({ id, body }: { id: number; body: { notes?: string } }) =>
			securityDeposits.processReturn(id, body),
		onSuccess: () => {
			showSuccess('Deposit return processed.');
			closeReturn();
			invalidateDeposits();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitReturn() {
		if (!returnTarget) return;
		const body: { notes?: string } = {};
		if (returnNotes.trim()) body.notes = returnNotes.trim();
		returnMut.mutate({ id: returnTarget.id, body });
	}

	// --- Helpers ---
	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2 }).format(value ?? 0);
	}

	const depositStatusMap: Record<string, { label?: string; class: string }> = {
		Held: { class: 'm3-tone-chip border m3-tone--info' },
		PartiallyReturned: { label: 'Partially Returned', class: 'm3-tone-chip border m3-tone--warning' },
		Returned: { class: 'm3-tone-chip border m3-tone--success' },
		// Whole deposit consumed by deductions — nothing returned. Red, distinct from the amber partial.
		Withheld: { class: 'm3-tone-chip border m3-tone--error' },
	};

	const depositsList = $derived(depositsQuery.data?.items ?? []);
	const depositsTotalCount = $derived(depositsQuery.data?.totalCount ?? 0);

	// --- DataGrid columns ---
	const columns: ColumnDef<SecurityDepositHolding>[] = [
		{
			key: 'lease',
			title: 'Lease / Tenant',
			sortable: true,
			mobileRole: 'title',
			accessor: (d) => d.leaseNumber ?? `Lease #${d.leaseId}`,
			cell: leaseCell,
		},
		{
			key: 'amount',
			title: 'Amount',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
			accessor: (d) => d.amount,
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCell,
		},
		{
			key: 'heldAt',
			title: 'Held Since',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			align: 'right',
			width: '12rem',
			cell: actionsCell,
		},
	];
</script>

{#snippet leaseCell(d: SecurityDepositHolding)}
	<div class="flex flex-col" data-testid="deposit-lease">
		<span>{d.leaseNumber ?? `Lease #${d.leaseId}`}</span>
		{#if d.tenantName}
			<span class="text-xs text-muted-foreground">{d.tenantName}</span>
		{/if}
	</div>
{/snippet}

{#snippet statusCell(d: SecurityDepositHolding)}
	<StatusBadge status={d.status} map={depositStatusMap} />
{/snippet}

{#snippet actionsCell(d: SecurityDepositHolding)}
	<div class="flex items-center justify-end gap-1" onclick={(e) => e.stopPropagation()} role="none">
		<Button
			size="sm"
			variant="outline"
			class="h-7 text-xs"
			onclick={(e) => { e.stopPropagation(); openDeduction(d); }}
			disabled={d.status !== 'Held'}
			data-testid="add-deduction-{d.id}"
		>
			Add deduction
		</Button>
		<Button
			size="sm"
			variant="outline"
			class="h-7 text-xs"
			onclick={(e) => { e.stopPropagation(); openReturn(d); }}
			disabled={d.status !== 'Held'}
			data-testid="process-return-{d.id}"
		>
			Return
		</Button>
	</div>
{/snippet}

<svelte:head>
	<title>Security Deposits - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="deposits-page">
	<div class="mb-4 flex items-center justify-between">
		<div>
			<h1 class="text-2xl font-bold">Security Deposits</h1>
			<p class="text-sm text-muted-foreground">
				Money held <span class="font-medium text-foreground">in trust</span> per lease — separate from rental income. Track held deposits, deductions, and returns for each lease.
			</p>
		</div>
	</div>

	<!-- Explainer so this page doesn't read like a duplicate of Payments: a deposit is the tenant's
	     money you're safekeeping, not income you've earned. -->
	<div
		class="mb-4 flex items-start gap-3 rounded-lg border border-[color-mix(in_srgb,var(--info)_38%,transparent)] bg-[color-mix(in_srgb,var(--info)_8%,var(--card))] p-3 text-sm"
		data-testid="deposits-explainer"
	>
		<Info class="mt-0.5 h-4 w-4 shrink-0 text-[var(--info)]" />
		<p class="text-foreground">
			A security deposit is the tenant's money held in trust until the lease ends — it is
			<span class="font-medium">not rental income</span>. Rent and other payments live on the
			<a href="/accounting" class="font-medium underline underline-offset-2">Money</a> page; this page only
			tracks what you're safekeeping and what gets deducted or returned.
		</p>
	</div>

	<DataGrid
		data={depositsList}
		{columns}
		loading={depositsQuery.isLoading || depositsQuery.isFetching}
		emptyMessage="No security deposits on record yet. Add a holding to get started."
		getRowKey={(d) => d.id}
		getRowTestId={() => 'deposit-row'}
		onRowClick={(d) => goto(`/deposits/${d.id}`)}
		data-testid="deposits-list"
		pageSize={PAGE_SIZE}
		page={gridPage}
		totalCount={depositsTotalCount}
		serverSide
		onPageChange={(page) => (gridPage = page)}
		sort={gridSort}
		onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
	>
		{#snippet toolbar()}
			<div class="flex-1"></div>
			<Button data-testid="new-holding-button" class="gap-2 shrink-0" onclick={openNewHolding}>
				<Plus class="h-4 w-4" />
				New Holding
			</Button>
		{/snippet}
	</DataGrid>
</div>

<!-- New Holding Dialog -->
<Dialog.Root open={showNewHolding} onOpenChange={(v) => { if (!v) closeNewHolding(); }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>New Security Deposit Holding</Dialog.Title>
			<Dialog.Description>Record a deposit held for a lease. Amount defaults to the deposit on the lease if left blank.</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-3" data-testid="new-holding-form">
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Lease</span>
				<Select.Root type="single" bind:value={newHoldingLeaseId}>
					<Select.Trigger class="w-full" data-testid="holding-lease-select">
						{selectedLeaseLabel ?? 'Select a lease'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="Select a lease">Select a lease</Select.Item>
						{#each leasesQuery.data ?? [] as lease}
							<Select.Item value={String(lease.id)} label="{lease.leaseNumber} · {lease.tenantName}">
								{lease.leaseNumber} · {lease.tenantName}
							</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				{#if newHoldingErrors.leaseId}<p class="mt-1 text-xs text-destructive" data-testid="holding-lease-error">{newHoldingErrors.leaseId}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Amount (optional)</span>
				<Input
					data-testid="holding-amount-input"
					bind:value={newHoldingAmount}
					placeholder="Defaults to lease deposit"
					type="number"
					min="0"
					step="0.01"
				/>
				{#if newHoldingErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="holding-amount-error">{newHoldingErrors.amount}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Notes (optional)</span>
				<textarea
					data-testid="holding-notes-input"
					bind:value={newHoldingNotes}
					rows="2"
					class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
				></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={closeNewHolding} data-testid="new-holding-cancel">Cancel</Button>
			<Button onclick={submitNewHolding} disabled={createMut.isPending} data-testid="new-holding-save">
				{createMut.isPending ? 'Saving…' : 'Create Holding'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Add Deduction Dialog -->
<Dialog.Root open={deductionTarget !== null} onOpenChange={(v) => { if (!v) closeDeduction(); }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>Add deduction</Dialog.Title>
			<Dialog.Description>
				{deductionTarget ? `Deduct from the ${money(deductionTarget.amount)} deposit on lease ${deductionTarget.leaseNumber ?? deductionTarget.leaseId}.` : ''}
			</Dialog.Description>
		</Dialog.Header>
		<div class="space-y-3" data-testid="deduction-form">
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Reason</span>
				<Input
					data-testid="deduction-reason-input"
					bind:value={deductionReason}
					placeholder="e.g. Carpet cleaning, Broken window"
				/>
				{#if deductionErrors.reason}<p class="mt-1 text-xs text-destructive" data-testid="deduction-reason-error">{deductionErrors.reason}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Amount</span>
				<Input
					data-testid="deduction-amount-input"
					bind:value={deductionAmount}
					type="number"
					min="0"
					step="0.01"
					placeholder="0.00"
				/>
				{#if deductionErrors.amount}<p class="mt-1 text-xs text-destructive" data-testid="deduction-amount-error">{deductionErrors.amount}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Notes (optional)</span>
				<textarea
					data-testid="deduction-notes-input"
					bind:value={deductionNotes}
					rows="2"
					class="w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
				></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<Button variant="outline" onclick={closeDeduction} data-testid="deduction-cancel">Cancel</Button>
			<Button onclick={submitDeduction} disabled={deductionMut.isPending} data-testid="deduction-save">
				{deductionMut.isPending ? 'Saving…' : 'Add deduction'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Process Return Confirm Dialog -->
<ConfirmDialog
	open={showReturnDialog}
	title="Process deposit return"
	message={returnTarget
		? `Return ${money(returnTarget.netRefund)} (deposit ${money(returnTarget.amount)} minus ${money(returnTarget.totalDeductions)} in deductions) to the tenant?`
		: ''}
	busy={returnMut.isPending}
	testid="return-confirm"
	confirmLabel="Process return"
	onconfirm={submitReturn}
	oncancel={closeReturn}
/>
