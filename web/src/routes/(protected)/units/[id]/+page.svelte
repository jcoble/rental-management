<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { units } from '$lib/api/endpoints/units';
	import { securityDeposits } from '$lib/api/endpoints/securityDeposits';
	import { appointments } from '$lib/api/endpoints/appointments';
	import * as Tabs from '$lib/components/ui/tabs';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import UnitHeader from '$lib/components/unit/UnitHeader.svelte';
	import ScanLauncher from '$lib/components/scan/ScanLauncher.svelte';
	import LifecycleRail from '$lib/components/unit/LifecycleRail.svelte';
	import UnitTimelineRail from '$lib/components/unit/UnitTimelineRail.svelte';
	import OverviewTab from '$lib/components/unit/tabs/OverviewTab.svelte';
	import ListingTab from '$lib/components/unit/tabs/ListingTab.svelte';
	import LeaseTab from '$lib/components/unit/tabs/LeaseTab.svelte';
	import ApplicationsTab from '$lib/components/unit/tabs/ApplicationsTab.svelte';
	import LedgerTab from '$lib/components/unit/tabs/LedgerTab.svelte';
	import MaintenanceTab from '$lib/components/unit/tabs/MaintenanceTab.svelte';
	import DocumentsTab from '$lib/components/unit/tabs/DocumentsTab.svelte';
	import TimelineTab from '$lib/components/unit/tabs/TimelineTab.svelte';
	import UnitFields from '$lib/components/forms/UnitFields.svelte';
	import { resolveUnitTab } from '$lib/components/unit/unit-tabs';
	import { createUnitEditForm, type UnitEditForm } from '$lib/components/unit/unit-edit-form';
	import type { ScanContext } from '$lib/scan/scan-context';
	import { unitSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import type { UnitLeaseSummary } from '$lib/types';
	import { ArrowLeft } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const id = $derived(Number(page.params.id));
	const emptyUnitForm: UnitEditForm = { unitNumber: '', bedrooms: '', bathrooms: '', marketRent: '' };

	// Active tab is driven by ?tab= (default overview) so deep links land on the right tab.
	let activeTab = $state(resolveUnitTab(page.url.searchParams.get('tab')));
	let showEditUnit = $state(false);
	let showMoveInDialog = $state(false);
	let showScanLauncher = $state(false);
	let scanLauncherContext = $state<ScanContext>({});
	let handledMoveInActionKey = $state('');
	let unitForm = $state({ ...emptyUnitForm });
	let unitFormErrors = $state<Record<string, string>>({});

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
	const moveInAppointment = $derived(
		dashboard?.overview.upcomingAppointments.find((appointment) =>
			appointment.type === 'MoveIn'
			&& appointment.status !== 'Completed'
			&& appointment.status !== 'Cancelled'
			&& appointment.status !== 'NoShow'
		) ?? null
	);
	const moveInDialogLease = $derived(dashboard?.currentLease ?? null);

	$effect(() => {
		const isConfirmMoveInAction = page.url.searchParams.get('action') === 'confirm-move-in';
		if (!dashboard || !isConfirmMoveInAction) return;

		const actionKey = `${id}:${dashboard.currentLease?.id ?? 'no-lease'}:${page.url.search}`;
		if (handledMoveInActionKey !== actionKey) {
			handledMoveInActionKey = actionKey;
			activeTab = 'lease';
			showMoveInDialog = true;
		}
	});

	function refreshUnitDashboard() {
		queryClient.invalidateQueries({ queryKey: ['unit-dashboard', id] });
	}

	function clearMoveInActionUrl() {
		if (page.url.searchParams.get('action') !== 'confirm-move-in') return;
		const url = new URL(page.url);
		url.searchParams.delete('action');
		goto(url, { replaceState: true, keepFocus: true, noScroll: true });
	}

	function closeMoveInDialog() {
		showMoveInDialog = false;
		clearMoveInActionUrl();
	}

	const moveInMutation = createMutation(() => ({
		mutationFn: async (lease: UnitLeaseSummary) => {
			const deposit = lease.securityDeposit > 0
				? await securityDeposits.create({
					leaseId: lease.id,
					notes: 'Confirmed from Unit Command Center move-in workflow.'
				})
				: null;

			if (moveInAppointment) {
				await appointments.update(moveInAppointment.id, { status: 'Completed' });
			}

			return deposit;
		},
		onSuccess: () => {
			showSuccess('Move-in confirmed.');
			closeMoveInDialog();
			queryClient.invalidateQueries({ queryKey: ['unit-dashboard', id] });
			queryClient.invalidateQueries({ queryKey: ['unit-timeline', id] });
			queryClient.invalidateQueries({ queryKey: ['deposits'] });
			queryClient.invalidateQueries({ queryKey: ['appointments'] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openEditUnit() {
		if (!dashboard) return;
		unitForm = createUnitEditForm(dashboard.unit);
		unitFormErrors = {};
		showEditUnit = true;
	}

	function closeEditUnit() {
		showEditUnit = false;
		unitFormErrors = {};
	}

	function submitUnit() {
		if (!dashboard) return;
		const result = parseForm(unitSchema, unitForm);
		if (result.errors) {
			unitFormErrors = result.errors;
			return;
		}
		unitFormErrors = {};
		updateUnitMutation.mutate({ unitId: dashboard.unit.id, data: result.data });
	}

	const updateUnitMutation = createMutation(() => ({
		mutationFn: ({ unitId, data }: { unitId: number; data: Record<string, unknown> }) =>
			units.update(unitId, data),
		onSuccess: () => {
			const propertyId = dashboard?.unit.propertyId;
			showSuccess('Unit updated.');
			closeEditUnit();
			queryClient.invalidateQueries({ queryKey: ['unit-dashboard', id] });
			queryClient.invalidateQueries({ queryKey: ['units'] });
			queryClient.invalidateQueries({ queryKey: ['properties'] });
			if (propertyId) queryClient.invalidateQueries({ queryKey: ['property', propertyId] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// Scan / Upload opens an in-place launcher while preserving the unit context the user started from.
	function goScan(context: Partial<ScanContext> = {}) {
		if (!dashboard) {
			goto('/scan');
			return;
		}
		const unit = dashboard.unit;
		scanLauncherContext = {
			...context,
			type: context.type ?? 'Expense',
			propertyId: context.propertyId ?? unit.propertyId,
			unitId: context.unitId ?? unit.id,
			returnTo: context.returnTo ?? `/units/${unit.id}?tab=${activeTab}`
		};
		showScanLauncher = true;
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
			<UnitHeader {dashboard} onEdit={openEditUnit} onScan={() => goScan()} />
			<LifecycleRail stage={dashboard.lifecycleStage} nextBestAction={dashboard.nextBestAction} onStageClick={setTab} />

			<div class="min-w-0">
				<Tabs.Root value={activeTab} onValueChange={setTab}>
					<div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
						<Tabs.List class="flex w-full flex-wrap sm:flex-1" data-testid="unit-tabs">
							<Tabs.Trigger value="overview" data-testid="tab-overview">Overview</Tabs.Trigger>
							<Tabs.Trigger value="listing" data-testid="tab-listing">Listing</Tabs.Trigger>
							<Tabs.Trigger value="lease" data-testid="tab-lease">Lease</Tabs.Trigger>
							<Tabs.Trigger value="applications" data-testid="tab-applications">Applications</Tabs.Trigger>
							<Tabs.Trigger value="ledger" data-testid="tab-ledger">Ledger</Tabs.Trigger>
							<Tabs.Trigger value="maintenance" data-testid="tab-maintenance">Maintenance</Tabs.Trigger>
							<Tabs.Trigger value="documents" data-testid="tab-documents">Documents</Tabs.Trigger>
							<Tabs.Trigger value="timeline" data-testid="tab-timeline">Timeline</Tabs.Trigger>
						</Tabs.List>
						<UnitTimelineRail activities={dashboard.recentTimeline} onViewAll={() => setTab('timeline')} />
					</div>

					<Tabs.Content value="overview" class="mt-4">
						<OverviewTab {dashboard} onOpenTab={setTab} />
					</Tabs.Content>
					<Tabs.Content value="listing" class="mt-4">
						<ListingTab {dashboard} />
					</Tabs.Content>
					<Tabs.Content value="lease" class="mt-4">
						<LeaseTab {dashboard} onScan={() => goScan({ type: 'Lease', returnTo: `/units/${dashboard.unit.id}?tab=lease` })} />
					</Tabs.Content>
					<Tabs.Content value="applications" class="mt-4">
						<ApplicationsTab {dashboard} />
					</Tabs.Content>
					<Tabs.Content value="ledger" class="mt-4">
						<LedgerTab {dashboard} onScan={goScan} />
					</Tabs.Content>
					<Tabs.Content value="maintenance" class="mt-4">
						<MaintenanceTab {dashboard} onScan={goScan} />
					</Tabs.Content>
					<Tabs.Content value="documents" class="mt-4">
						<DocumentsTab
							unitId={dashboard.unit.id}
							docs={dashboard.overview.pendingDocs}
							onScan={() => goScan({ returnTo: `/units/${dashboard.unit.id}?tab=documents` })}
							onDocumentsChanged={refreshUnitDashboard}
						/>
					</Tabs.Content>
					<Tabs.Content value="timeline" class="mt-4">
						<TimelineTab unitId={id} />
					</Tabs.Content>
				</Tabs.Root>
			</div>
		</div>
	{/if}
</div>

<Dialog.Root open={showMoveInDialog} onOpenChange={(v) => { if (!v) closeMoveInDialog(); }}>
	<Dialog.Content class="max-w-md" data-testid="unit-move-in-dialog">
		<Dialog.Header>
			<Dialog.Title>Confirm move-in</Dialog.Title>
			<Dialog.Description>
				Confirm this tenant has moved in and record the lease deposit as held.
			</Dialog.Description>
		</Dialog.Header>
		{#if moveInDialogLease}
			<div class="space-y-3 rounded-lg border bg-muted/30 p-3 text-sm">
				<div class="flex items-center justify-between gap-3">
					<span class="text-muted-foreground">Lease</span>
					<span class="font-medium">{moveInDialogLease.leaseNumber}</span>
				</div>
				<div class="flex items-center justify-between gap-3">
					<span class="text-muted-foreground">Tenants</span>
					<span class="font-medium">
						{dashboard?.currentTenants?.length
							? dashboard.currentTenants.map((tenant) => tenant.name).join(', ')
							: dashboard?.currentTenant?.name ?? 'Current tenants'}
					</span>
				</div>
				<div class="flex items-center justify-between gap-3">
					<span class="text-muted-foreground">Security deposit</span>
					<span class="font-mono font-medium tabular-nums">
						{new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(moveInDialogLease.securityDeposit)}
					</span>
				</div>
				{#if moveInAppointment}
					<div class="flex items-center justify-between gap-3">
						<span class="text-muted-foreground">Appointment</span>
						<span class="font-medium">{moveInAppointment.title}</span>
					</div>
				{/if}
			</div>
			<p class="text-sm text-muted-foreground">
				Use this after keys are handed over and the deposit has been received.
			</p>
		{:else}
			<p class="text-sm text-muted-foreground">This unit does not have a current lease to confirm.</p>
		{/if}
		<Dialog.Footer class="mt-4">
			<Button variant="outline" onclick={closeMoveInDialog}>Cancel</Button>
			<Button
				onclick={() => moveInDialogLease && moveInMutation.mutate(moveInDialogLease)}
				disabled={!moveInDialogLease || moveInMutation.isPending}
				data-testid="unit-move-in-confirm"
			>
				{moveInMutation.isPending ? 'Confirming…' : 'Confirm move-in'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ScanLauncher bind:open={showScanLauncher} context={scanLauncherContext} showTrigger={false} />

<Dialog.Root open={showEditUnit} onOpenChange={(v) => { if (!v) closeEditUnit(); }}>
	<Dialog.Content class="max-w-sm" data-testid="unit-detail-edit-dialog">
		<Dialog.Header>
			<Dialog.Title>Edit unit</Dialog.Title>
		</Dialog.Header>
		<UnitFields bind:form={unitForm} errors={unitFormErrors} testidPrefix="unit-detail" />
		<Dialog.Footer class="mt-4">
			<Button variant="outline" onclick={closeEditUnit}>Cancel</Button>
			<Button
				data-testid="unit-detail-save-button"
				onclick={submitUnit}
				disabled={updateUnitMutation.isPending}
			>
				{updateUnitMutation.isPending ? 'Saving…' : 'Save Unit'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
