<script lang="ts">
	import type { UnitDashboard } from '$lib/types';
	import { money } from '../money';
	import { formatDateOnly } from '$lib/utils/date';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Home, DollarSign, Wrench, FileText, CalendarClock, User } from '@lucide/svelte';

	let {
		dashboard,
		onOpenTab,
	}: {
		dashboard: UnitDashboard;
		/** Jump to another tab (used by "view all" links on each card). */
		onOpenTab: (tab: string) => void;
	} = $props();

	const o = $derived(dashboard.overview);
	const unit = $derived(dashboard.unit);
	const lease = $derived(dashboard.currentLease);
	const tenant = $derived(dashboard.currentTenant);
	const currentTenants = $derived(dashboard.currentTenants?.length ? dashboard.currentTenants : tenant ? [tenant] : []);
</script>

<div class="space-y-4" data-testid="unit-overview-tab">
	{#if dashboard.nextBestAction?.label}
		<a
			href={dashboard.nextBestAction.href}
			class="flex items-center justify-between gap-3 rounded-xl border border-primary/25 bg-primary/5 px-4 py-3 transition hover:border-primary/50 hover:bg-primary/10"
			data-testid="next-best-action"
		>
			<span><span class="block text-xs font-medium uppercase tracking-wide text-primary">Next action</span><span class="font-medium">{dashboard.nextBestAction.label}</span></span>
			<span aria-hidden="true">›</span>
		</a>
	{/if}
	<section class="rounded-xl border bg-card p-4" data-testid="unit-condition-families">
		<h2 class="mb-3 font-semibold">Unit conditions</h2>
		<dl class="grid gap-3 text-sm sm:grid-cols-2 xl:grid-cols-5">
			<div data-testid="condition-occupancy-possession"><dt class="text-muted-foreground">Occupancy / possession</dt><dd class="font-medium">{dashboard.occupancyPossession.status}</dd></div>
			<div data-testid="condition-marketing-availability"><dt class="text-muted-foreground">Marketing availability</dt><dd class="font-medium">{dashboard.marketingAvailability.status}</dd></div>
			<div data-testid="condition-tenant-account"><dt class="text-muted-foreground">TenantAccount</dt><dd class="font-medium">{dashboard.tenantAccountCondition.status}</dd></div>
			<div data-testid="condition-legal-notice"><dt class="text-muted-foreground">Legal / notice</dt><dd class="font-medium">{dashboard.legalNoticeCondition.status}</dd></div>
			<div data-testid="condition-maintenance-turnover"><dt class="text-muted-foreground">Maintenance / turnover</dt><dd class="font-medium">{dashboard.maintenanceTurnover.status}</dd></div>
		</dl>
	</section>
	<div class="grid gap-4 lg:grid-cols-2">
	<!-- Snapshot -->
	<DetailCard title="Snapshot" icon={Home} accent="primary" testid="overview-snapshot">
		<dl class="grid grid-cols-2 gap-3 text-sm">
			<div><dt class="text-muted-foreground">Beds / Baths</dt><dd class="font-medium">{unit.bedrooms} / {unit.bathrooms}</dd></div>
			<div><dt class="text-muted-foreground">Market rent</dt><dd class="font-medium">{money(unit.marketRent)}</dd></div>
			{#if unit.squareFeet}<div><dt class="text-muted-foreground">Size</dt><dd class="font-medium">{unit.squareFeet} sqft</dd></div>{/if}
			{#if unit.floorPlan}<div><dt class="text-muted-foreground">Floor plan</dt><dd class="font-medium">{unit.floorPlan}</dd></div>{/if}
			<div><dt class="text-muted-foreground">Status</dt><dd><StatusBadge status={unit.status} /></dd></div>
		</dl>
	</DetailCard>

	<!-- Tenant + lease -->
	<DetailCard title="Tenant & lease" icon={User} accent="success" testid="overview-tenant-lease">
		{#if dashboard.leaseManagementId}
			<div class="space-y-2 text-sm">
				{#if currentTenants.length}
					<div class="flex items-center justify-between gap-3">
						<div class="min-w-0 space-y-1">
							{#each currentTenants as currentTenant}
								<div>
									<p class="truncate font-medium">{currentTenant.name}</p>
									{#if currentTenant.email || currentTenant.phone}
										<p class="truncate text-muted-foreground">{currentTenant.email || currentTenant.phone}</p>
									{/if}
								</div>
							{/each}
						</div>
						<button type="button" class="shrink-0 text-xs text-primary hover:underline" onclick={() => onOpenTab('tenant-lease')}>View lease</button>
					</div>
				{/if}
				{#if lease}
					<dl class="grid grid-cols-2 gap-2 border-t pt-2">
						<div><dt class="text-muted-foreground">Agreement</dt><dd class="font-medium">{lease.leaseNumber}</dd></div>
						<div><dt class="text-muted-foreground">Rent</dt><dd class="font-medium">{money(lease.monthlyRent)}</dd></div>
						<div><dt class="text-muted-foreground">Start</dt><dd>{formatDateOnly(lease.startDate)}</dd></div>
						<div><dt class="text-muted-foreground">End</dt><dd>{formatDateOnly(lease.endDate)}</dd></div>
					</dl>
				{:else}
					<p class="border-t pt-2 text-muted-foreground">Possession is active for this tenant relationship. No governing Agreement is on file.</p>
				{/if}
			</div>
		{:else}
			<p class="text-sm text-muted-foreground">No current tenant relationship for this unit.</p>
		{/if}
	</DetailCard>

	<!-- Rent -->
	<DetailCard title="Rent" icon={DollarSign} accent="warning" testid="overview-rent">
		<div class="mb-2 flex items-center justify-between">
			<span class="text-sm text-muted-foreground">Outstanding</span>
			<span class="text-lg font-semibold">{money(dashboard.header.outstandingRentBalance)}</span>
		</div>
		{#if o.recentPayments.length === 0}
			<p class="text-sm text-muted-foreground">No payments recorded.</p>
		{:else}
			<ul class="divide-y text-sm">
				{#each o.recentPayments as p (p.id)}
					<li class="flex items-center justify-between py-1.5">
						<span>{formatDateOnly(p.dueDate)} · {p.type}</span>
						<span class="flex items-center gap-2"><StatusBadge status={p.status} />{money(p.amount)}</span>
					</li>
				{/each}
			</ul>
			<button type="button" class="mt-2 text-xs text-primary hover:underline" onclick={() => onOpenTab('money')}>Open ledger</button>
		{/if}
	</DetailCard>

	<!-- Open repairs -->
	<DetailCard title="Open repairs" icon={Wrench} accent="destructive" testid="overview-repairs">
		{#if o.openWorkOrders.length === 0}
			<p class="text-sm text-muted-foreground">No open work orders.</p>
		{:else}
			<ul class="divide-y text-sm">
				{#each o.openWorkOrders as w (w.id)}
					<li class="flex items-center justify-between py-1.5">
						<span class="truncate">{w.title}</span>
						<span class="flex shrink-0 items-center gap-2"><StatusBadge status={w.priority} /><StatusBadge status={w.status} /></span>
					</li>
				{/each}
			</ul>
			<button type="button" class="mt-2 text-xs text-primary hover:underline" onclick={() => onOpenTab('maintenance')}>Open maintenance</button>
		{/if}
	</DetailCard>

	<!-- Docs awaiting review -->
	<DetailCard title="Documents" icon={FileText} accent="primary" testid="overview-docs">
		{#if o.pendingDocs.length === 0}
			<p class="text-sm text-muted-foreground">No documents on file.</p>
		{:else}
			<ul class="divide-y text-sm">
				{#each o.pendingDocs as d (d.id)}
					<li class="flex items-center justify-between py-1.5">
						<span class="truncate">{d.fileName}</span>
						{#if d.entityType}<span class="shrink-0 text-xs text-muted-foreground">{d.entityType}</span>{/if}
					</li>
				{/each}
			</ul>
			<button type="button" class="mt-2 text-xs text-primary hover:underline" onclick={() => onOpenTab('documents-history')}>Open documents</button>
		{/if}
	</DetailCard>

	<!-- Upcoming appointments -->
	<DetailCard title="Upcoming" icon={CalendarClock} accent="muted" testid="overview-appointments">
		{#if o.upcomingAppointments.length === 0}
			<p class="text-sm text-muted-foreground">No upcoming appointments.</p>
		{:else}
			<ul class="divide-y text-sm">
				{#each o.upcomingAppointments as a (a.id)}
					<li class="flex items-center justify-between py-1.5">
						<span class="truncate">{a.title} · {a.type}</span>
						<span class="shrink-0 text-muted-foreground">{formatDateOnly(a.scheduledStart)}</span>
					</li>
				{/each}
			</ul>
		{/if}
	</DetailCard>
	</div>
</div>
