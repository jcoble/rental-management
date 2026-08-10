<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { accounting } from '$lib/api/endpoints/accounting';
	import { messages } from '$lib/api/endpoints/messages';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import type { Dashboard, DashboardActivity } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Home, AlertTriangle, CalendarClock, Wallet, Wrench, Building, MessageSquare, HandCoins, Receipt, PiggyBank, ArrowRight, ChevronRight } from '@lucide/svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import GettingStartedCard from '$lib/components/onboarding/GettingStartedCard.svelte';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { labelForType } from './appointments/calendar-utils';
	import DashboardBriefing from './DashboardBriefing.svelte';
	import { dashboardActivityHref } from '$lib/navigation/dashboard-activity-href';
	import { recordHref } from '$lib/navigation/record-href';

	const dashboardQuery = createQuery(() => ({
		queryKey: ['dashboard', getCurrentPortfolioId()],
		queryFn: () => portfolios.dashboard(getCurrentPortfolioId()),
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

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(value || 0);
	}

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
		<div class="mx-auto flex min-h-64 max-w-xl flex-col items-center justify-center rounded-xl border border-destructive/40 bg-destructive/5 p-6 text-center" role="alert" data-testid="dashboard-error">
			<p class="font-medium text-destructive" data-testid="dashboard-error-title">Could not load dashboard.</p>
			<p class="mt-1 text-sm text-muted-foreground" data-testid="dashboard-error-description">Try again. Your portfolio data has not been changed.</p>
			<div class="mt-4 flex flex-wrap justify-center gap-2" data-testid="dashboard-error-actions">
				<Button variant="outline" onclick={() => dashboardQuery.refetch()} data-testid="dashboard-retry">Try again</Button>
				<Button href="/properties" variant="ghost" data-testid="dashboard-properties-link">Open properties</Button>
			</div>
		</div>
	{:else if dashboardQuery.data}
		{@const data = dashboardQuery.data as Dashboard}
		<Card.Root
			class="m3-dashboard-hero m3-dashboard-art m3-dashboard-art--portfolio m3-surface-art m3-surface-art--hero m3-art-09 m3-motion-enter relative mb-6 overflow-hidden p-0"
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
							<p class="m3-type-body-medium text-muted-foreground">Portfolio pulse</p>
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

		<!-- The selected portfolio is the dashboard's primary context and stays first. The briefing
		     keeps its own request and loading state, but follows the portfolio card in the visual order. -->
		<DashboardBriefing />

		<!-- Persistent "Getting started" checklist nudge. Self-managing: shows progress + the next steps
		     (each deep-links and spotlights the exact control) and hides once everything is done. Works
		     in both Sandbox and Live; supersedes the old empty-only "finish setup" banner. -->
		{#if canOpenGettingStarted}
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
							<div class="flex items-center gap-2 text-sm font-medium text-success" title="All payments received — rent, deposits, and fees">
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
						<!-- Actionable: drills into the "Who's behind" list (Mark paid / Text), mirroring the
						     mobile OverdueScreen. The list reads the SAME GET /accounting/past-due source as this
						     KPI, so its row count always equals snap.pastDueCount. -->
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

		<div class="m3-motion-enter mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4" style="--m3-motion-index: 3">
			<Card.Root class="m3-tonal-card m3-tonal-card--sky gap-0 py-0">
				<Card.Content class="p-4">
					<div class="flex items-center gap-2 text-muted-foreground"><Building class="h-4 w-4" /> Occupancy</div>
					<p class="mt-2 font-mono tabular-nums text-2xl font-bold">{data.occupancy.occupancyRate}%</p>
					<p class="text-xs text-muted-foreground">{data.occupancy.occupiedUnits}/{data.occupancy.totalUnits} occupied</p>
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
					<div class="flex items-center gap-2 text-destructive"><Wrench class="h-4 w-4" /> Open Work Orders</div>
					<p class="mt-2 font-mono tabular-nums text-2xl font-bold">{data.maintenance.openCount}</p>
					<p class="text-xs text-muted-foreground">{data.maintenance.emergencyCount} emergency</p>
				</Card.Content>
			</Card.Root>
		</div>

		<!-- Secondary row: Latest Messages collapses entirely when empty so it never eats prime
		     space; Latest Work Orders then expands to fill the row. -->
		{@const hasMessages = messagesQuery.isLoading || (messagesQuery.data?.items.length ?? 0) > 0}
		<div class="m3-motion-enter mb-6 grid gap-4 {hasMessages ? 'lg:grid-cols-2' : ''}" style="--m3-motion-index: 4">
			{#if hasMessages}
				<Card.Root class="m3-tonal-card m3-tonal-card--violet gap-0 py-0" data-testid="dashboard-latest-messages">
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
						Latest Work Orders
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
						<p class="text-sm text-muted-foreground">Work orders are unavailable.</p>
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
								<p class="text-sm text-muted-foreground">No open work orders.</p>
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
						<Card.Title class="text-base font-semibold">Recent Activity</Card.Title>
					</Card.Header>
					<Card.Content class="px-4 pb-4 pt-0">
						<div class="space-y-2">
							{#snippet activityBody(activity: DashboardActivity)}
								<p class="text-foreground">
									{activity.description || activity.action || activity.type}{#if activity.label}<span class="text-muted-foreground"> — {activity.label}</span>{/if}
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
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
			</div>

			<div class="space-y-6">
				<Card.Root class="m3-tonal-card m3-tonal-card--rose gap-0 py-0">
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
										<p class="font-mono tabular-nums text-xs text-muted-foreground">{new Date(appt.scheduledStart).toLocaleString()} · {labelForType(appt.type)}</p>
									</div>
								{/each}
							</div>
						{/if}
					</Card.Content>
				</Card.Root>

				<Card.Root class="m3-tonal-card m3-tonal-card--mint gap-0 py-0">
					<Card.Header class="px-4 pt-4 pb-2">
						<Card.Title class="flex items-center gap-2 text-base font-semibold"><Home class="h-4 w-4 text-primary" />Leasing Mix</Card.Title>
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
	{/if}
</div>
