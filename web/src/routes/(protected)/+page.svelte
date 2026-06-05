<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { accounting } from '$lib/api/endpoints/accounting';
	import { ai } from '$lib/api/endpoints/ai';
	import { messages } from '$lib/api/endpoints/messages';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { properties } from '$lib/api/endpoints/properties';
	import type { Dashboard } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Home, AlertTriangle, CalendarClock, Wallet, Wrench, Building, Sparkles, MessageSquare, HandCoins, Receipt, PiggyBank, Rocket, ArrowRight } from '@lucide/svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';

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
</script>

<svelte:head>
	<title>Dashboard - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20">
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
		<div class="mb-6">
			<h1 class="text-2xl font-bold text-foreground">{data.portfolio.name}</h1>
			<p class="mt-1 text-sm text-muted-foreground">{data.portfolio.managementCompanyName} · {data.portfolio.timeZone}</p>
		</div>

		{#if isEmptyPortfolio && !isSandbox}
			<!-- Empty-portfolio entry point into the guided setup wizard (live accounts only) -->
			<Card.Root class="mb-6 border-primary/40 bg-primary/5" data-testid="dashboard-onboarding-banner">
				<Card.Content class="flex flex-col gap-4 p-5 sm:flex-row sm:items-center sm:justify-between">
					<div class="flex items-start gap-3">
						<div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-primary/15 text-primary">
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

		<!-- Plain-English money snapshot: collected / spent / kept, each with a sentence -->
		<Card.Root class="mb-6 gap-0 py-0" data-testid="dashboard-money-snapshot">
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

		<div class="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
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

		<div class="mb-6 grid gap-4 lg:grid-cols-2">
			<Card.Root class="gap-0 py-0" data-testid="dashboard-todays-briefing">
				<Card.Header class="px-4 pt-4 pb-3">
					<Card.Title class="flex items-center gap-2 text-base font-semibold">
						<Sparkles class="h-4 w-4 text-primary" />
						Today's Briefing
					</Card.Title>
				</Card.Header>
				<Card.Content class="px-4 pb-4 pt-0">
					{#if briefingQuery.isLoading}
						<div class="space-y-2">
							<div class="h-4 w-3/4 animate-pulse rounded bg-muted"></div>
							<div class="h-14 w-full animate-pulse rounded bg-muted"></div>
						</div>
					{:else if briefingQuery.isError}
						<p class="text-sm text-muted-foreground">Briefing is unavailable.</p>
					{:else}
						{@const briefing = briefingQuery.data}
						{#if briefing?.summary}
							<p class="mb-3 text-sm text-muted-foreground">{briefing.summary}</p>
						{/if}
						<div class="space-y-2">
							{#each (briefing?.bullets ?? []).slice(0, 5) as bullet}
								<div class="rounded border border-border bg-background px-3 py-2">
									<div class="flex items-center justify-between gap-3">
										<p class="text-sm font-medium">{bullet.title}</p>
										<span class="rounded-full border px-2 py-0.5 text-[11px] capitalize text-muted-foreground">{bullet.severity}</span>
									</div>
									<p class="mt-1 text-xs text-muted-foreground">{bullet.detail}</p>
								</div>
							{:else}
								<p class="text-sm text-muted-foreground">No priority items for today.</p>
							{/each}
						</div>
					{/if}
				</Card.Content>
			</Card.Root>

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
					{:else if messagesQuery.isError}
						<p class="text-sm text-muted-foreground">Messages are unavailable.</p>
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
							{:else}
								<p class="text-sm text-muted-foreground">No recent messages.</p>
							{/each}
						</div>
					{/if}
				</Card.Content>
			</Card.Root>

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

		<div class="grid gap-6 lg:grid-cols-3">
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
