<!--
  The dashboard opens on ONE ranked list of what needs attention today - money first - because that
  is the landlord's whole morning. Underneath it the page still tells the rest of the story: the
  portfolio band, the plain-English money cards, the headline numbers, the latest messages and
  repairs, and what has happened recently.
-->
<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { accounting } from '$lib/api/endpoints/accounting';
	import { messages } from '$lib/api/endpoints/messages';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { tenantMoney } from '$lib/api/endpoints/tenant-money';
	import { ai, type BriefingBullet } from '$lib/api/endpoints/ai';
	import type { Dashboard, DashboardActivity, PastDueLease } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { loadAllOpenCharges } from '$lib/accounting/past-due-preview';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import {
		AlertTriangle,
		ArrowRight,
		Building,
		CalendarClock,
		CheckCircle2,
		ChevronRight,
		CircleCheckBig,
		HandCoins,
		History,
		Home,
		Loader2,
		MessageSquare,
		PiggyBank,
		Receipt,
		Wallet,
		Wrench
	} from '@lucide/svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import GettingStartedCard from '$lib/components/onboarding/GettingStartedCard.svelte';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { recordHref, type RecordType } from '$lib/navigation/record-href';
	import { dashboardActivityHref } from '$lib/navigation/dashboard-activity-href';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { labelForType } from './appointments/calendar-utils';
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

	const snapshotQuery = createQuery(() => ({
		queryKey: ['accounting-snapshot', getCurrentPortfolioId()],
		queryFn: () => accounting.snapshot(),
	}));
	const messagesQuery = createQuery(() => ({
		queryKey: ['dashboard-messages', getCurrentPortfolioId()],
		queryFn: () => messages.listPage({ take: 5, sort: '-lastMessageAt' }),
	}));
	const workOrdersQuery = createQuery(() => ({
		queryKey: ['dashboard-work-orders', getCurrentPortfolioId()],
		queryFn: () => workOrders.list(getCurrentPortfolioId(), { take: 10, sort: '-requestedAt' }),
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
		<Card.Root
			class="m3-dashboard-hero m3-dashboard-art m3-dashboard-art--portfolio m3-surface-art m3-surface-art--hero m3-art-09 m3-motion-enter relative mb-6 overflow-hidden p-0"
			style="--m3-motion-index: 0"
			data-testid="dashboard-hero"
		>
			<Card.Content class="m3-dashboard-hero__content grid gap-5 p-5 md:grid-cols-[1.35fr_0.9fr] md:p-6">
				<div class="min-w-0">
					<div class="mb-4 flex flex-wrap items-center gap-2">
						<span class="m3-dashboard-chip m3-dashboard-chip--active">Today</span>
						<span class="m3-dashboard-chip">
							<span class="m3-dashboard-chip__dot text-warning"></span>
							{data.maintenance.openCount} open repairs
						</span>
						<span class="m3-dashboard-chip">{data.occupancy.occupancyRate}% occupied</span>
					</div>
					<p class="m3-type-label-large text-muted-foreground">{data.portfolio.managementCompanyName}</p>
					<h2 class="m3-type-display-small mt-2 max-w-2xl text-foreground">{data.portfolio.name}</h2>
					<p class="m3-type-body-large mt-3 max-w-2xl text-muted-foreground">
						Track rent, repairs, leasing, and tenant follow-up from one calm workspace.
					</p>
					<div class="mt-5 flex flex-wrap gap-3">
						<Button href="/accounting" class="gap-2" data-testid="dashboard-hero-primary-action">
							Open money
							<ArrowRight class="h-4 w-4" />
						</Button>
						<Button href="/properties" variant="secondary" data-testid="dashboard-hero-secondary-action">View properties</Button>
						<Button href="/onboarding?from=dashboard" variant="outline" class="gap-2" data-testid="dashboard-hero-guided-setup">
							Guided Setup
							<ArrowRight class="h-4 w-4" />
						</Button>
					</div>
				</div>
				<div class="m3-dashboard-hero-status p-5" data-testid="dashboard-hero-status">
					<div class="flex items-start justify-between gap-4">
						<div>
							<p class="m3-type-title-medium text-foreground">Today</p>
							<p class="m3-type-body-medium text-muted-foreground">Today's summary</p>
						</div>
						<div class="flex h-12 w-12 items-center justify-center rounded-[var(--m3-shape-large)] bg-primary/20 text-primary ring-1 ring-primary/25">
							<Home class="h-5 w-5" />
						</div>
					</div>
					<div class="mt-5 grid grid-cols-2 gap-3">
						<div class="m3-dashboard-hero-stat" data-testid="dashboard-hero-overdue">
							<p class="font-mono text-2xl font-semibold leading-none text-foreground">{money(data.accounting.overdueAmount)}</p>
							<p class="m3-type-body-small mt-1 text-muted-foreground">Who's behind</p>
						</div>
						<div class="m3-dashboard-hero-stat" data-testid="dashboard-hero-net">
							<p class="font-mono text-2xl font-semibold leading-none text-foreground">{money(data.accounting.netThisMonth)}</p>
							<p class="m3-type-body-small mt-1 text-muted-foreground">Kept this month</p>
						</div>
					</div>
				</div>
			</Card.Content>
		</Card.Root>

		<!-- The setup checklist is for a brand-new account only. Once there is a rental on the books
		     it stops taking up the dashboard; Guided Setup stays in the sidebar. -->
		{#if canOpenGettingStarted && data.occupancy.totalUnits === 0}
			<GettingStartedCard />
		{/if}

		<!-- Plain-English money snapshot: collected / spent / kept, each with a sentence -->
		<Card.Root class="m3-expressive-card m3-expressive-card--success m3-expressive-card--bars m3-motion-enter mb-6 gap-0 py-0" style="--m3-motion-index: 2" data-testid="dashboard-money-snapshot">
			<Card.Header class="px-5 pt-5 pb-2">
				<Card.Title class="flex items-center gap-2 text-base font-semibold">
					<Wallet class="h-4 w-4 text-primary" />
					Your money
					{#if snapshotQuery.data}
						<span class="font-normal text-muted-foreground">· {snapshotQuery.data.periodLabel}</span>
					{/if}
				</Card.Title>
			</Card.Header>
			<Card.Content class="px-5 pb-5 pt-0">
				{#if snapshotQuery.isLoading}
					<div class="grid gap-4 sm:grid-cols-3">
						{#each [0, 1, 2] as _}
							<div class="rounded-lg border border-border bg-background p-4">
								<div class="h-4 w-20 animate-pulse rounded bg-muted"></div>
								<div class="mt-2 h-9 w-28 animate-pulse rounded bg-muted"></div>
								<div class="mt-2 h-3 w-full animate-pulse rounded bg-muted"></div>
							</div>
						{/each}
					</div>
				{:else if snapshotQuery.isError || !snapshotQuery.data}
					<p class="text-sm text-muted-foreground">Your money snapshot is unavailable right now.</p>
				{:else}
					{@const snap = snapshotQuery.data}
					<div class="grid gap-4 sm:grid-cols-3">
						<div class="m3-tonal-card m3-tonal-card--mint rounded-lg border p-4" data-testid="dashboard-money-collected">
							<div class="flex items-center gap-2 text-sm font-medium text-success" title="All payments received - rent, deposits, and fees">
								<HandCoins class="h-4 w-4" /> Total Collected
							</div>
							<p class="mt-1 font-mono tabular-nums text-3xl font-bold text-foreground" data-testid="dashboard-money-collected-amount">{money(snap.collected)}</p>
							<p class="mt-1.5 text-sm leading-snug text-muted-foreground">{snap.explanations.collected}</p>
						</div>
						<div class="m3-tonal-card m3-tonal-card--amber rounded-lg border p-4" data-testid="dashboard-money-spent">
							<div class="flex items-center gap-2 text-sm font-medium text-warning">
								<Receipt class="h-4 w-4" /> Spent
							</div>
							<p class="mt-1 font-mono tabular-nums text-3xl font-bold text-foreground" data-testid="dashboard-money-spent-amount">{money(snap.spent)}</p>
							<p class="mt-1.5 text-sm leading-snug text-muted-foreground">{snap.explanations.spent}</p>
						</div>
						<div class="m3-tonal-card m3-tonal-card--sky rounded-lg border p-4" data-testid="dashboard-money-net">
							<div class="flex items-center gap-2 text-sm font-medium {snap.net < 0 ? 'text-destructive' : 'text-success'}">
								<PiggyBank class="h-4 w-4" /> Kept
							</div>
							<p class="mt-1 font-mono tabular-nums text-3xl font-bold text-foreground" data-testid="dashboard-money-net-amount">{money(snap.net)}</p>
							<p class="mt-1.5 text-sm leading-snug text-muted-foreground">{snap.explanations.net}</p>
						</div>
					</div>
					{#if snap.pastDueCount > 0}
						<a
							href="/accounting/past-due"
							class="group mt-4 flex items-start gap-2 rounded-lg border border-warning/40 bg-warning/10 px-4 py-3 text-sm text-foreground transition-colors hover:bg-warning/20"
							data-testid="dashboard-money-pastdue"
						>
							<AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-warning" />
							<span class="flex-1">{snap.explanations.pastDue}</span>
							<ChevronRight class="mt-0.5 h-4 w-4 shrink-0 text-warning/70 transition-transform group-hover:translate-x-0.5" />
						</a>
					{:else}
						<div
							class="mt-4 flex items-start gap-2 rounded-lg border border-border bg-background px-4 py-3 text-sm text-muted-foreground"
							data-testid="dashboard-money-pastdue"
						>
							<AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
							<span>{snap.explanations.pastDue}</span>
						</div>
					{/if}
				{/if}
			</Card.Content>
		</Card.Root>

		<div class="m3-motion-enter mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4" style="--m3-motion-index: 3" data-testid="dashboard-summary">
			<Card.Root class="m3-tonal-card m3-tonal-card--sky gap-0 py-0">
				<Card.Content class="p-4">
					<div class="flex items-center gap-2 text-muted-foreground"><Building class="h-4 w-4" /> Occupancy</div>
					<p class="mt-2 font-mono tabular-nums text-2xl font-bold">{data.occupancy.occupancyRate}%</p>
					<p class="text-xs text-muted-foreground">{data.occupancy.occupiedUnits}/{data.occupancy.totalUnits} occupied · {data.occupancy.vacantUnits} vacant</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="m3-tonal-card m3-tonal-card--coral gap-0 py-0">
				<Card.Content class="p-4">
					<div class="flex items-center gap-2 text-warning"><AlertTriangle class="h-4 w-4" /> Who's behind</div>
					<p class="mt-2 font-mono tabular-nums text-2xl font-bold">{money(data.accounting.overdueAmount)}</p>
					<p class="text-xs text-muted-foreground">Rent past due</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="m3-tonal-card m3-tonal-card--mint gap-0 py-0">
				<Card.Content class="p-4">
					<div class="flex items-center gap-2 text-success"><Wallet class="h-4 w-4" /> Kept this month</div>
					<p class="mt-2 font-mono tabular-nums text-2xl font-bold">{money(data.accounting.netThisMonth)}</p>
					<p class="text-xs text-muted-foreground">What's left after expenses</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="m3-tonal-card m3-tonal-card--rose gap-0 py-0">
				<Card.Content class="p-4">
					<div class="flex items-center gap-2 text-destructive"><Wrench class="h-4 w-4" /> Open repairs</div>
					<p class="mt-2 font-mono tabular-nums text-2xl font-bold">{data.maintenance.openCount}</p>
					<p class="text-xs text-muted-foreground">{data.maintenance.emergencyCount} emergency</p>
				</Card.Content>
			</Card.Root>
		</div>

		<!-- Secondary row: Latest messages collapses entirely when empty so it never eats prime
		     space; Latest repairs then expands to fill the row. -->
		{@const hasMessages = messagesQuery.isLoading || (messagesQuery.data?.items.length ?? 0) > 0}
		<div class="m3-motion-enter mb-6 grid gap-4 {hasMessages ? 'lg:grid-cols-2' : ''}" style="--m3-motion-index: 4">
			{#if hasMessages}
				<Card.Root class="m3-tonal-card m3-tonal-card--violet gap-0 py-0" data-testid="dashboard-latest-messages">
					<Card.Header class="px-4 pt-4 pb-3">
						<Card.Title class="flex items-center gap-2 text-base font-semibold">
							<MessageSquare class="h-4 w-4 text-primary" />
							Latest messages
						</Card.Title>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						{#if messagesQuery.isLoading}
							<div class="space-y-2">
								{#each [0, 1, 2] as _}
									<div class="h-14 w-full animate-pulse rounded bg-muted"></div>
								{/each}
							</div>
						{:else}
							<div class="space-y-2">
								{#each messagesQuery.data?.items ?? [] as thread}
									<a href={`/messages/${thread.id}`} class="block rounded border border-border bg-background px-3 py-2 transition-colors hover:bg-muted/40" data-testid={`dashboard-message-${thread.id}`}>
										<div class="flex items-center justify-between gap-3">
											<p class="truncate text-sm font-medium">{thread.subject}</p>
											<span class="shrink-0 font-mono text-[11px] text-muted-foreground">{new Date(thread.lastMessageAt).toLocaleDateString()}</span>
										</div>
										<p class="mt-0.5 truncate text-xs text-muted-foreground">{thread.tenantName}{thread.propertyName ? ` · ${thread.propertyName}` : ''}</p>
										{#if thread.lastMessagePreview}
											<p class="mt-1 truncate text-xs text-muted-foreground">{thread.lastMessagePreview}</p>
										{/if}
									</a>
								{/each}
							</div>
						{/if}
					</Card.Content>
				</Card.Root>
			{/if}

			<Card.Root class="m3-tonal-card m3-tonal-card--coral gap-0 py-0" data-testid="dashboard-latest-maintenance">
				<Card.Header class="px-4 pt-4 pb-3">
					<Card.Title class="flex items-center gap-2 text-base font-semibold">
						<Wrench class="h-4 w-4 text-primary" />
						Latest repairs
					</Card.Title>
				</Card.Header>
				<Card.Content class="px-4 pb-4 pt-0">
					{#if workOrdersQuery.isLoading}
						<div class="space-y-2">
							{#each [0, 1, 2] as _}
								<div class="h-14 w-full animate-pulse rounded bg-muted"></div>
							{/each}
						</div>
					{:else if workOrdersQuery.isError}
						<p class="text-sm text-muted-foreground">Repairs are unavailable right now.</p>
					{:else}
						<div class="space-y-2">
							{#each (workOrdersQuery.data ?? []).filter((w) => !['Completed', 'Cancelled', 'Archived'].includes(String(w.status))).slice(0, 3) as order}
								<a href={recordHref('workOrder', order)} class="block rounded border border-border bg-background px-3 py-2 transition-colors hover:bg-muted/40">
									<div class="flex items-center justify-between gap-3">
										<p class="truncate text-sm font-medium">{order.title}</p>
										<span class="shrink-0 rounded-full border px-2 py-0.5 text-[11px] text-muted-foreground">{formatStatusLabel(order.priority)}</span>
									</div>
									<p class="mt-1 truncate text-xs text-muted-foreground">{formatStatusLabel(order.status)} · {new Date(order.requestedAt).toLocaleDateString()}</p>
								</a>
							{:else}
								<p class="text-sm text-muted-foreground">No open repairs.</p>
							{/each}
						</div>
					{/if}
				</Card.Content>
			</Card.Root>
		</div>

		<div class="m3-motion-enter grid gap-6 lg:grid-cols-3" style="--m3-motion-index: 5">
			<div class="space-y-6 lg:col-span-2">
				<Card.Root class="m3-tonal-card m3-tonal-card--sky gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-3">
						<Card.Title class="text-base font-semibold">Recent activity</Card.Title>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						<div class="space-y-2">
							{#snippet activityBody(activity: DashboardActivity)}
								<p class="text-foreground">
									{activity.description || activity.action || activity.type}{#if activity.label}<span class="text-muted-foreground">&nbsp;— {activity.label}</span>{/if}
								</p>
								<p class="font-mono tabular-nums text-xs text-muted-foreground">{new Date(activity.createdAt).toLocaleString()}</p>
							{/snippet}
							{#each data.recentActivity.slice(0, 8) as activity}
								{@const href = dashboardActivityHref(activity)}
								{#if href}
									<a
										{href}
										data-testid="dashboard-activity-row"
										data-activity-type={activity.type}
										class="block rounded border border-border bg-background px-3 py-2 text-sm transition-colors hover:bg-muted/60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
									>
										{@render activityBody(activity)}
									</a>
								{:else}
									<div
										data-testid="dashboard-activity-row"
										data-activity-type={activity.type}
										class="rounded border border-border bg-background px-3 py-2 text-sm"
									>
										{@render activityBody(activity)}
									</div>
								{/if}
							{:else}
								<p class="text-sm text-muted-foreground">Nothing has happened yet.</p>
							{/each}
						</div>
						<a
							href="/audit"
							class="mt-3 inline-flex items-center gap-1.5 text-sm text-muted-foreground hover:text-foreground"
							data-testid="dashboard-activity-history-link"
						>
							<History class="h-4 w-4" />
							Activity history
						</a>
					</Card.Content>
				</Card.Root>
			</div>

			<div class="space-y-6">
				<Card.Root class="m3-tonal-card m3-tonal-card--rose gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-2">
						<Card.Title class="flex items-center gap-2 text-base font-semibold"><CalendarClock class="h-4 w-4 text-primary" />Upcoming appointments</Card.Title>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						{#if data.upcomingAppointments.length === 0}
							<p class="text-sm text-muted-foreground">No upcoming appointments.</p>
						{:else}
							<div class="space-y-2">
								{#each data.upcomingAppointments as appt}
									<div class="rounded border border-border bg-background p-2 text-sm">
										<p>{appt.title}</p>
										<p class="font-mono tabular-nums text-xs text-muted-foreground">{new Date(appt.scheduledStart).toLocaleString()} · {labelForType(appt.type)}</p>
									</div>
								{/each}
							</div>
						{/if}
					</Card.Content>
				</Card.Root>

				<Card.Root class="m3-tonal-card m3-tonal-card--mint gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-2">
						<Card.Title class="flex items-center gap-2 text-base font-semibold"><Home class="h-4 w-4 text-primary" />Leasing mix</Card.Title>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						<div class="space-y-2 text-sm">
							{#each Object.entries(data.leasing.byStatus) as [status, count]}
								<div class="flex items-center justify-between rounded border border-border bg-background px-2 py-1">
									<span>{formatStatusLabel(status)}</span>
									<span class="text-muted-foreground">{count}</span>
								</div>
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
			</div>
		</div>

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
