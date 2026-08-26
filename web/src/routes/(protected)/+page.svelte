<!--
  The dashboard opens on ONE ranked list of what needs attention today — money first — because that
  is the landlord's whole morning. Everything that used to compete for the top of the page (hero
  band, KPI tiles, latest messages, latest work orders, recent activity, appointments, leasing mix)
  is gone; each of those lives on its own page in the nav. What's left below the list is a single
  compact business summary row, and the setup checklist only while there is no rental yet.
-->
<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { accounting } from '$lib/api/endpoints/accounting';
	import { tenantMoney } from '$lib/api/endpoints/tenant-money';
	import { ai, type BriefingBullet } from '$lib/api/endpoints/ai';
	import type { Dashboard, PastDueLease } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { loadAllOpenCharges } from '$lib/accounting/past-due-preview';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import {
		AlertTriangle,
		Building,
		CheckCircle2,
		ChevronRight,
		CircleCheckBig,
		History,
		Loader2,
		Wallet
	} from '@lucide/svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import GettingStartedCard from '$lib/components/onboarding/GettingStartedCard.svelte';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { recordHref, type RecordType } from '$lib/navigation/record-href';
	import DashboardBriefing from './DashboardBriefing.svelte';
	import PastDuePaymentDialog, {
		type PastDuePaymentSubmission
	} from '$lib/components/accounting/PastDuePaymentDialog.svelte';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';
	import {
		ATTENTION_LIMIT,
		attentionKindForBriefingCategory,
		rankAttention,
		type AttentionKind
	} from '$lib/dashboard/attention-ranking';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const dashboardQuery = createQuery(() => ({
		queryKey: ['dashboard', getCurrentPortfolioId()],
		queryFn: () => portfolios.dashboard(getCurrentPortfolioId()),
	}));

	// Same query key as the assistant line, so TanStack serves both from one request.
	const briefingQuery = createQuery(() => ({
		queryKey: ['ai-briefing', getCurrentPortfolioId()],
		queryFn: () => ai.briefing(),
		retry: false
	}));

	// The overdue rows come from the same GET /accounting/past-due source as the "Who's behind"
	// page, so the two can never disagree about who is behind or by how much.
	const PAST_DUE_TAKE = 20;
	const pastDueQuery = createQuery(() => ({
		queryKey: ['accounting-past-due', portfolioId, 0, PAST_DUE_TAKE],
		enabled: portfolioId > 0,
		queryFn: () => accounting.pastDue({ skip: 0, take: PAST_DUE_TAKE }),
	}));

	const canOpenGettingStarted = $derived(hasCapability('security.manage'));

	// The dashboard deliberately drops the cents.
	function money(value: number) {
		return formatAccountingCurrency(value || 0, 'USD', true);
	}

	const todayLabel = new Date().toLocaleDateString(undefined, {
		weekday: 'long',
		month: 'long',
		day: 'numeric'
	});

	function recordTypeForEntity(entityType: string | null | undefined): RecordType | null {
		switch (entityType) {
			case 'WorkOrder':
				return 'workOrder';
			case 'Payment':
				return 'payment';
			case 'LeaseManagement':
				return 'leaseManagement';
			case 'Expense':
				return 'expense';
			case 'RentalApplication':
			case 'Application':
				return 'application';
			default:
				return null;
		}
	}

	function bulletHref(bullet: BriefingBullet): string | null {
		if (!bullet.entityType || bullet.entityId == null) return null;
		if (bullet.entityType === 'TenantAccount') {
			return bullet.unitId
				? `/units/${bullet.unitId}?tab=money&tenantAccount=${bullet.entityId}`
				: `/tenant-accounts/${bullet.entityId}`;
		}
		if (bullet.entityType === 'LeaseAgreement') {
			return bullet.unitId
				? `/units/${bullet.unitId}?tab=tenant-lease&view=agreements&agreement=${bullet.entityId}`
				: `/lease-agreements/${bullet.entityId}`;
		}
		const type = recordTypeForEntity(bullet.entityType);
		if (type) return recordHref(type, { id: bullet.entityId, unitId: bullet.unitId });
		switch (bullet.entityType) {
			case 'Appointment':
				return `/appointments/${bullet.entityId}`;
			case 'Inspection':
				return `/maintenance/inspections/${bullet.entityId}`;
			default:
				return null;
		}
	}

	type AttentionRow = {
		key: string;
		kind: AttentionKind;
		title: string;
		detail: string;
		href: string | null;
		urgent: boolean;
		lease: PastDueLease | null;
	};

	function tenantName(lease: PastDueLease): string {
		if (lease.tenantName?.trim()) return lease.tenantName.trim();
		if (lease.relationshipNumber?.trim()) return lease.relationshipNumber.trim();
		if (lease.unitNumber?.trim()) return `Unit ${lease.unitNumber.trim()}`;
		return 'Tenant account';
	}

	function leaseWhere(lease: PastDueLease): string {
		if (!lease.propertyName?.trim()) return '';
		return lease.unitNumber?.trim()
			? `${lease.propertyName} · Unit ${lease.unitNumber}`
			: lease.propertyName;
	}

	const attentionRows = $derived.by<AttentionRow[]>(() => {
		const rows: AttentionRow[] = [];
		for (const lease of pastDueQuery.data?.items ?? []) {
			const where = leaseWhere(lease);
			rows.push({
				key: `past-due-${lease.tenantAccountId}`,
				kind: 'money',
				title: `${tenantName(lease)} owes ${money(lease.pastDueAmount)}`,
				detail: where ? `Rent past due · ${where}` : 'Rent past due',
				href: `/units/${lease.unitId}?tab=money&view=tenant-account&tenantAccount=${lease.tenantAccountId}`,
				urgent: true,
				lease
			});
		}
		const bullets = briefingQuery.isError ? [] : (briefingQuery.data?.bullets ?? []);
		bullets.forEach((bullet, index) => {
			const kind = attentionKindForBriefingCategory(bullet.category);
			if (!kind) return;
			rows.push({
				key: `briefing-${index}`,
				kind,
				title: bullet.title,
				detail: bullet.detail,
				href: bulletHref(bullet),
				urgent: bullet.severity === 'critical',
				lease: null
			});
		});
		return rows;
	});

	let showAllAttention = $state(false);
	const visibleAttention = $derived(
		rankAttention(attentionRows, showAllAttention ? attentionRows.length : ATTENTION_LIMIT)
	);
	const hiddenAttentionCount = $derived(Math.max(0, attentionRows.length - ATTENTION_LIMIT));
	const attentionLoading = $derived(pastDueQuery.isLoading || briefingQuery.isLoading);

	// ---- Record payment, straight from an overdue row -------------------------------------------
	let busyAccountId = $state<number | null>(null);
	let payTarget = $state<PastDueLease | null>(null);
	let receiptOperationKey = $state<string | null>(null);

	function openRecordPayment(lease: PastDueLease) {
		payTarget = lease;
		receiptOperationKey = null;
	}
	function closeRecordPayment() {
		payTarget = null;
		receiptOperationKey = null;
	}

	const openChargesQuery = createQuery(() => {
		const tenantAccountId = payTarget?.tenantAccountId;
		return {
			queryKey: ['past-due-open-charges', portfolioId, tenantAccountId],
			enabled: tenantAccountId != null,
			queryFn: () => loadAllOpenCharges(tenantAccountId as number)
		};
	});

	const recordPaymentMutation = createMutation(() => ({
		mutationFn: async ({ lease, data }: { lease: PastDueLease; data: PastDuePaymentSubmission }) => {
			receiptOperationKey ??= crypto.randomUUID();
			return tenantMoney.recordReceipt(lease.tenantAccountId, receiptOperationKey, {
				amount: data.amount,
				effectiveOn: data.paidDate,
				description: data.notes || `Payment for ${lease.relationshipNumber || 'tenant account'}`,
				paymentMethodSummary: data.method,
				externalReference: data.externalReference,
				payerName: data.payerName || lease.tenantName || undefined,
				targetChargeEntryId: null,
				allocateOldestCharges: true
			});
		},
		onMutate: ({ lease }) => {
			busyAccountId = lease.tenantAccountId;
		},
		onSuccess: () => {
			showSuccess('Payment recorded and allocated oldest-first.');
			closeRecordPayment();
			queryClient.invalidateQueries({ queryKey: ['accounting-past-due', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['accounting-snapshot', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['dashboard', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['payments'] });
		},
		onError: (err) => showError(apiErrorMessage(err, "Couldn't record this payment. Please try again.")),
		onSettled: () => {
			busyAccountId = null;
		}
	}));

	function submitRecordPayment(data: PastDuePaymentSubmission) {
		if (!payTarget) return;
		recordPaymentMutation.mutate({ lease: payTarget, data });
	}
</script>

<svelte:head>
	<title>Dashboard - Rental Command</title>
</svelte:head>

<div class="m3-page-frame">
	<header class="mb-5" data-testid="dashboard-today">
		<h1 class="m3-type-title-large text-foreground" data-testid="dashboard-today-header">
			Today · {todayLabel}
		</h1>
		<DashboardBriefing />
	</header>

	<section class="mb-6" data-testid="needs-attention">
		<Card.Root class="gap-0 py-0">
			<Card.Header class="px-5 pt-5 pb-3">
				<Card.Title class="flex items-center gap-2 text-base font-semibold">
					<AlertTriangle class="h-4 w-4 text-warning" />
					Needs attention
					{#if attentionRows.length > 0}
						<span class="font-normal text-muted-foreground">· {attentionRows.length}</span>
					{/if}
				</Card.Title>
			</Card.Header>
			<Card.Content class="px-5 pb-5 pt-0">
				{#if attentionLoading}
					<div class="space-y-2" data-testid="needs-attention-loading">
						{#each [0, 1, 2, 3] as _}
							<div class="h-14 w-full animate-pulse rounded-lg bg-muted"></div>
						{/each}
					</div>
				{:else if visibleAttention.length === 0}
					<div class="flex flex-col items-center gap-2 py-8 text-center" data-testid="needs-attention-empty">
						<CircleCheckBig class="h-9 w-9 text-success" />
						<p class="text-sm font-medium text-foreground">Nothing needs you right now.</p>
						<p class="max-w-sm text-sm text-muted-foreground">
							Late rent, repairs, and anything waiting on you show up here first.
						</p>
					</div>
				{:else}
					<ul class="space-y-2" data-testid="needs-attention-list">
						{#each visibleAttention as row (row.key)}
							{@const busy = row.lease != null && busyAccountId === row.lease.tenantAccountId}
							<li
								class="flex flex-wrap items-center gap-3 rounded-lg border border-border bg-background px-4 py-3"
								data-testid="needs-attention-row"
								data-attention-kind={row.kind}
							>
								<span
									class="h-8 w-1 shrink-0 rounded-full {row.urgent ? 'bg-destructive' : 'bg-primary/50'}"
								></span>
								{#if row.href}
									<a
										href={row.href}
										class="group min-w-0 flex-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
										data-testid="needs-attention-row-open"
									>
										<span class="flex min-w-0 items-center gap-1">
											<span class="min-w-0 truncate text-sm font-medium text-foreground">{row.title}</span>
											<ChevronRight class="h-4 w-4 shrink-0 text-muted-foreground/60 transition-transform group-hover:translate-x-0.5" />
										</span>
										<span class="mt-0.5 block truncate text-xs text-muted-foreground">{row.detail}</span>
									</a>
								{:else}
									<span class="min-w-0 flex-1">
										<span class="block truncate text-sm font-medium text-foreground">{row.title}</span>
										<span class="mt-0.5 block truncate text-xs text-muted-foreground">{row.detail}</span>
									</span>
								{/if}
								{#if row.lease}
									<Button
										size="sm"
										variant="secondary"
										class="shrink-0 gap-1.5"
										disabled={busy}
										onclick={() => openRecordPayment(row.lease as PastDueLease)}
										data-testid="needs-attention-record-payment"
									>
										{#if busy}
											<Loader2 class="h-4 w-4 animate-spin" />
										{:else}
											<CheckCircle2 class="h-4 w-4" />
										{/if}
										Record payment
									</Button>
								{/if}
							</li>
						{/each}
					</ul>
					{#if hiddenAttentionCount > 0}
						<Button
							variant="ghost"
							size="sm"
							class="mt-3"
							onclick={() => (showAllAttention = !showAllAttention)}
							data-testid="needs-attention-see-all"
						>
							{showAllAttention ? 'Show fewer' : `See all ${attentionRows.length}`}
						</Button>
					{/if}
				{/if}
			</Card.Content>
		</Card.Root>
	</section>

	{#if dashboardQuery.data}
		{@const data = dashboardQuery.data as Dashboard}
		<Card.Root class="mb-6 gap-0 py-0" data-testid="dashboard-summary">
			<Card.Content class="grid gap-4 p-5 sm:grid-cols-3">
				<div data-testid="dashboard-summary-kept">
					<div class="flex items-center gap-2 text-sm text-muted-foreground">
						<Wallet class="h-4 w-4 text-success" /> Kept this month
					</div>
					<p class="mt-1 font-mono tabular-nums text-2xl font-semibold text-foreground">
						{money(data.accounting.netThisMonth)}
					</p>
				</div>
				<a
					href="/accounting/past-due"
					class="group rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
					data-testid="dashboard-summary-behind"
				>
					<span class="flex items-center gap-2 text-sm text-muted-foreground">
						<AlertTriangle class="h-4 w-4 text-warning" /> Who's behind
						<ChevronRight class="h-4 w-4 text-muted-foreground/60 transition-transform group-hover:translate-x-0.5" />
					</span>
					<span class="mt-1 block font-mono tabular-nums text-2xl font-semibold text-foreground">
						{money(data.accounting.overdueAmount)}
					</span>
				</a>
				<a
					href="/properties"
					class="group rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
					data-testid="dashboard-summary-vacant"
				>
					<span class="flex items-center gap-2 text-sm text-muted-foreground">
						<Building class="h-4 w-4 text-primary" /> Vacant
						<ChevronRight class="h-4 w-4 text-muted-foreground/60 transition-transform group-hover:translate-x-0.5" />
					</span>
					<span class="mt-1 block font-mono tabular-nums text-2xl font-semibold text-foreground">
						{data.occupancy.vacantUnits}
					</span>
				</a>
			</Card.Content>
			<Card.Content class="border-t border-border px-5 py-3">
				<a
					href="/audit"
					class="inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground"
					data-testid="dashboard-activity-history-link"
				>
					<History class="h-4 w-4" />
					Activity history
				</a>
			</Card.Content>
		</Card.Root>

		<!-- The setup checklist is for a brand-new account only. Once there is a rental on the books
		     it stops taking up the dashboard; Guided Setup stays in the sidebar. -->
		{#if canOpenGettingStarted && data.occupancy.totalUnits === 0}
			<GettingStartedCard />
		{/if}
	{:else if dashboardQuery.isError}
		<div
			class="flex flex-wrap items-center justify-center gap-3 rounded-xl border border-destructive/40 bg-destructive/5 p-5 text-center"
			role="alert"
			data-testid="dashboard-error"
		>
			<p class="text-sm font-medium text-destructive" data-testid="dashboard-error-title">
				Could not load your summary.
			</p>
			<Button variant="outline" size="sm" onclick={() => dashboardQuery.refetch()} data-testid="dashboard-retry">
				Try again
			</Button>
			<Button href="/properties" variant="ghost" size="sm" data-testid="dashboard-properties-link">
				Open properties
			</Button>
		</div>
	{/if}
</div>

<PastDuePaymentDialog
	open={payTarget != null}
	target={payTarget}
	openCharges={openChargesQuery.data ?? []}
	totalOpenAmount={payTarget?.totalOpenBalance ?? 0}
	businessDate={pastDueQuery.data?.businessDate}
	openChargesLoading={openChargesQuery.isLoading}
	openChargesError={openChargesQuery.isError}
	pending={recordPaymentMutation.isPending}
	onclose={closeRecordPayment}
	onsubmit={submitRecordPayment}
/>
