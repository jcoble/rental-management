<script lang="ts">
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { analytics } from '$lib/api/endpoints/analytics';
	import type { AnalyticsOverview, MonthlyPoint, PriorityCount } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { RefreshCw } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const overviewQuery = createQuery(() => ({
		queryKey: ['analytics', portfolioId],
		queryFn: () => analytics.overview(),
		staleTime: 60_000
	}));

	const overview = $derived(overviewQuery.data as AnalyticsOverview | undefined);

	function money(value: number): string {
		return new Intl.NumberFormat('en-US', {
			style: 'currency',
			currency: 'USD',
			maximumFractionDigits: 0
		}).format(value || 0);
	}

	function pct(value: number): string {
		return `${Math.round(value || 0)}%`;
	}

	function refresh() {
		queryClient.invalidateQueries({ queryKey: ['analytics', portfolioId] });
	}

	// ---- SVG trend chart helpers ----
	const CHART_W = 600;
	const CHART_H = 180;
	const PAD_LEFT = 48;
	const PAD_RIGHT = 8;
	const PAD_TOP = 12;
	const PAD_BOTTOM = 32;
	const PLOT_W = CHART_W - PAD_LEFT - PAD_RIGHT;
	const PLOT_H = CHART_H - PAD_TOP - PAD_BOTTOM;

	function buildChart(trend: MonthlyPoint[]): {
		bars: { x: number; incomeH: number; expH: number; month: string; income: number; expenses: number }[];
		yLabels: { y: number; value: number }[];
	} {
		if (!trend || trend.length === 0) return { bars: [], yLabels: [] };

		const maxVal = Math.max(...trend.map((p) => Math.max(p.income, p.expenses)), 1);
		// Round up to a nice number
		const magnitude = Math.pow(10, Math.floor(Math.log10(maxVal)));
		const niceMax = Math.ceil(maxVal / magnitude) * magnitude;

		const n = trend.length;
		const groupW = PLOT_W / n;
		const barW = Math.max(4, groupW * 0.35);
		const gap = Math.max(1, groupW * 0.06);

		const bars = trend.map((p, i) => {
			const cx = PAD_LEFT + i * groupW + groupW / 2;
			const incomeH = (p.income / niceMax) * PLOT_H;
			const expH = (p.expenses / niceMax) * PLOT_H;
			return {
				x: cx,
				incomeH,
				expH,
				month: p.month.slice(5), // "MM"
				income: p.income,
				expenses: p.expenses
			};
		});

		// 4 y-axis gridlines
		const yLabels = [0, 0.25, 0.5, 0.75, 1].map((t) => ({
			y: PAD_TOP + PLOT_H * (1 - t),
			value: Math.round(niceMax * t)
		}));

		return { bars, yLabels };
	}

	const chartData = $derived(buildChart(overview?.trend ?? []));

	// Month abbreviations for x-axis labels
	const MONTH_ABB = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
	function monthLabel(mm: string): string {
		const idx = parseInt(mm, 10) - 1;
		return MONTH_ABB[idx] ?? mm;
	}

	// Priority sort order for work orders
	const PRIORITY_ORDER: Record<string, number> = { Emergency: 0, High: 1, Normal: 2, Low: 3 };
	const sortedWorkOrders = $derived(
		[...(overview?.openWorkOrders ?? [])].sort(
			(a, b) => (PRIORITY_ORDER[a.priority] ?? 9) - (PRIORITY_ORDER[b.priority] ?? 9)
		)
	);

	const woMax = $derived(Math.max(...(sortedWorkOrders.map((w) => w.count) ?? [1]), 1));

	const PRIORITY_COLOR: Record<string, string> = {
		Emergency: 'bg-destructive',
		High: 'bg-orange-500',
		Normal: 'bg-primary',
		Low: 'bg-muted-foreground'
	};
</script>

<svelte:head>
	<title>Insights - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="analytics-page">
	<div class="mb-5 flex items-center justify-between">
		<div>
			<h1 class="text-2xl font-bold">Insights</h1>
			<p class="text-sm text-muted-foreground">Portfolio performance at a glance.</p>
		</div>
		<Button
			variant="outline"
			size="sm"
			onclick={refresh}
			disabled={overviewQuery.isFetching}
			data-testid="analytics-refresh"
		>
			<RefreshCw class="h-3.5 w-3.5 {overviewQuery.isFetching ? 'animate-spin' : ''}" />
			Refresh
		</Button>
	</div>

	{#if overviewQuery.isLoading}
		<!-- Skeleton loading state -->
		<div class="mb-5 grid gap-4 sm:grid-cols-2 lg:grid-cols-4" data-testid="analytics-loading">
			{#each [0, 1, 2, 3] as _}
				<Card.Root class="gap-0 py-0">
					<Card.Content class="p-4">
						<div class="h-3 w-24 animate-pulse rounded bg-muted"></div>
						<div class="mt-2 h-8 w-28 animate-pulse rounded bg-muted"></div>
						<div class="mt-1.5 h-3 w-32 animate-pulse rounded bg-muted"></div>
					</Card.Content>
				</Card.Root>
			{/each}
		</div>
		<div class="grid gap-4 lg:grid-cols-3">
			<Card.Root class="gap-0 py-0 lg:col-span-2">
				<Card.Header class="border-b border-border px-4 py-3">
					<div class="h-5 w-36 animate-pulse rounded bg-muted"></div>
				</Card.Header>
				<Card.Content class="p-4">
					<div class="h-44 w-full animate-pulse rounded bg-muted"></div>
				</Card.Content>
			</Card.Root>
			<div class="flex flex-col gap-4">
				<Card.Root class="gap-0 py-0">
					<Card.Header class="border-b border-border px-4 py-3">
						<div class="h-5 w-32 animate-pulse rounded bg-muted"></div>
					</Card.Header>
					<Card.Content class="p-4">
						<div class="space-y-3">
							{#each [0, 1, 2] as _}
								<div class="h-5 w-full animate-pulse rounded bg-muted"></div>
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
				<Card.Root class="gap-0 py-0">
					<Card.Header class="border-b border-border px-4 py-3">
						<div class="h-5 w-36 animate-pulse rounded bg-muted"></div>
					</Card.Header>
					<Card.Content class="p-4">
						<div class="space-y-3">
							{#each [0, 1, 2] as _}
								<div class="h-7 w-full animate-pulse rounded bg-muted"></div>
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
			</div>
		</div>
	{:else if overviewQuery.isError}
		<p class="py-16 text-center text-sm text-destructive" data-testid="analytics-error">
			Failed to load insights. Check your connection or try refreshing.
		</p>
	{:else if !overview}
		<p class="py-16 text-center text-sm text-muted-foreground" data-testid="analytics-empty">
			No data available yet.
		</p>
	{:else}
		<!-- KPI Cards -->
		<div class="mb-5 grid gap-4 sm:grid-cols-2 lg:grid-cols-4" data-testid="analytics-kpi-row">
			<!-- Occupancy -->
			<Card.Root class="gap-0 py-0" data-testid="kpi-occupancy">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Occupancy</p>
					<p class="font-mono tabular-nums text-2xl font-bold">{pct(overview.occupancyRate)}</p>
					<p class="mt-0.5 text-xs text-muted-foreground">
						{overview.occupiedUnits} / {overview.totalUnits} units
					</p>
				</Card.Content>
			</Card.Root>

			<!-- Collection Rate -->
			<Card.Root class="gap-0 py-0" data-testid="kpi-collection">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Collection Rate</p>
					<p class="font-mono tabular-nums text-2xl font-bold">{pct(overview.collectionRate)}</p>
					<p class="mt-0.5 text-xs text-muted-foreground">
						{money(overview.monthRentCollected)} of {money(overview.monthRentScheduled)}
					</p>
				</Card.Content>
			</Card.Root>

			<!-- Overdue -->
			<Card.Root class="gap-0 py-0" data-testid="kpi-overdue">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Overdue</p>
					<p class="font-mono tabular-nums text-2xl font-bold text-destructive">{money(overview.overdue.amount)}</p>
					<p class="mt-0.5 text-xs text-muted-foreground">
						{overview.overdue.count} payment{overview.overdue.count !== 1 ? 's' : ''}
					</p>
				</Card.Content>
			</Card.Root>

			<!-- Monthly Recurring Rent -->
			<Card.Root class="gap-0 py-0" data-testid="kpi-mrr">
				<Card.Content class="p-4">
					<p class="text-xs text-muted-foreground">Monthly Recurring Rent</p>
					<p class="font-mono tabular-nums text-2xl font-bold">{money(overview.monthlyRecurringRent)}</p>
					<p class="mt-0.5 text-xs text-muted-foreground">Across active leases</p>
				</Card.Content>
			</Card.Root>
		</div>

		<!-- Trend Chart + Lease Pipeline + Work Orders -->
		<div class="grid gap-4 lg:grid-cols-3">
			<!-- 12-month Income vs Expenses Trend (spans 2 cols on lg) -->
			<Card.Root class="gap-0 py-0 lg:col-span-2" data-testid="analytics-trend-card">
				<Card.Header class="border-b border-border px-4 py-3">
					<Card.Title class="text-base font-semibold">12-Month Trend</Card.Title>
				</Card.Header>
				<Card.Content class="p-4">
					{#if !overview.trend || overview.trend.length === 0}
						<p class="py-8 text-center text-sm text-muted-foreground">No trend data yet.</p>
					{:else}
						<!-- Legend -->
						<div class="mb-3 flex items-center gap-4 text-xs text-muted-foreground">
							<span class="flex items-center gap-1.5">
								<span class="inline-block h-2.5 w-2.5 rounded-sm bg-primary"></span>
								Income
							</span>
							<span class="flex items-center gap-1.5">
								<span class="inline-block h-2.5 w-2.5 rounded-sm bg-orange-400"></span>
								Expenses
							</span>
						</div>
						<!-- Responsive SVG chart -->
						<div class="w-full overflow-x-auto">
							<svg
								viewBox="0 0 {CHART_W} {CHART_H}"
								class="w-full"
								style="min-width: 320px; height: auto;"
								role="img"
								aria-label="12-month income vs expenses bar chart"
							>
								<!-- Y-axis gridlines + labels -->
								{#each chartData.yLabels as yl}
									<line
										x1={PAD_LEFT}
										y1={yl.y}
										x2={CHART_W - PAD_RIGHT}
										y2={yl.y}
										stroke="currentColor"
										stroke-opacity="0.08"
										stroke-width="1"
									/>
									<text
										x={PAD_LEFT - 4}
										y={yl.y + 4}
										text-anchor="end"
										font-size="9"
										fill="currentColor"
										opacity="0.45"
									>
										{yl.value >= 1000 ? `${Math.round(yl.value / 1000)}k` : yl.value}
									</text>
								{/each}

								<!-- Bars -->
								{#each chartData.bars as bar, i}
									{@const barW = Math.max(4, (PLOT_W / chartData.bars.length) * 0.35)}
									{@const gap = Math.max(1, (PLOT_W / chartData.bars.length) * 0.06)}
									<!-- Income bar -->
									<rect
										x={bar.x - barW - gap / 2}
										y={PAD_TOP + PLOT_H - bar.incomeH}
										width={barW}
										height={Math.max(bar.incomeH, 0)}
										rx="1.5"
										class="fill-primary"
										opacity="0.85"
									/>
									<!-- Expenses bar -->
									<rect
										x={bar.x + gap / 2}
										y={PAD_TOP + PLOT_H - bar.expH}
										width={barW}
										height={Math.max(bar.expH, 0)}
										rx="1.5"
										class="fill-orange-400"
										opacity="0.85"
									/>
									<!-- X-axis month label (every other label if many bars) -->
									{#if chartData.bars.length <= 6 || i % 2 === 0}
										<text
											x={bar.x}
											y={PAD_TOP + PLOT_H + 16}
											text-anchor="middle"
											font-size="9"
											fill="currentColor"
											opacity="0.5"
										>
											{monthLabel(bar.month)}
										</text>
									{/if}
								{/each}
							</svg>
						</div>
					{/if}
				</Card.Content>
			</Card.Root>

			<!-- Right column: Lease Pipeline + Work Orders stacked -->
			<div class="flex flex-col gap-4">
				<!-- Lease Expiry Pipeline -->
				<Card.Root class="gap-0 py-0" data-testid="analytics-lease-expiry-card">
					<Card.Header class="border-b border-border px-4 py-3">
						<Card.Title class="text-base font-semibold">Leases Expiring</Card.Title>
					</Card.Header>
					<Card.Content class="p-4">
						<div class="space-y-3">
							<div class="flex items-center justify-between">
								<span class="text-sm text-muted-foreground">Within 30 days</span>
								<span
									class="text-sm font-semibold {overview.leasesExpiring30 > 0
										? 'text-destructive'
										: 'text-foreground'}"
									data-testid="leases-expiring-30">{overview.leasesExpiring30}</span
								>
							</div>
							<div class="flex items-center justify-between">
								<span class="text-sm text-muted-foreground">Within 60 days</span>
								<span
									class="text-sm font-semibold {overview.leasesExpiring60 > 0
										? 'text-orange-500'
										: 'text-foreground'}"
									data-testid="leases-expiring-60">{overview.leasesExpiring60}</span
								>
							</div>
							<div class="flex items-center justify-between">
								<span class="text-sm text-muted-foreground">Within 90 days</span>
								<span class="text-sm font-semibold text-foreground" data-testid="leases-expiring-90"
									>{overview.leasesExpiring90}</span
								>
							</div>
						</div>
					</Card.Content>
				</Card.Root>

				<!-- Open Work Orders by Priority -->
				<Card.Root class="gap-0 py-0" data-testid="analytics-workorders-card">
					<Card.Header class="border-b border-border px-4 py-3">
						<Card.Title class="text-base font-semibold">Open Work Orders</Card.Title>
					</Card.Header>
					<Card.Content class="p-4">
						{#if sortedWorkOrders.length === 0}
							<p class="py-2 text-center text-sm text-muted-foreground">None open.</p>
						{:else}
							<div class="space-y-2">
								{#each sortedWorkOrders as wo}
									<div class="space-y-1">
										<div class="flex items-center justify-between text-sm">
											<span class="text-muted-foreground">{wo.priority}</span>
											<span class="font-semibold">{wo.count}</span>
										</div>
										<div class="h-1.5 w-full overflow-hidden rounded-full bg-muted">
											<div
												class="h-full rounded-full {PRIORITY_COLOR[wo.priority] ?? 'bg-primary'}"
												style="width: {Math.round((wo.count / woMax) * 100)}%"
											></div>
										</div>
									</div>
								{/each}
							</div>
						{/if}
					</Card.Content>
				</Card.Root>
			</div>
		</div>
	{/if}
</div>
