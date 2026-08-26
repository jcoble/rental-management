<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { leaseManagements } from '$lib/api/endpoints/lease-managements';
	import { securityDeposits } from '$lib/api/endpoints/securityDeposits';
	import { closeChecklist } from '$lib/leases/moveout-defaults';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import type { UnitDashboard } from '$lib/types';
	import type { ScanContext } from '$lib/scan/scan-context';
	import { formatDateOnly } from '$lib/utils/date';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import * as Dialog from '$lib/components/ui/dialog';
	import MaintenanceTab from './MaintenanceTab.svelte';
	import { CalendarDays, Check, ClipboardCheck, Clock3, ReceiptText, Wrench, X } from '@lucide/svelte';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';

	let {
		dashboard,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onScan: (context?: Partial<ScanContext>) => void;
	} = $props();

	const queryClient = useQueryClient();
	const canCloseAccount = $derived(hasCapability('leasing.onboarding.manage'));
	let closeOpen = $state(false);
	let closeReasonCode = $state('');
	let closeNote = $state('');

	// Shares its cache with the Tenant & lease tab, so opening both costs one round trip.
	const leasesQuery = createQuery(() => ({
		queryKey: ['lease-managements', 'unit', dashboard.unit.id],
		queryFn: () => leaseManagements.listPage({ unitId: dashboard.unit.id, take: 50, sort: '-updatedAtUtc' }),
		retry: false,
	}));

	const lease = $derived(
		leasesQuery.data?.items.find((item) => item.leaseManagementId === dashboard.leaseManagementId) ??
			leasesQuery.data?.items[0] ??
			null
	);
	const closeTenantAccountId = $derived(lease?.tenantAccountId ?? null);

	const depositQuery = createQuery(() => ({
		queryKey: ['deposit', closeTenantAccountId],
		queryFn: () => securityDeposits.get(closeTenantAccountId!),
		enabled: (closeTenantAccountId ?? 0) > 0,
		retry: false,
	}));

	const checklist = $derived(
		closeChecklist({
			unitId: dashboard.unit.id,
			tenantAccountId: closeTenantAccountId,
			keysReturned: Boolean(lease?.possessionReturnedAtUtc),
			amountOwed: dashboard.tenantAccountCondition?.receivableBalance ?? 0,
			depositHeld: depositQuery.data?.heldBalance ?? 0,
			paperworkPending: Boolean(
				lease?.upcomingLeaseAgreementId && lease.upcomingAgreementStatus !== 'Executed'
			),
		})
	);

	const closeMutation = createMutation(() => ({
		mutationFn: () =>
			leaseManagements.closeAccount(
				lease!.leaseManagementId,
				{
					tenantAccountId: closeTenantAccountId!,
					closeReasonCode: closeReasonCode.trim(),
					closeNote: closeNote.trim() || null,
				},
				crypto.randomUUID()
			),
		onSuccess: async () => {
			closeOpen = false;
			showSuccess('The tenant is all settled up and their account is closed.');
			await queryClient.invalidateQueries({ queryKey: ['lease-managements'] });
			await queryClient.invalidateQueries({ queryKey: ['unit-dashboard', dashboard.unit.id] });
		},
		onError: (error) =>
			showError(apiErrorMessage(error, 'The account could not be closed yet. Check the list above.')),
	}));

	const summary = $derived(dashboard.turnover ?? {
		status: 'NotStarted',
		totalTaskCount: 0,
		openTaskCount: 0,
		completedTaskCount: 0,
		receiptCount: 0,
		estimatedCost: 0,
		actualCost: 0,
		startedAt: null,
		targetReadyDate: null,
		lastActivityAt: null,
		daysInTurnover: null,
	});

	const statusLabel = $derived(turnoverStatusLabel(summary.status));
	const budgetVariance = $derived(summary.actualCost - summary.estimatedCost);

	function turnoverStatusLabel(status: string): string {
		switch (status) {
			case 'AwaitingVacancy':
				return 'Awaiting vacancy';
			case 'MoveOut':
				return 'Move-out';
			case 'InProgress':
				return 'In progress';
			case 'RentReady':
				return 'Rent-ready';
			case 'NotStarted':
				return 'Not started';
			default:
				return status;
		}
	}
</script>

<div class="space-y-4" data-testid="unit-turnover-tab">
	<div class="flex flex-wrap items-start justify-between gap-3">
		<div class="min-w-0">
			<h2 class="truncate text-lg font-semibold">Turnover</h2>
			<p class="text-sm text-muted-foreground">
				Move-out, make-ready work, linked receipts, and rent-ready progress for this unit.
			</p>
		</div>
		<Button
			size="sm"
			class="gap-2"
			onclick={() => onScan({ type: 'Expense', returnTo: `/units/${dashboard.unit.id}?tab=maintenance&view=turnover` })}
			data-testid="turnover-scan-receipt"
		>
			<ReceiptText class="h-4 w-4" />
			Scan receipt
		</Button>
	</div>

	<div class="grid gap-3 md:grid-cols-2 xl:grid-cols-4" data-testid="turnover-summary">
		<section class="rounded-lg border bg-card p-4" data-testid="turnover-status-card">
			<div class="flex items-start justify-between gap-3">
				<div>
					<p class="text-xs font-medium uppercase text-muted-foreground">Status</p>
					<p class="mt-2 text-xl font-semibold">{statusLabel}</p>
				</div>
				<StatusBadge status={summary.status} />
			</div>
			<p class="mt-3 text-sm text-muted-foreground">{summary.openTaskCount} open / {summary.completedTaskCount} done</p>
		</section>

		<section class="rounded-lg border bg-card p-4" data-testid="turnover-time-card">
			<div class="flex items-start gap-3">
				<Clock3 class="mt-0.5 h-5 w-5 text-primary" />
				<div>
					<p class="text-xs font-medium uppercase text-muted-foreground">Turn time</p>
					<p class="mt-2 text-xl font-semibold">{summary.daysInTurnover == null ? '—' : `${summary.daysInTurnover}d`}</p>
					<p class="mt-1 text-sm text-muted-foreground">{summary.startedAt ? `Started ${formatDateOnly(summary.startedAt)}` : 'No turnover start yet'}</p>
				</div>
			</div>
		</section>

		<section class="rounded-lg border bg-card p-4" data-testid="turnover-target-card">
			<div class="flex items-start gap-3">
				<CalendarDays class="mt-0.5 h-5 w-5 text-primary" />
				<div>
					<p class="text-xs font-medium uppercase text-muted-foreground">Target ready</p>
					<p class="mt-2 text-xl font-semibold">{summary.targetReadyDate ? formatDateOnly(summary.targetReadyDate) : '—'}</p>
					<p class="mt-1 text-sm text-muted-foreground">{summary.lastActivityAt ? `Updated ${formatDateOnly(summary.lastActivityAt)}` : 'No activity yet'}</p>
				</div>
			</div>
		</section>

		<section class="rounded-lg border bg-card p-4" data-testid="turnover-cost-card">
			<div class="flex items-start gap-3">
				<ReceiptText class="mt-0.5 h-5 w-5 text-primary" />
				<div>
					<p class="text-xs font-medium uppercase text-muted-foreground">Budget / actual</p>
					<p class="mt-2 text-xl font-semibold">{formatAccountingCurrency(summary.estimatedCost)} / {formatAccountingCurrency(summary.actualCost)}</p>
					<p class="mt-1 text-sm text-muted-foreground">{summary.receiptCount} receipt{summary.receiptCount === 1 ? '' : 's'} · {budgetVariance > 0 ? '+' : ''}{formatAccountingCurrency(budgetVariance)}</p>
				</div>
			</div>
		</section>
	</div>

	<DetailCard
		title="Make-ready plan"
		icon={ClipboardCheck}
		accent="primary"
		testid="turnover-plan"
	>
		<div class="grid gap-3 text-sm sm:grid-cols-3">
			<div class="rounded-md border bg-muted/30 p-3">
				<p class="font-medium">1. Move-out</p>
				<p class="mt-1 text-muted-foreground">Schedule the move-out visit and capture photos or inspection notes.</p>
			</div>
			<div class="rounded-md border bg-muted/30 p-3">
				<p class="font-medium">2. Punch list</p>
				<p class="mt-1 text-muted-foreground">Track cleanout, paint, repairs, re-key, appliances, and vendor jobs.</p>
			</div>
			<div class="rounded-md border bg-muted/30 p-3">
				<p class="font-medium">3. Ready</p>
				<p class="mt-1 text-muted-foreground">Close repairs, attach receipts, and hand the unit back to listing.</p>
			</div>
		</div>
	</DetailCard>

	{#if lease}
		<DetailCard title="Close out" icon={ClipboardCheck} testid="closeout-checklist">
			{#if lease.accountClosedAtUtc}
				<p class="text-sm text-muted-foreground" data-testid="closeout-done">
					This tenant is fully settled up and their account was closed on {formatDateOnly(lease.accountClosedAtUtc)}.
				</p>
			{:else}
				<p class="mb-3 text-sm text-muted-foreground">
					Four things have to be true before you can close this tenant out for good.
				</p>
				<ul class="space-y-2 text-sm">
					{#each checklist.rows as row (row.id)}
						<li class="flex flex-wrap items-start gap-3 rounded-md border p-3" data-testid="closeout-row-{row.id}">
							{#if row.done}
								<Check class="mt-0.5 h-4 w-4 shrink-0 text-primary" aria-label="Done" />
							{:else}
								<X class="mt-0.5 h-4 w-4 shrink-0 text-destructive" aria-label="Not yet" />
							{/if}
							<div class="min-w-0 flex-1">
								<p class="font-medium">{row.label}</p>
								<p class="mt-0.5 text-muted-foreground">{row.detail}</p>
							</div>
							{#if row.href && row.actionLabel}
								<Button href={row.href} variant="outline" size="sm">{row.actionLabel}</Button>
							{/if}
						</li>
					{/each}
				</ul>
				{#if canCloseAccount}
					<div class="mt-4 flex justify-end">
						<Button
							size="sm"
							disabled={!checklist.allGreen || !closeTenantAccountId}
							onclick={() => { closeReasonCode = ''; closeNote = ''; closeOpen = true; }}
							data-testid="closeout-close-account"
						>
							Close account
						</Button>
					</div>
				{/if}
			{/if}
		</DetailCard>
	{/if}

	<div class="rounded-lg border bg-card p-4" data-testid="turnover-workspace">
		<div class="mb-4 flex items-center gap-2">
			<Wrench class="h-4 w-4 text-primary" />
			<h3 class="text-base font-semibold">Punch list and receipts</h3>
		</div>
		<MaintenanceTab {dashboard} {onScan} />
	</div>
</div>

<Dialog.Root open={closeOpen} onOpenChange={(open) => (closeOpen = open)}>
	<Dialog.Content class="max-w-lg" data-testid="close-account-dialog">
		<Dialog.Header>
			<Dialog.Title>Close this tenant's account</Dialog.Title>
			<Dialog.Description>Everything on the close-out list is settled, so this account can be closed for good.</Dialog.Description>
		</Dialog.Header>
		<label class="block space-y-1 text-sm"><span>Why are you closing it?</span><input bind:value={closeReasonCode} maxlength="40" class="m3-field-surface h-10 w-full px-3" placeholder="For example: moved out" data-testid="close-account-reason" /></label>
		<label class="block space-y-1 text-sm"><span>Note <span class="text-muted-foreground">(optional)</span></span><textarea bind:value={closeNote} maxlength="1000" class="min-h-20 w-full rounded-md border p-3"></textarea></label>
		<Dialog.Footer>
			<Button variant="outline" onclick={() => (closeOpen = false)}>Cancel</Button>
			<Button disabled={!closeReasonCode.trim() || closeMutation.isPending} onclick={() => closeMutation.mutate()} data-testid="close-account-submit">Close account</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
