<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import type { Dashboard } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Home, AlertTriangle, CalendarClock, Wallet, Wrench, Building } from '@lucide/svelte';

	const dashboardQuery = createQuery(() => ({
		queryKey: ['dashboard', getCurrentPortfolioId()],
		queryFn: () => portfolios.dashboard(getCurrentPortfolioId()),
	}));

	function money(value: number) {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(value || 0);
	}
</script>

<svelte:head>
	<title>Dashboard - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	{#if dashboardQuery.isLoading}
		<div class="flex h-64 items-center justify-center text-muted-foreground">Loading dashboard...</div>
	{:else if dashboardQuery.isError}
		<div class="flex h-64 items-center justify-center text-destructive">Failed to load dashboard.</div>
	{:else if dashboardQuery.data}
		{@const data = dashboardQuery.data as Dashboard}
		<div class="mb-6">
			<h1 class="text-2xl font-bold text-foreground">{data.portfolio.name}</h1>
			<p class="mt-1 text-sm text-muted-foreground">{data.portfolio.managementCompanyName} · {data.portfolio.timeZone}</p>
		</div>

		<div class="mb-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
			<div class="rounded-lg border border-border bg-card p-4">
				<div class="flex items-center gap-2 text-muted-foreground"><Building class="h-4 w-4" /> Occupancy</div>
				<p class="mt-2 text-2xl font-bold">{data.occupancy.occupancyRate}%</p>
				<p class="text-xs text-muted-foreground">{data.occupancy.occupiedUnits}/{data.occupancy.totalUnits} occupied</p>
			</div>
			<div class="rounded-lg border border-border bg-card p-4">
				<div class="flex items-center gap-2 text-warning"><AlertTriangle class="h-4 w-4" /> Overdue</div>
				<p class="mt-2 text-2xl font-bold">{money(data.accounting.overdueAmount)}</p>
				<p class="text-xs text-muted-foreground">Receivables past due</p>
			</div>
			<div class="rounded-lg border border-border bg-card p-4">
				<div class="flex items-center gap-2 text-success"><Wallet class="h-4 w-4" /> Net This Month</div>
				<p class="mt-2 text-2xl font-bold">{money(data.accounting.netThisMonth)}</p>
				<p class="text-xs text-muted-foreground">Paid - expenses</p>
			</div>
			<div class="rounded-lg border border-border bg-card p-4">
				<div class="flex items-center gap-2 text-destructive"><Wrench class="h-4 w-4" /> Open Work Orders</div>
				<p class="mt-2 text-2xl font-bold">{data.maintenance.openCount}</p>
				<p class="text-xs text-muted-foreground">{data.maintenance.emergencyCount} emergency</p>
			</div>
		</div>

		<div class="grid gap-6 lg:grid-cols-3">
			<div class="space-y-6 lg:col-span-2">
				<div class="rounded-lg border border-border bg-card p-4">
					<h2 class="mb-3 font-semibold">Leases Expiring in 60 Days</h2>
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
										<p class="text-xs text-warning">Ends {new Date(lease.endDate).toLocaleDateString()}</p>
									</div>
								</div>
							{/each}
						</div>
					{/if}
				</div>

				<div class="rounded-lg border border-border bg-card p-4">
					<h2 class="mb-3 font-semibold">Recent Activity</h2>
					<div class="space-y-2">
						{#each data.recentActivity.slice(0, 8) as activity}
							<div class="rounded border border-border bg-background px-3 py-2 text-sm">
								<p class="text-foreground">{activity.description || activity.action || activity.type}</p>
								<p class="text-xs text-muted-foreground">{new Date(activity.createdAt).toLocaleString()}</p>
							</div>
						{/each}
					</div>
				</div>
			</div>

			<div class="space-y-6">
				<div class="rounded-lg border border-border bg-card p-4">
					<div class="mb-2 flex items-center gap-2"><CalendarClock class="h-4 w-4 text-primary" /><h2 class="font-semibold">Upcoming Appointments</h2></div>
					{#if data.upcomingAppointments.length === 0}
						<p class="text-sm text-muted-foreground">No upcoming appointments.</p>
					{:else}
						<div class="space-y-2">
							{#each data.upcomingAppointments as appt}
								<div class="rounded border border-border bg-background p-2 text-sm">
									<p>{appt.title}</p>
									<p class="text-xs text-muted-foreground">{new Date(appt.scheduledStart).toLocaleString()} · {appt.type}</p>
								</div>
							{/each}
						</div>
					{/if}
				</div>

				<div class="rounded-lg border border-border bg-card p-4">
					<div class="mb-2 flex items-center gap-2"><Home class="h-4 w-4 text-primary" /><h2 class="font-semibold">Leasing Mix</h2></div>
					<div class="space-y-2 text-sm">
						{#each Object.entries(data.leasing.byStatus) as [status, count]}
							<div class="flex items-center justify-between rounded border border-border bg-background px-2 py-1">
								<span>{status}</span>
								<span class="text-muted-foreground">{count}</span>
							</div>
						{/each}
					</div>
				</div>
			</div>
		</div>
	{/if}
</div>
