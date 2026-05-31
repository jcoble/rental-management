<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { securityDeposits } from '$lib/api/endpoints/securityDeposits';
	import { leases } from '$lib/api/endpoints/leases';
	import type { SecurityDepositHolding } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';
	import { Badge } from '$lib/components/ui/badge';
	import { Plus, ChevronDown, ChevronRight } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	// --- Queries ---
	const depositsQuery = createQuery(() => ({
		queryKey: ['deposits', portfolioId],
		queryFn: () => securityDeposits.list(),
	}));

	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId],
		queryFn: () => leases.list(portfolioId, { take: 200 }),
	}));

	function invalidateDeposits() {
		queryClient.invalidateQueries({ queryKey: ['deposits', portfolioId] });
	}

	// --- Expanded rows ---
	let expandedIds = $state<Set<number>>(new Set());
	function toggleExpand(id: number) {
		const next = new Set(expandedIds);
		if (next.has(id)) next.delete(id);
		else next.add(id);
		expandedIds = next;
	}

	// --- New holding dialog ---
	let showNewHolding = $state(false);
	let newHoldingLeaseId = $state('');
	let newHoldingAmount = $state('');
	let newHoldingNotes = $state('');

	function openNewHolding() {
		newHoldingLeaseId = '';
		newHoldingAmount = '';
		newHoldingNotes = '';
		showNewHolding = true;
	}
	function closeNewHolding() {
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
		const lid = parseInt(newHoldingLeaseId, 10);
		if (!lid) { showError('Please select a lease.'); return; }
		const body: { leaseId: number; amount?: number; notes?: string } = { leaseId: lid };
		const amt = parseFloat(newHoldingAmount);
		if (!isNaN(amt) && amt > 0) body.amount = amt;
		if (newHoldingNotes.trim()) body.notes = newHoldingNotes.trim();
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

	function openDeduction(deposit: SecurityDepositHolding) {
		deductionTarget = deposit;
		deductionReason = '';
		deductionAmount = '';
		deductionNotes = '';
	}
	function closeDeduction() {
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
		const reason = deductionReason.trim();
		const amount = parseFloat(deductionAmount);
		if (!reason) { showError('Please enter a reason.'); return; }
		if (isNaN(amount) || amount <= 0) { showError('Please enter a valid amount.'); return; }
		const body: { reason: string; amount: number; notes?: string } = { reason, amount };
		if (deductionNotes.trim()) body.notes = deductionNotes.trim();
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

	function formatDate(iso: string) {
		return new Date(iso).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
	}

	type BadgeVariant = 'default' | 'secondary' | 'destructive' | 'outline';

	function statusVariant(status: string): BadgeVariant {
		if (status === 'Held') return 'default';
		if (status === 'PartiallyReturned') return 'secondary';
		return 'outline';
	}

	function statusLabel(status: string) {
		if (status === 'Held') return 'Held';
		if (status === 'PartiallyReturned') return 'Partially Returned';
		return 'Returned';
	}

	const depositsList = $derived(depositsQuery.data ?? []);
</script>

<svelte:head>
	<title>Security Deposits - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="deposits-page">
	<div class="mb-4 flex items-center justify-between">
		<div>
			<h1 class="text-2xl font-bold">Security Deposits</h1>
			<p class="text-sm text-muted-foreground">Track held deposits, deductions, and returns for each lease.</p>
		</div>
		<Button data-testid="new-holding-button" onclick={openNewHolding}><Plus class="h-4 w-4" /> New Holding</Button>
	</div>

	<Card.Root class="gap-0 py-0">
		<Card.Content class="p-0">
			{#if depositsQuery.isLoading}
				<p class="py-10 text-center text-sm text-muted-foreground" data-testid="deposits-loading">Loading deposits…</p>
			{:else if depositsQuery.isError}
				<p class="py-10 text-center text-sm text-destructive" data-testid="deposits-error">Failed to load deposits. Please refresh.</p>
			{:else if depositsList.length === 0}
				<p class="py-10 text-center text-sm text-muted-foreground" data-testid="deposits-empty">No security deposits on record yet. Add a holding to get started.</p>
			{:else}
				<!-- Table header -->
				<div class="hidden grid-cols-[2fr_1fr_1fr_1fr_1fr_auto] gap-4 border-b border-border px-4 py-2 text-xs font-medium uppercase tracking-wide text-muted-foreground md:grid">
					<span>Lease</span>
					<span>Deposit</span>
					<span>Status</span>
					<span>Deductions</span>
					<span>Net Refund</span>
					<span></span>
				</div>
				{#each depositsList as deposit (deposit.id)}
					{@const expanded = expandedIds.has(deposit.id)}
					{@const canReturn = deposit.status === 'Held'}
					<div class="border-b border-border last:border-b-0" data-testid="deposit-row" data-deposit-id={deposit.id}>
						<!-- Main row -->
						<div class="grid grid-cols-[1fr_auto] gap-2 px-4 py-3 md:grid-cols-[2fr_1fr_1fr_1fr_1fr_auto]">
							<div class="min-w-0">
								<p class="truncate font-medium text-sm">{deposit.leaseNumber ?? `Lease #${deposit.leaseId}`}</p>
								<p class="text-xs text-muted-foreground">Held {formatDate(deposit.heldAt)}{deposit.returnedAt ? ` · Returned ${formatDate(deposit.returnedAt)}` : ''}</p>
							</div>
							<p class="hidden text-sm md:block">{money(deposit.amount)}</p>
							<div class="hidden md:block">
								<Badge variant={statusVariant(deposit.status)} class={deposit.status === 'Held' ? 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300' : deposit.status === 'PartiallyReturned' ? 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300' : 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300'}>
									{statusLabel(deposit.status)}
								</Badge>
							</div>
							<p class="hidden text-sm md:block">{deposit.deductions.length > 0 ? money(deposit.totalDeductions) : '—'}</p>
							<p class="hidden text-sm md:block">{money(deposit.netRefund)}</p>
							<div class="flex items-center gap-1">
								<Button
									variant="ghost"
									size="icon"
									aria-label={expanded ? 'Collapse details' : 'Expand details'}
									onclick={() => toggleExpand(deposit.id)}
									data-testid="deposit-expand-{deposit.id}"
								>
									{#if expanded}
										<ChevronDown class="h-4 w-4" />
									{:else}
										<ChevronRight class="h-4 w-4" />
									{/if}
								</Button>
							</div>
						</div>

						<!-- Expanded detail panel -->
						{#if expanded}
							<div class="border-t border-border bg-muted/20 px-4 pb-4 pt-3" data-testid="deposit-detail-{deposit.id}">
								<!-- Mobile summary -->
								<div class="mb-3 flex flex-wrap gap-4 text-sm md:hidden">
									<div><span class="text-xs text-muted-foreground">Deposit</span><br />{money(deposit.amount)}</div>
									<div>
										<span class="text-xs text-muted-foreground">Status</span><br />
										<Badge variant={statusVariant(deposit.status)} class={deposit.status === 'Held' ? 'bg-blue-100 text-blue-800' : deposit.status === 'PartiallyReturned' ? 'bg-amber-100 text-amber-800' : 'bg-green-100 text-green-800'}>
											{statusLabel(deposit.status)}
										</Badge>
									</div>
									<div><span class="text-xs text-muted-foreground">Deductions</span><br />{deposit.deductions.length > 0 ? money(deposit.totalDeductions) : '—'}</div>
									<div><span class="text-xs text-muted-foreground">Net Refund</span><br />{money(deposit.netRefund)}</div>
								</div>

								{#if deposit.notes}
									<p class="mb-3 text-sm text-muted-foreground">{deposit.notes}</p>
								{/if}

								<!-- Deductions list -->
								{#if deposit.deductions.length > 0}
									<div class="mb-3">
										<p class="mb-1 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Deductions</p>
										<div class="space-y-1">
											{#each deposit.deductions as d}
												<div class="flex items-center justify-between rounded bg-background px-3 py-2 text-sm ring-1 ring-border">
													<span class="font-medium">{d.reason}</span>
													<span class="text-destructive">{money(d.amount)}</span>
												</div>
												{#if d.notes}
													<p class="px-3 text-xs text-muted-foreground">{d.notes}</p>
												{/if}
											{/each}
										</div>
									</div>
								{:else}
									<p class="mb-3 text-sm text-muted-foreground">No deductions recorded.</p>
								{/if}

								<!-- Actions -->
								<div class="flex flex-wrap gap-2">
									<Button
										size="sm"
										variant="outline"
										onclick={() => openDeduction(deposit)}
										data-testid="add-deduction-{deposit.id}"
									>
										Add Deduction
									</Button>
									<Button
										size="sm"
										variant="outline"
										onclick={() => openReturn(deposit)}
										disabled={!canReturn}
										data-testid="process-return-{deposit.id}"
									>
										Process Return
									</Button>
									{#if !canReturn}
										<span class="self-center text-xs text-muted-foreground">
											{deposit.status === 'PartiallyReturned' ? 'Return already partially processed' : 'Deposit already returned'}
										</span>
									{/if}
								</div>
							</div>
						{/if}
					</div>
				{/each}
			{/if}
		</Card.Content>
	</Card.Root>
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
			<Dialog.Title>Add Deduction</Dialog.Title>
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
				{deductionMut.isPending ? 'Saving…' : 'Add Deduction'}
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
	onconfirm={submitReturn}
	oncancel={closeReturn}
/>
