<script lang="ts">
	import type { UnitDashboard } from '$lib/types';
	import type { ScanContext } from '$lib/scan/scan-context';
	import { formatDateOnly } from '$lib/utils/date';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { money } from '../money';
	import MaintenanceTab from './MaintenanceTab.svelte';
	import { CalendarDays, ClipboardCheck, Clock3, ReceiptText, Wrench } from '@lucide/svelte';

	let {
		dashboard,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onScan: (context?: Partial<ScanContext>) => void;
	} = $props();

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
			onclick={() => onScan({ type: 'Expense', returnTo: `/units/${dashboard.unit.id}?tab=turnover` })}
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
					<p class="mt-2 text-xl font-semibold">{money(summary.estimatedCost)} / {money(summary.actualCost)}</p>
					<p class="mt-1 text-sm text-muted-foreground">{summary.receiptCount} receipt{summary.receiptCount === 1 ? '' : 's'} · {budgetVariance > 0 ? '+' : ''}{money(budgetVariance)}</p>
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
				<p class="mt-1 text-muted-foreground">Close work orders, attach receipts, and hand the unit back to listing.</p>
			</div>
		</div>
	</DetailCard>

	<div class="rounded-lg border bg-card p-4" data-testid="turnover-workspace">
		<div class="mb-4 flex items-center gap-2">
			<Wrench class="h-4 w-4 text-primary" />
			<h3 class="text-base font-semibold">Punch list and receipts</h3>
		</div>
		<MaintenanceTab {dashboard} {onScan} />
	</div>
</div>
