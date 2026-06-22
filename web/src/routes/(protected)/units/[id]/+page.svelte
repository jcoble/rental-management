<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { createQuery } from '@tanstack/svelte-query';
	import { units } from '$lib/api/endpoints/units';
	import * as Tabs from '$lib/components/ui/tabs';
	import UnitHeader from '$lib/components/unit/UnitHeader.svelte';
	import LifecycleRail from '$lib/components/unit/LifecycleRail.svelte';
	import UnitTimelineRail from '$lib/components/unit/UnitTimelineRail.svelte';
	import OverviewTab from '$lib/components/unit/tabs/OverviewTab.svelte';
	import LeaseTab from '$lib/components/unit/tabs/LeaseTab.svelte';
	import RentTab from '$lib/components/unit/tabs/RentTab.svelte';
	import MaintenanceTab from '$lib/components/unit/tabs/MaintenanceTab.svelte';
	import DocumentsTab from '$lib/components/unit/tabs/DocumentsTab.svelte';
	import ExpensesTab from '$lib/components/unit/tabs/ExpensesTab.svelte';
	import TimelineTab from '$lib/components/unit/tabs/TimelineTab.svelte';
	import { resolveUnitTab } from '$lib/components/unit/unit-tabs';
	import { scanHref, type ScanContext } from '$lib/scan/scan-context';
	import { ArrowLeft } from '@lucide/svelte';

	const id = $derived(Number(page.params.id));

	// Active tab is driven by ?tab= (default overview) so deep links land on the right tab.
	let activeTab = $state(resolveUnitTab(page.url.searchParams.get('tab')));

	$effect(() => {
		const tabFromUrl = resolveUnitTab(page.url.searchParams.get('tab'));
		if (tabFromUrl !== activeTab) activeTab = tabFromUrl;
	});

	// Keep the URL in sync when the user switches tabs (replace, no history spam), so a refresh/back stays put.
	function setTab(tab: string) {
		const nextTab = resolveUnitTab(tab);
		activeTab = nextTab;
		const url = new URL(page.url);
		url.searchParams.set('tab', nextTab);
		goto(url, { replaceState: true, keepFocus: true, noScroll: true });
	}

	const dashboardQuery = createQuery(() => ({
		queryKey: ['unit-dashboard', id],
		enabled: !isNaN(id) && id > 0,
		queryFn: () => units.dashboard(id),
	}));

	const dashboard = $derived(dashboardQuery.data);

	// Scan / Upload routes into the existing scan -> draft -> confirm flow while preserving
	// the unit context the user started from.
	function goScan(context: Partial<ScanContext> = {}) {
		if (!dashboard) {
			goto('/scan');
			return;
		}
		const unit = dashboard.unit;
		goto(scanHref({
			...context,
			type: context.type ?? 'Expense',
			propertyId: context.propertyId ?? unit.propertyId,
			unitId: context.unitId ?? unit.id,
			returnTo: context.returnTo ?? `/units/${unit.id}?tab=${activeTab}`
		}));
	}
</script>

<svelte:head>
	<title>{dashboard ? `Unit ${dashboard.unit.unitNumber}` : 'Unit'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-4 pb-20 sm:p-6" data-testid="unit-page">
	{#if dashboardQuery.isLoading}
		<div class="space-y-4" data-testid="unit-loading">
			<div class="h-32 animate-pulse rounded-xl bg-muted"></div>
			<div class="h-16 animate-pulse rounded-xl bg-muted"></div>
			<div class="h-64 animate-pulse rounded-xl bg-muted"></div>
		</div>
	{:else if dashboardQuery.isError || !dashboard}
		<div class="rounded-xl border bg-card p-8 text-center" data-testid="unit-error">
			<p class="text-sm text-muted-foreground">This unit could not be loaded.</p>
			<a href="/units" class="mt-2 inline-flex items-center gap-1 text-sm text-primary hover:underline">
				<ArrowLeft class="h-4 w-4" /> Back to units
			</a>
		</div>
	{:else}
		<div class="space-y-4">
			<UnitHeader {dashboard} onScan={() => goScan()} />
			<LifecycleRail stage={dashboard.lifecycleStage} nextBestAction={dashboard.nextBestAction} onStageClick={setTab} />

			<div class="grid gap-4 lg:grid-cols-[1fr_320px]">
				<!-- Work tabs -->
				<div class="min-w-0">
					<Tabs.Root value={activeTab} onValueChange={setTab}>
						<Tabs.List class="flex w-full flex-wrap" data-testid="unit-tabs">
							<Tabs.Trigger value="overview" data-testid="tab-overview">Overview</Tabs.Trigger>
							<Tabs.Trigger value="lease" data-testid="tab-lease">Lease</Tabs.Trigger>
							<Tabs.Trigger value="rent" data-testid="tab-rent">Rent</Tabs.Trigger>
							<Tabs.Trigger value="maintenance" data-testid="tab-maintenance">Maintenance</Tabs.Trigger>
							<Tabs.Trigger value="documents" data-testid="tab-documents">Documents</Tabs.Trigger>
							<Tabs.Trigger value="expenses" data-testid="tab-expenses">Expenses</Tabs.Trigger>
							<Tabs.Trigger value="timeline" data-testid="tab-timeline">Timeline</Tabs.Trigger>
						</Tabs.List>

						<Tabs.Content value="overview" class="mt-4">
							<OverviewTab {dashboard} onOpenTab={setTab} />
						</Tabs.Content>
						<Tabs.Content value="lease" class="mt-4">
							<LeaseTab {dashboard} onScan={() => goScan({ type: 'Lease', returnTo: `/units/${dashboard.unit.id}?tab=lease` })} />
						</Tabs.Content>
						<Tabs.Content value="rent" class="mt-4">
							<RentTab {dashboard} onScan={() => goScan({ type: 'Payment', leaseId: dashboard.currentLease?.id, returnTo: `/units/${dashboard.unit.id}?tab=rent` })} />
						</Tabs.Content>
						<Tabs.Content value="maintenance" class="mt-4">
							<MaintenanceTab {dashboard} onScan={goScan} />
						</Tabs.Content>
						<Tabs.Content value="documents" class="mt-4">
							<DocumentsTab docs={dashboard.overview.pendingDocs} onScan={() => goScan({ returnTo: `/units/${dashboard.unit.id}?tab=documents` })} />
						</Tabs.Content>
						<Tabs.Content value="expenses" class="mt-4">
							<ExpensesTab {dashboard} onScan={goScan} />
						</Tabs.Content>
						<Tabs.Content value="timeline" class="mt-4">
							<TimelineTab unitId={id} />
						</Tabs.Content>
					</Tabs.Root>
				</div>

				<!-- Persistent timeline rail (visible across tabs). -->
				<div class="lg:sticky lg:top-4 lg:self-start">
					<UnitTimelineRail activities={dashboard.recentTimeline} onViewAll={() => setTab('timeline')} />
				</div>
			</div>
		</div>
	{/if}
</div>
