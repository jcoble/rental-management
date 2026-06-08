<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { accounting } from '$lib/api/endpoints/accounting';
	import { ai, type BriefingBullet } from '$lib/api/endpoints/ai';
	import { messages } from '$lib/api/endpoints/messages';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { properties } from '$lib/api/endpoints/properties';
	import type { Dashboard } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Home, AlertTriangle, CalendarClock, Wallet, Wrench, Building, Sparkles, MessageSquare, HandCoins, Receipt, PiggyBank, Rocket, ArrowRight, ChevronRight, CircleCheckBig, ListChecks } from '@lucide/svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import AIBadge from '$lib/components/shared/AIBadge.svelte';

	const dashboardQuery = createQuery(() => ({
		queryKey: ['dashboard', getCurrentPortfolioId()],
		queryFn: () => portfolios.dashboard(getCurrentPortfolioId()),
	}));
	const snapshotQuery = createQuery(() => ({
		queryKey: ['accounting-snapshot', getCurrentPortfolioId()],
		queryFn: () => accounting.snapshot(),
	}));
	const briefingQuery = createQuery(() => ({
		queryKey: ['ai-briefing', getCurrentPortfolioId()],
		queryFn: () => ai.briefing(),
	}));
	const messagesQuery = createQuery(() => ({
		queryKey: ['dashboard-messages', getCurrentPortfolioId()],
		queryFn: () => messages.list(),
	}));
	const workOrdersQuery = createQuery(() => ({
		queryKey: ['dashboard-work-orders', getCurrentPortfolioId()],
		queryFn: () => workOrders.list(getCurrentPortfolioId(), { take: 10, sort: '-requestedAt' }),
	}));
	// Used only to detect an empty portfolio for the setup-wizard entry banner.
	const propertiesQuery = createQuery(() => ({
		queryKey: ['dashboard-properties-count', getCurrentPortfolioId()],
		queryFn: () => properties.list(getCurrentPortfolioId(), { take: 1 }),
	}));
	// Empty = the query resolved with zero properties (avoid flashing the banner while loading).
	const isEmptyPortfolio = $derived(propertiesQuery.isSuccess && (propertiesQuery.data?.length ?? 0) === 0);
	// The setup banner points at /onboarding, which is live-only. Sandbox is pre-seeded demo data,
	// so suppress the "finish setup" banner there.
	const sandboxQuery = createQuery(() => ({
		queryKey: ['sandbox-state', getCurrentPortfolioId()],
		queryFn: () => portfolios.sandboxState(),
		staleTime: 60_000,
	}));
	const isSandbox = $derived(sandboxQuery.data?.isSandbox === true);

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(value || 0);
	}

	// Resolve a briefing action item to the record it's about, so a to-do deep-links
	// straight to the lease/payment/work-order. Keep in sync with the server's
	// EntityType strings in DailyBriefingService (WorkOrder/Payment/Lease/Appointment/Inspection).
	function bulletHref(bullet: BriefingBullet): string | null {
		if (!bullet.entityType || bullet.entityId == null) return null;
		switch (bullet.entityType) {
			case 'WorkOrder':
				return `/maintenance/work-orders/${bullet.entityId}`;
			case 'Payment':
				return `/accounting/payments/${bullet.entityId}`;
			case 'Lease':
				return `/leases/${bullet.entityId}`;
			case 'Appointment':
				return `/appointments/${bullet.entityId}`;
			case 'Inspection':
				return `/maintenance/inspections/${bullet.entityId}`;
			default:
				return null;
		}
	}

	// Severity → tints for the action item's left rail and pill. Critical reads as
	// "drop everything", warning as "soon", info as "heads up".
	const severityStyles = {
		critical: { rail: 'bg-destructive', pill: 'border-destructive/40 bg-destructive/10 text-destructive' },
		warning: { rail: 'bg-warning', pill: 'border-warning/40 bg-warning/10 text-warning' },
		info: { rail: 'bg-primary/60', pill: 'border-border bg-muted text-muted-foreground' },
	} as const;
	function severityStyle(severity: string) {
		return severityStyles[severity as keyof typeof severityStyles] ?? severityStyles.info;
	}

	const ACTION_ITEM_LIMIT = 6;
</script>

<svelte:head>
	<title>Dashboard - Rental Command</title>
</svelte:head>

<div class="m3-page-frame">
	{#if dashboardQuery.isLoading}
		<!-- Skeleton loading state -->
		<div class="mb-6">
			<div class="h-7 w-48 animate-pulse rounded bg-muted"></div>
			<div class="mt-2 h-4 w-64 animate-pulse rounded bg-muted"></div>
		</div>
		<!-- Briefing hero skeleton (mirrors the real hero so the AI moat never looks broken) -->
		<div class="mb-6 rounded-xl border border-border bg-card p-5">
			<div class="h-5 w-44 animate-pulse rounded bg-muted"></div>
			<div class="mt-4 grid gap-5 lg:grid-cols-5">
				<div class="space-y-2 lg:col-span-3">
					<div class="h-24 w-full animate-pulse rounded-lg bg-muted"></div>
				</div>
				<div class="space-y-2 lg:col-span-2">
					{#each [0, 1, 2] as _}
						<div class="h-12 w-full animate-pulse rounded-lg bg-muted"></div>
					{/each}
				</div>
			</div>
		</div>
		<div class="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
			{#each [0, 1, 2, 3] as _}
				<Card.Root class="gap-0 py-0">
					<Card.Content class="p-4">
						<div class="h-4 w-24 animate-pulse rounded bg-muted"></div>
						<div class="mt-2 h-8 w-32 animate-pulse rounded bg-muted"></div>
						<div class="mt-1.5 h-3 w-20 animate-pulse rounded bg-muted"></div>
					</Card.Content>
				</Card.Root>
			{/each}
		</div>
		<div class="grid gap-6 lg:grid-cols-3">
			<div class="space-y-6 lg:col-span-2">
				<Card.Root class="gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-3">
						<div class="h-5 w-48 animate-pulse rounded bg-muted"></div>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						<div class="space-y-2">
							{#each [0, 1, 2] as _}
								<div class="h-14 w-full animate-pulse rounded border border-border bg-muted"></div>
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
				<Card.Root class="gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-3">
						<div class="h-5 w-32 animate-pulse rounded bg-muted"></div>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						<div class="space-y-2">
							{#each [0, 1, 2, 3, 4] as _}
								<div class="h-12 w-full animate-pulse rounded border border-border bg-muted"></div>
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
			</div>
			<div class="space-y-6">
				<Card.Root class="gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-2">
						<div class="h-5 w-40 animate-pulse rounded bg-muted"></div>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						<div class="space-y-2">
							{#each [0, 1, 2] as _}
								<div class="h-12 w-full animate-pulse rounded border border-border bg-muted"></div>
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
				<Card.Root class="gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-2">
						<div class="h-5 w-32 animate-pulse rounded bg-muted"></div>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						<div class="space-y-2">
							{#each [0, 1, 2] as _}
								<div class="h-8 w-full animate-pulse rounded border border-border bg-muted"></div>
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
			</div>
		</div>
	{:else if dashboardQuery.isError}
		<div class="flex h-64 items-center justify-center text-destructive">Failed to load dashboard.</div>
	{:else if dashboardQuery.data}
		{@const data = dashboardQuery.data as Dashboard}
		<Card.Root
			class="m3-dashboard-hero m3-surface-pattern m3-motion-enter relative mb-6 overflow-hidden border-primary/30 p-0"
			style="--m3-motion-index: 0"
			data-testid="dashboard-hero"
		>
			<Card.Content class="m3-dashboard-hero__content grid gap-5 p-5 md:grid-cols-[1.35fr_0.9fr] md:p-6">
				<div class="min-w-0">
					<div class="mb-4 flex flex-wrap items-center gap-2">
						<span class="m3-dashboard-chip m3-dashboard-chip--active">Command center</span>
						<span class="m3-dashboard-chip">
							<span class="m3-dashboard-chip__dot text-warning"></span>
							{data.maintenance.openCount} open work orders
						</span>
						<span class="m3-dashboard-chip">{data.occupancy.occupancyRate}% occupied</span>
					</div>
					<p class="m3-type-label-large text-muted-foreground">{data.portfolio.managementCompanyName}</p>
					<h1 class="m3-type-display-small mt-2 max-w-2xl text-foreground">{data.portfolio.name}</h1>
					<p class="m3-type-body-large mt-3 max-w-2xl text-muted-foreground">
						Track rent, maintenance, leasing, and tenant follow-up from one calm portfolio workspace.
					</p>
					<div class="mt-5 flex flex-wrap gap-3">
						<Button href="/accounting" class="gap-2" data-testid="dashboard-hero-primary-action">
							Open money
							<ArrowRight class="h-4 w-4" />
						</Button>
						<Button href="/properties" variant="secondary" data-testid="dashboard-hero-secondary-action">View properties</Button>
					</div>
				</div>
				<div class="m3-dashboard-hero-status p-5" data-testid="dashboard-hero-status">
					<div class="flex items-start justify-between gap-4">
						<div>
							<p class="m3-type-title-medium text-foreground">Today</p>
							<p class="m3-type-body-medium text-muted-foreground">Portfolio pulse</p>
						</div>
						<div class="flex h-12 w-12 items-center justify-center rounded-[var(--m3-shape-large)] bg-primary/20 text-primary ring-1 ring-primary/25">
							<Home class="h-5 w-5" />
						</div>
					</div>
					<div class="mt-5 grid grid-cols-2 gap-3">
						<div class="m3-dashboard-hero-stat" data-testid="dashboard-hero-overdue">
							<p class="font-mono text-2xl font-semibold leading-none text-foreground">{money(data.accounting.overdueAmount)}</p>
							<p class="m3-type-body-small mt-1 text-muted-foreground">overdue</p>
						</div>
						<div class="m3-dashboard-hero-stat" data-testid="dashboard-hero-net">
							<p class="font-mono text-2xl font-semibold leading-none text-foreground">{money(data.accounting.netThisMonth)}</p>
							<p class="m3-type-body-small mt-1 text-muted-foreground">net this month</p>
						</div>
					</div>
				</div>
			</Card.Content>
		</Card.Root>

		{#if isEmptyPortfolio && !isSandbox}
			<!-- Empty-portfolio entry point into the guided setup wizard (live accounts only) -->
			<Card.Root class="m3-motion-enter mb-6 border-primary/40 bg-primary/8" style="--m3-motion-index: 1" data-testid="dashboard-onboarding-banner">
				<Card.Content class="flex flex-col gap-4 p-5 sm:flex-row sm:items-center sm:justify-between">
					<div class="flex items-start gap-3">
						<div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-[var(--m3-shape-large)] bg-primary/15 text-primary">
							<Rocket class="h-5 w-5" />
						</div>
						<div>
							<p class="font-semibold text-foreground">Finish setting up your portfolio</p>
							<p class="mt-0.5 text-sm text-muted-foreground">
								Add your first property, tenants, and lease in a few guided steps — the computer does the typing.
							</p>
						</div>
					</div>
					<Button href="/onboarding" class="shrink-0 gap-2" data-testid="dashboard-onboarding-cta">
						Start setup
						<ArrowRight class="h-4 w-4" />
					</Button>
				</Card.Content>
			</Card.Root>
		{/if}

		<!-- Today's Briefing — the AI moat, promoted to the top, full width. Two distinct halves:
		     the AI voice (what the computer is saying) and the action list (what to do today). -->
		<Card.Root
			class="m3-surface-pattern m3-motion-enter relative mb-6 gap-0 overflow-hidden border-primary/30 py-0"
			style="--m3-motion-index: 1"
			data-testid="dashboard-todays-briefing"
		>
			<Card.Header class="relative px-5 pt-5 pb-3">
				<Card.Title class="flex items-center gap-2 text-lg font-semibold">
					<span class="flex h-7 w-7 items-center justify-center rounded-[var(--m3-shape-large)] bg-primary/15 ring-1 ring-primary/25">
						<Sparkles class="h-4 w-4 text-primary" />
					</span>
					Today's Briefing
					<AIBadge />
				</Card.Title>
			</Card.Header>
			<Card.Content class="relative px-5 pb-5 pt-0">
				{#if briefingQuery.isLoading}
					<div class="grid gap-5 lg:grid-cols-5">
						<div class="lg:col-span-3">
							<div class="h-28 w-full animate-pulse rounded-xl bg-muted"></div>
						</div>
						<div class="space-y-2 lg:col-span-2">
							{#each [0, 1, 2] as _}
								<div class="h-12 w-full animate-pulse rounded-lg bg-muted"></div>
							{/each}
						</div>
					</div>
				{:else}
					{@const briefing = briefingQuery.isError ? undefined : briefingQuery.data}
					{@const bullets = briefing?.bullets ?? []}
					{@const shown = bullets.slice(0, ACTION_ITEM_LIMIT)}
					{@const overflow = bullets.length - shown.length}
					<div class="grid gap-5 lg:grid-cols-5">
						<!-- LEFT: the AI voice. Gradient-tinted, ringed, clear type — obviously the computer talking. -->
						<div class="lg:col-span-3">
							{#if briefing?.summary}
								<div
									class="flex flex-col gap-3 rounded-[var(--m3-shape-large)] bg-[color-mix(in_srgb,var(--primary)_12%,transparent)] p-4 ring-1 ring-primary/25"
									data-testid="dashboard-briefing-narrative"
								>
									<div class="flex items-center gap-2">
										<span class="flex h-6 w-6 items-center justify-center rounded-full bg-primary/20 ring-1 ring-primary/25">
											<Sparkles class="h-3.5 w-3.5 text-primary" />
										</span>
										<span class="m3-type-label-medium text-primary">The assistant says</span>
									</div>
									<p class="text-[15px] leading-relaxed text-foreground">{briefing.summary}</p>
								</div>
							{:else if briefingQuery.isError}
								<div class="flex items-center rounded-xl bg-muted/40 p-4 text-sm text-muted-foreground" data-testid="dashboard-briefing-narrative">
									Your daily briefing is unavailable right now.
								</div>
							{:else}
								<div
									class="flex flex-col gap-2 rounded-[var(--m3-shape-large)] bg-[color-mix(in_srgb,var(--primary)_12%,transparent)] p-4 ring-1 ring-primary/25"
									data-testid="dashboard-briefing-narrative"
								>
									<div class="flex items-center gap-2">
										<span class="flex h-6 w-6 items-center justify-center rounded-full bg-primary/20 ring-1 ring-primary/25">
											<Sparkles class="h-3.5 w-3.5 text-primary" />
										</span>
										<span class="m3-type-label-medium text-primary">The assistant says</span>
									</div>
									<p class="text-[15px] leading-relaxed text-foreground">
										{#if bullets.length > 0}
											You've got {bullets.length} thing{bullets.length === 1 ? '' : 's'} to look at today — they're listed to the right, most urgent first.
										{:else}
											Nothing urgent on your plate today. Everything's running smoothly across your portfolio.
										{/if}
									</p>
								</div>
							{/if}
						</div>

						<!-- RIGHT: the action list. Severity-ranked, color-coded, deep-linking. -->
						<div class="lg:col-span-2" data-testid="dashboard-briefing-actions">
							<div class="mb-2 flex items-center gap-2">
								{#if bullets.length > 0}
									<ListChecks class="h-4 w-4 text-muted-foreground" />
									<h2 class="text-sm font-semibold text-foreground">
										{bullets.length} thing{bullets.length === 1 ? '' : 's'} need{bullets.length === 1 ? 's' : ''} attention
									</h2>
								{:else if briefingQuery.isError}
									<AlertTriangle class="h-4 w-4 text-muted-foreground" />
									<h2 class="text-sm font-semibold text-foreground">Action items unavailable</h2>
								{:else}
									<CircleCheckBig class="h-4 w-4 text-success" />
									<h2 class="text-sm font-semibold text-foreground">You're all caught up</h2>
								{/if}
							</div>
							{#if bullets.length === 0}
								<p class="text-sm text-muted-foreground">
									{briefingQuery.isError ? "We couldn't load today's action items." : 'No priority items for today.'}
								</p>
							{:else}
								<div class="space-y-2">
									{#each shown as bullet}
										{@const sev = severityStyle(bullet.severity)}
										{@const href = bulletHref(bullet)}
										{#if href}
											<a
												{href}
												class="group relative flex items-start gap-3 overflow-hidden rounded-lg border border-border bg-background py-2 pl-4 pr-3 transition-colors hover:border-border/80 hover:bg-muted/40"
												data-testid="dashboard-briefing-action-item"
											>
												<span class="absolute inset-y-0 left-0 w-1 {sev.rail}"></span>
												<div class="min-w-0 flex-1">
													<div class="flex items-center justify-between gap-2">
														<p class="truncate text-sm font-medium text-foreground">{bullet.title}</p>
														<span class="shrink-0 rounded-full border px-2 py-0.5 text-[10px] font-medium capitalize {sev.pill}">{bullet.severity}</span>
													</div>
													<p class="mt-0.5 line-clamp-2 text-xs text-muted-foreground">{bullet.detail}</p>
												</div>
												<ChevronRight class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground/60 transition-transform group-hover:translate-x-0.5 group-hover:text-muted-foreground" />
											</a>
										{:else}
											<div
												class="relative flex items-start gap-3 overflow-hidden rounded-lg border border-border bg-background py-2 pl-4 pr-3"
												data-testid="dashboard-briefing-action-item"
											>
												<span class="absolute inset-y-0 left-0 w-1 {sev.rail}"></span>
												<div class="min-w-0 flex-1">
													<div class="flex items-center justify-between gap-2">
														<p class="truncate text-sm font-medium text-foreground">{bullet.title}</p>
														<span class="shrink-0 rounded-full border px-2 py-0.5 text-[10px] font-medium capitalize {sev.pill}">{bullet.severity}</span>
													</div>
													<p class="mt-0.5 line-clamp-2 text-xs text-muted-foreground">{bullet.detail}</p>
												</div>
											</div>
										{/if}
									{/each}
									{#if overflow > 0}
										<p class="pt-0.5 text-xs text-muted-foreground" data-testid="dashboard-briefing-overflow">
											+{overflow} more item{overflow === 1 ? '' : 's'}
										</p>
									{/if}
								</div>
							{/if}
						</div>
					</div>
				{/if}
			</Card.Content>
		</Card.Root>

		<!-- Plain-English money snapshot: collected / spent / kept, each with a sentence -->
		<Card.Root class="m3-motion-enter mb-6 gap-0 py-0" style="--m3-motion-index: 2" data-testid="dashboard-money-snapshot">
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
						<div class="rounded-lg border border-border bg-background p-4" data-testid="dashboard-money-collected">
							<div class="flex items-center gap-2 text-sm font-medium text-success">
								<HandCoins class="h-4 w-4" /> Collected
							</div>
							<p class="mt-1 font-mono tabular-nums text-3xl font-bold text-foreground" data-testid="dashboard-money-collected-amount">{money(snap.collected)}</p>
							<p class="mt-1.5 text-sm leading-snug text-muted-foreground">{snap.explanations.collected}</p>
						</div>
						<div class="rounded-lg border border-border bg-background p-4" data-testid="dashboard-money-spent">
							<div class="flex items-center gap-2 text-sm font-medium text-warning">
								<Receipt class="h-4 w-4" /> Spent
							</div>
							<p class="mt-1 font-mono tabular-nums text-3xl font-bold text-foreground" data-testid="dashboard-money-spent-amount">{money(snap.spent)}</p>
							<p class="mt-1.5 text-sm leading-snug text-muted-foreground">{snap.explanations.spent}</p>
						</div>
						<div class="rounded-lg border border-border bg-background p-4" data-testid="dashboard-money-net">
							<div class="flex items-center gap-2 text-sm font-medium {snap.net < 0 ? 'text-destructive' : 'text-success'}">
								<PiggyBank class="h-4 w-4" /> Kept
							</div>
							<p class="mt-1 font-mono tabular-nums text-3xl font-bold text-foreground" data-testid="dashboard-money-net-amount">{money(snap.net)}</p>
							<p class="mt-1.5 text-sm leading-snug text-muted-foreground">{snap.explanations.net}</p>
						</div>
					</div>
					<div
						class="mt-4 flex items-start gap-2 rounded-lg border px-4 py-3 text-sm {snap.pastDueCount > 0 ? 'border-warning/40 bg-warning/10 text-foreground' : 'border-border bg-background text-muted-foreground'}"
						data-testid="dashboard-money-pastdue"
					>
						<AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 {snap.pastDueCount > 0 ? 'text-warning' : 'text-muted-foreground'}" />
						<span>{snap.explanations.pastDue}</span>
					</div>
				{/if}
			</Card.Content>
		</Card.Root>

		<div class="m3-motion-enter mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4" style="--m3-motion-index: 3">
			<Card.Root class="gap-0 py-0">
				<Card.Content class="p-4">
					<div class="flex items-center gap-2 text-muted-foreground"><Building class="h-4 w-4" /> Occupancy</div>
					<p class="mt-2 font-mono tabular-nums text-2xl font-bold">{data.occupancy.occupancyRate}%</p>
					<p class="text-xs text-muted-foreground">{data.occupancy.occupiedUnits}/{data.occupancy.totalUnits} occupied</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="gap-0 py-0">
				<Card.Content class="p-4">
					<div class="flex items-center gap-2 text-warning"><AlertTriangle class="h-4 w-4" /> Overdue</div>
					<p class="mt-2 font-mono tabular-nums text-2xl font-bold">{money(data.accounting.overdueAmount)}</p>
					<p class="text-xs text-muted-foreground">Receivables past due</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="gap-0 py-0">
				<Card.Content class="p-4">
					<div class="flex items-center gap-2 text-success"><Wallet class="h-4 w-4" /> Net This Month</div>
					<p class="mt-2 font-mono tabular-nums text-2xl font-bold">{money(data.accounting.netThisMonth)}</p>
					<p class="text-xs text-muted-foreground">Paid - expenses</p>
				</Card.Content>
			</Card.Root>
			<Card.Root class="gap-0 py-0">
				<Card.Content class="p-4">
					<div class="flex items-center gap-2 text-destructive"><Wrench class="h-4 w-4" /> Open Work Orders</div>
					<p class="mt-2 font-mono tabular-nums text-2xl font-bold">{data.maintenance.openCount}</p>
					<p class="text-xs text-muted-foreground">{data.maintenance.emergencyCount} emergency</p>
				</Card.Content>
			</Card.Root>
		</div>

		<!-- Secondary row: Latest Messages collapses entirely when empty so it never eats prime
		     space; Latest Maintenance then expands to fill the row. -->
		{@const hasMessages = messagesQuery.isLoading || (messagesQuery.data?.length ?? 0) > 0}
		<div class="m3-motion-enter mb-6 grid gap-4 {hasMessages ? 'lg:grid-cols-2' : ''}" style="--m3-motion-index: 4">
			{#if hasMessages}
				<Card.Root class="gap-0 py-0" data-testid="dashboard-latest-messages">
					<Card.Header class="px-4 pt-4 pb-3">
						<Card.Title class="flex items-center gap-2 text-base font-semibold">
							<MessageSquare class="h-4 w-4 text-primary" />
							Latest Messages
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
								{#each (messagesQuery.data ?? []).slice(0, 5) as thread}
									<a href="/messages?conversation={thread.id}" class="block rounded border border-border bg-background px-3 py-2 transition-colors hover:bg-muted/40">
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

			<Card.Root class="gap-0 py-0" data-testid="dashboard-latest-maintenance">
				<Card.Header class="px-4 pt-4 pb-3">
					<Card.Title class="flex items-center gap-2 text-base font-semibold">
						<Wrench class="h-4 w-4 text-primary" />
						Latest Maintenance
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
						<p class="text-sm text-muted-foreground">Maintenance is unavailable.</p>
					{:else}
						<div class="space-y-2">
							{#each (workOrdersQuery.data ?? []).filter((w) => !['Completed', 'Cancelled', 'Archived'].includes(String(w.status))).slice(0, 3) as order}
								<a href="/maintenance/work-orders/{order.id}" class="block rounded border border-border bg-background px-3 py-2 transition-colors hover:bg-muted/40">
									<div class="flex items-center justify-between gap-3">
										<p class="truncate text-sm font-medium">{order.title}</p>
										<span class="shrink-0 rounded-full border px-2 py-0.5 text-[11px] text-muted-foreground">{order.priority}</span>
									</div>
									<p class="mt-1 truncate text-xs text-muted-foreground">{order.status} · {new Date(order.requestedAt).toLocaleDateString()}</p>
								</a>
							{:else}
								<p class="text-sm text-muted-foreground">No open maintenance requests.</p>
							{/each}
						</div>
					{/if}
				</Card.Content>
			</Card.Root>
		</div>

		<div class="m3-motion-enter grid gap-6 lg:grid-cols-3" style="--m3-motion-index: 5">
			<div class="space-y-6 lg:col-span-2">
				<Card.Root class="gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-3">
						<Card.Title class="text-base font-semibold">Leases Expiring in 60 Days</Card.Title>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						{#if data.leasing.expiringSoon.length === 0}
							<p class="text-sm text-muted-foreground">No active leases expiring soon.</p>
						{:else}
							<div class="space-y-2">
								{#each data.leasing.expiringSoon as lease}
									<div class="rounded border border-border bg-background p-3">
										<div class="flex items-center justify-between gap-2">
											<div>
												<p class="text-sm font-medium">{lease.leaseNumber} · {lease.tenant}</p>
												<p class="text-xs text-muted-foreground">{lease.property} · Unit {lease.unit}</p>
											</div>
											<p class="font-mono tabular-nums text-xs text-warning">Ends {new Date(lease.endDate).toLocaleDateString()}</p>
										</div>
									</div>
								{/each}
							</div>
						{/if}
					</Card.Content>
				</Card.Root>

				<Card.Root class="gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-3">
						<Card.Title class="text-base font-semibold">Recent Activity</Card.Title>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						<div class="space-y-2">
							{#each data.recentActivity.slice(0, 8) as activity}
								<div class="rounded border border-border bg-background px-3 py-2 text-sm">
									<p class="text-foreground">{activity.description || activity.action || activity.type}</p>
									<p class="font-mono tabular-nums text-xs text-muted-foreground">{new Date(activity.createdAt).toLocaleString()}</p>
								</div>
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
			</div>

			<div class="space-y-6">
				<Card.Root class="gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-2">
						<Card.Title class="flex items-center gap-2 text-base font-semibold"><CalendarClock class="h-4 w-4 text-primary" />Upcoming Appointments</Card.Title>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						{#if data.upcomingAppointments.length === 0}
							<p class="text-sm text-muted-foreground">No upcoming appointments.</p>
						{:else}
							<div class="space-y-2">
								{#each data.upcomingAppointments as appt}
									<div class="rounded border border-border bg-background p-2 text-sm">
										<p>{appt.title}</p>
										<p class="font-mono tabular-nums text-xs text-muted-foreground">{new Date(appt.scheduledStart).toLocaleString()} · {appt.type}</p>
									</div>
								{/each}
							</div>
						{/if}
					</Card.Content>
				</Card.Root>

				<Card.Root class="gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-2">
						<Card.Title class="flex items-center gap-2 text-base font-semibold"><Home class="h-4 w-4 text-primary" />Leasing Mix</Card.Title>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						<div class="space-y-2 text-sm">
							{#each Object.entries(data.leasing.byStatus) as [status, count]}
								<div class="flex items-center justify-between rounded border border-border bg-background px-2 py-1">
									<span>{status}</span>
									<span class="text-muted-foreground">{count}</span>
								</div>
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
			</div>
		</div>
	{/if}
</div>
