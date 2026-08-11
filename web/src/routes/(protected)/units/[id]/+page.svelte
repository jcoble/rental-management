<script lang="ts">
	import { page } from '$app/state';
	import { goto, pushState, replaceState } from '$app/navigation';
	import { onMount, tick, untrack } from 'svelte';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { units } from '$lib/api/endpoints/units';
	import { inspections } from '$lib/api/endpoints/inspections';
	import { leaseManagements } from '$lib/api/endpoints/lease-managements';
	import type { PrepareMoveInResponse } from '$lib/api/endpoints/lease-managements';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import UnitHeader from '$lib/components/unit/UnitHeader.svelte';
	import ScanLauncher from '$lib/components/scan/ScanLauncher.svelte';
	import UnitTimelineRail from '$lib/components/unit/UnitTimelineRail.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import OverviewTab from '$lib/components/unit/tabs/OverviewTab.svelte';
	import ListingTab from '$lib/components/unit/tabs/ListingTab.svelte';
	import LeaseTab from '$lib/components/unit/tabs/LeaseTab.svelte';
	import LeaseManagementDetail from '$lib/components/leases/LeaseManagementDetail.svelte';
	import ResidentsTab from '$lib/components/unit/tabs/ResidentsTab.svelte';
	import ApplicationsTab from '$lib/components/unit/tabs/ApplicationsTab.svelte';
	import LedgerTab from '$lib/components/unit/tabs/LedgerTab.svelte';
	import InspectionDetail from '$lib/components/records/InspectionDetail.svelte';
	import MaintenanceTab from '$lib/components/unit/tabs/MaintenanceTab.svelte';
	import TurnoverTab from '$lib/components/unit/tabs/TurnoverTab.svelte';
	import UnitRecurringMaintenanceView from '$lib/components/maintenance/UnitRecurringMaintenanceView.svelte';
	import DocumentsTab from '$lib/components/unit/tabs/DocumentsTab.svelte';
	import TimelineTab from '$lib/components/unit/tabs/TimelineTab.svelte';
	import PrepareMoveInDialog from '$lib/components/applications/PrepareMoveInDialog.svelte';
	import { readPrepareMoveInPrefill } from '$lib/leases/prepare-move-in-prefill';
	import UnitFields from '$lib/components/forms/UnitFields.svelte';
	import {
		UNIT_SUBNAV_ITEM_CLASS,
		UNIT_SUBNAV_LIST_CLASS,
	} from '$lib/components/unit/unit-tabs';
	import {
		createUnitTabNavigationHandler,
		normalizeUnitTabNavigationState,
		resolveUnitPageDestination,
		synchronizeUnitTabState,
		type UnitTabNavigationState,
	} from '$lib/components/unit/unit-tab-navigation';
	import { createUnitEditForm, type UnitEditForm } from '$lib/components/unit/unit-edit-form';
	import type { ScanContext } from '$lib/scan/scan-context';
	import { unitSchemaForPropertyType, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { businessDateOrToday } from '$lib/utils/business-date';
	import type { UnitLeaseSummary } from '$lib/types';
	import { ArrowLeft } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const id = $derived(Number(page.params.id));
	const emptyUnitForm: UnitEditForm = { unitNumber: '', floorPlan: '', bedrooms: '', bathrooms: '', squareFeet: '', marketRent: '', notes: '' };
	const unitPageState = $derived(
		normalizeUnitTabNavigationState(page.state as UnitTabNavigationState | null | undefined),
	);

	const activeDestination = $derived(resolveUnitPageDestination(
		page.url,
		unitPageState,
	));
	const activeTab = $derived(activeDestination.tab);
	const activeView = $derived(activeDestination.view);
	let routeStateHydrated = $state(false);
	onMount(() => {
		// SvelteKit's navigation module installs its root component instance
		// after the route's mount callbacks are scheduled. Wait one tick before
		// the first shallow replaceState so cold document loads never reach an
		// uninitialized navigation root.
		void tick().then(() => {
			routeStateHydrated = true;
		});
	});

	$effect(() => {
		// A hard document navigation can run this effect before SvelteKit has
		// finished mounting the route component. Defer the first shallow-state
		// write until onMount so replaceState cannot update an uninitialized page.
		if (!routeStateHydrated) return;
		const nextState = synchronizeUnitTabState(page.url, unitPageState);
		if (!nextState) return;
		replaceState(`${page.url.pathname}${page.url.search}`, nextState as App.PageState);
	});
	const prepareMoveInPrefill = $derived(readPrepareMoveInPrefill(page.url.searchParams));
	const selectedLeaseManagementId = $derived.by(() => {
		const rawValue = page.url.searchParams.get('leaseManagement');
		if (!rawValue) return null;
		const value = Number(rawValue);
		return Number.isInteger(value) && value > 0 ? value : null;
	});
	const selectedInspectionId = $derived.by(() => {
		const rawValue = page.url.searchParams.get('inspection');
		if (!rawValue) return null;
		const value = Number(rawValue);
		return Number.isInteger(value) && value > 0 ? value : null;
	});
	const leaseRelationshipListHref = $derived.by(() => {
		const params = new URLSearchParams(page.url.searchParams);
		params.delete('leaseManagement');
		const query = params.toString();
		return `${page.url.pathname}${query ? `?${query}` : ''}`;
	});
	const inspectionListHref = $derived.by(() => {
		const params = new URLSearchParams(page.url.searchParams);
		params.delete('inspection');
		const query = params.toString();
		return `${page.url.pathname}${query ? `?${query}` : ''}`;
	});
	let showEditUnit = $state(false);
	let showMoveInDialog = $state(false);
	let moveInDepositEffectiveOn = $state(businessDateOrToday(undefined));
	let moveInDepositPaymentMethod = $state('');
	let moveInDepositReference = $state('');
	let moveInDepositErrors = $state<Record<string, string>>({});
	let moveInOperation = $state({ fingerprint: '', operationKey: '' });
	let showScanLauncher = $state(false);
	let scanLauncherContext = $state<ScanContext>({});
	let handledMoveInActionKey = $state('');
	let unitForm = $state({ ...emptyUnitForm });
	let unitFormErrors = $state<Record<string, string>>({});
	let inspectionSearch = $state('');
	let inspectionSort = $state('-scheduledFor');
	let inspectionSkip = $state(0);
	const maintenancePageSize = 10;
	const setTab = createUnitTabNavigationHandler({
		getCurrent: () => ({
			url: page.url,
			state: unitPageState,
		}),
		pushState: (url, state) => pushState(url, state as App.PageState),
		navigate: (url, state) => void goto(url, {
			state: state as App.PageState,
			keepFocus: true,
			noScroll: true,
		}),
		replaceState: (url, state) => replaceState(url, state as App.PageState),
	});

	function tabState(tab: string) {
		return activeTab === tab ? 'active' : 'inactive';
	}

	function currentUnitReturnTo() {
		return `${page.url.pathname}${page.url.search}`;
	}

	function openInspection(inspectionId: number) {
		void goto(`/units/${id}?tab=maintenance&view=inspections&inspection=${inspectionId}`, {
			keepFocus: true,
			noScroll: true,
		});
	}

	function closeInspection() {
		void goto(inspectionListHref, {
			replaceState: true,
			keepFocus: true,
			noScroll: true,
		});
	}

	function closePrepareMoveIn() {
		const url = new URL(page.url);
		url.searchParams.delete('prepareMoveIn');
		url.searchParams.delete('applicationId');
		url.searchParams.delete('unitId');
		url.searchParams.delete('tenantId');
		void goto(`${url.pathname}${url.search}`, {
			replaceState: true,
			keepFocus: true,
			noScroll: true
		});
	}

	function finishPrepareMoveIn(result: PrepareMoveInResponse) {
		void goto(`/units/${id}?tab=tenant-lease&view=agreements&leaseManagement=${result.leaseManagementId}`);
	}

	const dashboardQuery = createQuery(() => ({
		queryKey: ['unit-dashboard', id],
		enabled: !isNaN(id) && id > 0,
		queryFn: () => units.dashboard(id),
	}));

	const dashboard = $derived(dashboardQuery.data);
	const moveInContextQuery = createQuery(() => ({
		queryKey: ['prepare-move-in-context'],
		enabled: page.url.searchParams.get('action') === 'confirm-move-in',
		queryFn: () => leaseManagements.prepareMoveInContext()
	}));
	const moveInBusinessDate = $derived(businessDateOrToday(moveInContextQuery.data?.businessDate));
	const unitInspectionsQuery = createQuery(() => ({
		queryKey: ['unit-inspections', id, inspectionSearch, inspectionSort, inspectionSkip, maintenancePageSize],
		enabled: !isNaN(id) && id > 0,
		queryFn: () => inspections.listUnitPage({
			unitId: id,
			search: inspectionSearch || undefined,
			sort: inspectionSort,
			skip: inspectionSkip,
			take: maintenancePageSize,
		}),
	}));
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

		if (activeTab !== 'tenant-lease' || activeView !== 'agreements') {
			const url = new URL(page.url);
			url.searchParams.set('tab', 'tenant-lease');
			url.searchParams.set('view', 'agreements');
			url.searchParams.set('action', 'confirm-move-in');
			replaceState(`${url.pathname}${url.search}`, {
				...unitPageState,
				unitTab: 'tenant-lease',
				unitView: 'agreements',
			});
			return;
		}

		const actionKey = `${id}:${dashboard.currentLease?.id ?? 'no-lease'}:${page.url.search}:${moveInBusinessDate}`;
		if (untrack(() => handledMoveInActionKey) !== actionKey) {
			handledMoveInActionKey = actionKey;
			moveInDepositEffectiveOn = moveInBusinessDate;
			moveInDepositPaymentMethod = '';
			moveInDepositReference = '';
			moveInDepositErrors = {};
			moveInOperation = { fingerprint: '', operationKey: '' };
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
		replaceState(`${url.pathname}${url.search}`, unitPageState as App.PageState);
	}

	function closeMoveInDialog() {
		showMoveInDialog = false;
		moveInDepositErrors = {};
		clearMoveInActionUrl();
	}

	const moveInMutation = createMutation(() => ({
		mutationFn: async ({
			lease,
			operationKey
		}: {
			lease: UnitLeaseSummary;
			operationKey: string;
		}) => {
			return leaseManagements.confirmMoveIn(
				lease.leaseManagementId,
				{
					unitId: id,
					depositEffectiveOn: lease.securityDeposit > 0 ? moveInDepositEffectiveOn : null,
					depositPaymentMethodSummary:
						lease.securityDeposit > 0 ? moveInDepositPaymentMethod.trim() : null,
					depositExternalReference: moveInDepositReference.trim() || null,
					moveInAppointmentId: moveInAppointment?.id ?? null,
				},
				operationKey
			);
		},
		onSuccess: () => {
			showSuccess('Possession given. Move-in confirmed.');
			moveInOperation = { fingerprint: '', operationKey: '' };
			closeMoveInDialog();
			queryClient.invalidateQueries({ queryKey: ['unit-dashboard', id] });
			queryClient.invalidateQueries({ queryKey: ['unit-timeline', id] });
			queryClient.invalidateQueries({ queryKey: ['deposits'] });
			queryClient.invalidateQueries({ queryKey: ['appointments'] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitMoveIn(lease: UnitLeaseSummary) {
		const errors: Record<string, string> = {};
		if (lease.securityDeposit > 0 && !moveInDepositEffectiveOn) {
			errors.effectiveOn = 'Received date is required.';
		}
		if (lease.securityDeposit > 0 && !moveInDepositPaymentMethod.trim()) {
			errors.paymentMethod = 'Payment method is required.';
		}
		moveInDepositErrors = errors;
		if (Object.keys(errors).length > 0) return;

		const fingerprint = JSON.stringify({
			leaseManagementId: lease.leaseManagementId,
			unitId: id,
			amount: lease.securityDeposit,
			effectiveOn: moveInDepositEffectiveOn,
			paymentMethod: moveInDepositPaymentMethod.trim(),
			reference: moveInDepositReference.trim(),
			appointmentId: moveInAppointment?.id ?? null,
		});
		if (
			moveInOperation.fingerprint !== fingerprint ||
			!moveInOperation.operationKey
		) {
			moveInOperation = {
				fingerprint,
				operationKey: crypto.randomUUID()
			};
		}
		moveInMutation.mutate({
			lease,
			operationKey: moveInOperation.operationKey
		});
	}

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
		const result = parseForm(unitSchemaForPropertyType(dashboard?.unit.propertyType), unitForm);
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
			leaseManagementId: context.leaseManagementId ?? dashboard.leaseManagementId ?? undefined,
			leaseAgreementId: context.leaseAgreementId ?? dashboard.currentLease?.id ?? undefined,
			tenantAccountId: context.tenantAccountId ?? dashboard.tenantAccountId ?? undefined,
			sourceLabel: context.sourceLabel ?? `${dashboard.propertyName} · Unit ${unit.unitNumber}`,
			returnTo: context.returnTo ?? currentUnitReturnTo()
		};
		showScanLauncher = true;
	}
</script>

<svelte:head>
	<title>{dashboard ? `Unit ${dashboard.unit.unitNumber}` : 'Unit'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-4 pb-20 sm:p-6" data-testid="unit-page">
	{#if dashboardQuery.isLoading}
		<LoadingState label="Loading Unit details" variant="page" testid="unit-loading" />
	{:else if dashboardQuery.isError || !dashboard}
		<div class="rounded-xl border bg-card p-8 text-center" data-testid="unit-error">
			<p class="text-sm text-muted-foreground">This unit could not be loaded.</p>
			<div class="mt-4 flex flex-wrap justify-center gap-2">
				<Button variant="outline" onclick={() => dashboardQuery.refetch()}>Try again</Button>
				<Button href="/units" variant="ghost" class="gap-1"><ArrowLeft class="h-4 w-4" /> Back to units</Button>
			</div>
		</div>
	{:else}
		<div class="space-y-4">
			<UnitHeader {dashboard} onEdit={openEditUnit} onScan={() => goScan()} />
			<div class="min-w-0">
				<div class="flex flex-col gap-2">
					<div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
						<div class="min-w-0 overflow-x-auto sm:flex-1">
							<div class="m3-tabs-list min-w-max" role="tablist" data-testid="unit-tabs">
								<button type="button" role="tab" aria-selected={activeTab === 'summary'} data-state={tabState('summary')} class="m3-tabs-trigger" onclick={() => setTab('summary')} data-testid="tab-summary">Summary</button>
								<button type="button" role="tab" aria-selected={activeTab === 'leasing'} data-state={tabState('leasing')} class="m3-tabs-trigger" onclick={() => setTab('leasing')} data-testid="tab-leasing">Leasing</button>
								<button type="button" role="tab" aria-selected={activeTab === 'tenant-lease'} data-state={tabState('tenant-lease')} class="m3-tabs-trigger" onclick={() => setTab('tenant-lease')} data-testid="tab-tenant-lease">Tenant &amp; lease</button>
								<button type="button" role="tab" aria-selected={activeTab === 'money'} data-state={tabState('money')} class="m3-tabs-trigger" onclick={() => setTab('money')} data-testid="tab-money">Money</button>
								<button type="button" role="tab" aria-selected={activeTab === 'maintenance'} data-state={tabState('maintenance')} class="m3-tabs-trigger" onclick={() => setTab('maintenance')} data-testid="tab-maintenance">Maintenance</button>
								<button type="button" role="tab" aria-selected={activeTab === 'documents-history'} data-state={tabState('documents-history')} class="m3-tabs-trigger" onclick={() => setTab('documents-history')} data-testid="tab-documents-history">Documents &amp; history</button>
							</div>
						</div>
						<UnitTimelineRail activities={dashboard.recentTimeline} onViewAll={() => setTab('documents-history', 'history')} />
					</div>

					{#if activeTab === 'summary'}
					<div class="mt-4 flex-1 outline-none" role="tabpanel" aria-label="Summary">
						<OverviewTab {dashboard} onOpenTab={setTab} />
					</div>
					{:else if activeTab === 'leasing'}
					<div class="mt-4 flex-1 outline-none" role="tabpanel" aria-label="Leasing">
						<div class="space-y-4" data-testid="unit-leasing-surface">
							<div class="min-w-0 overflow-x-auto">
								<nav aria-label="Leasing sections" data-testid="unit-leasing-subnav">
									<div class={UNIT_SUBNAV_LIST_CLASS}>
										<button type="button" aria-current={activeView === 'listing' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setTab('leasing', 'listing')}>Listing</button>
										<button type="button" aria-current={activeView === 'applications' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setTab('leasing', 'applications')}>Applications</button>
									</div>
								</nav>
							</div>
							{#if activeView === 'applications'}
							<section tabindex="-1" class="scroll-mt-4 outline-none" data-testid="unit-applications-section">
								<ApplicationsTab {dashboard} />
							</section>
								{:else}
								<section tabindex="-1" class="scroll-mt-4 outline-none" data-testid="unit-listing-section">
									<ListingTab {dashboard} />
								</section>
								{/if}
						</div>
					</div>
					{:else if activeTab === 'tenant-lease'}
					<div class="mt-4 flex-1 outline-none" role="tabpanel" aria-label="Tenant & lease">
						<div class="space-y-4" data-testid="unit-tenant-lease-surface">
							<div class="min-w-0 overflow-x-auto">
								<nav aria-label="Tenant and lease sections" data-testid="unit-tenant-lease-subnav">
									<div class={UNIT_SUBNAV_LIST_CLASS}>
										<button type="button" aria-current={activeView === 'agreements' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setTab('tenant-lease', 'agreements')}>Lease &amp; move-in</button>
										<button type="button" aria-current={activeView === 'residents' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setTab('tenant-lease', 'residents')}>Residents</button>
									</div>
								</nav>
							</div>
							{#if activeView === 'residents'}
							<section
								tabindex="-1"
								class="scroll-mt-4 outline-none"
								data-testid="unit-residents-section"
							>
								<ResidentsTab {dashboard} />
							</section>
							{:else}
							<section
								tabindex="-1"
								class="scroll-mt-4 outline-none"
								data-testid="unit-agreement-section"
							>
								{#if selectedLeaseManagementId}
									<div class="mb-4">
										<Button href={leaseRelationshipListHref} variant="outline" size="sm" class="gap-2">
											<ArrowLeft class="h-4 w-4" /> Back to tenant relationships
										</Button>
									</div>
									<LeaseManagementDetail leaseManagementId={selectedLeaseManagementId} />
								{:else}
								<LeaseTab
									{dashboard}
									onScan={() => goScan({
										type: 'LeaseAgreement',
										returnTo: `/units/${dashboard.unit.id}?tab=tenant-lease&view=agreements`
									})}
								/>
								{/if}
							</section>
							{/if}
						</div>
					</div>
					{:else if activeTab === 'money'}
					<div class="mt-4 flex-1 outline-none" role="tabpanel" aria-label="Money">
						<LedgerTab {dashboard} onScan={goScan} onOpenTab={setTab} />
					</div>
					{:else if activeTab === 'maintenance'}
					<div class="mt-4 flex-1 outline-none" role="tabpanel" aria-label="Maintenance">
						<div class="space-y-4" data-testid="unit-maintenance-surface">
							<div class="min-w-0 overflow-x-auto">
								<nav aria-label="Maintenance sections" data-testid="unit-maintenance-subnav">
									<div class={UNIT_SUBNAV_LIST_CLASS}>
										<button type="button" aria-current={activeView === 'work-orders' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setTab('maintenance', 'work-orders')}>Work orders</button>
										<button type="button" aria-current={activeView === 'inspections' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setTab('maintenance', 'inspections')}>Inspections</button>
										<button type="button" aria-current={activeView === 'recurring' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setTab('maintenance', 'recurring')}>Recurring work</button>
										<button type="button" aria-current={activeView === 'turnover' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setTab('maintenance', 'turnover')}>Move-out &amp; turnover</button>
									</div>
								</nav>
							</div>
							{#if activeView === 'inspections'}
							<section tabindex="-1" class="scroll-mt-4 outline-none" data-testid="unit-inspections-section">
								{#if selectedInspectionId}
								<div class="space-y-4" data-testid="unit-inspection-detail-surface">
									<Button variant="outline" size="sm" class="gap-1" onclick={closeInspection} data-testid="inspection-detail-close">
										<ArrowLeft class="h-4 w-4" /> Back to inspections
									</Button>
									<InspectionDetail inspectionId={selectedInspectionId} expectedUnitId={id} onUnitMismatch={closeInspection} />
								</div>
								{:else}
								<div class="space-y-3">
									<div><h2 class="text-lg font-semibold">Inspections</h2><p class="text-sm text-muted-foreground">Scheduled and completed inspections for this rental.</p></div>
									<div class="flex flex-wrap gap-2">
										<Input bind:value={inspectionSearch} oninput={() => inspectionSkip = 0} placeholder="Search inspections" aria-label="Search Unit inspections" class="max-w-xs" />
										<Select.Root type="single" bind:value={inspectionSort} onValueChange={() => inspectionSkip = 0}>
											<Select.Trigger class="w-full sm:w-52" aria-label="Sort Unit inspections">
												<Select.Value placeholder="Newest scheduled" />
											</Select.Trigger>
											<Select.Content>
												<Select.Item value="-scheduledFor" label="Newest scheduled">Newest scheduled</Select.Item>
												<Select.Item value="scheduledFor" label="Oldest scheduled">Oldest scheduled</Select.Item>
												<Select.Item value="status" label="Status">Status</Select.Item>
											</Select.Content>
										</Select.Root>
									</div>
									{#if unitInspectionsQuery.isLoading}
										<LoadingState label="Loading inspections" testid="unit-inspections-loading" />
									{:else if unitInspectionsQuery.isError}
										<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="unit-inspections-error">
											<p class="text-sm font-medium text-destructive">Inspections could not be loaded.</p>
											<Button class="mt-3" variant="outline" size="sm" onclick={() => unitInspectionsQuery.refetch()}>Try again</Button>
										</div>
									{:else if !unitInspectionsQuery.data?.items.length}<p class="text-sm text-muted-foreground">No inspections for this rental.</p>
									{:else}
										<ul class="divide-y rounded-lg border" data-testid="unit-inspection-list">
											{#each unitInspectionsQuery.data.items as inspection (inspection.id)}
												<li data-testid={`inspection-${inspection.id}`}>
													<button
														type="button"
														class="flex w-full items-center justify-between gap-3 p-3 text-left text-sm transition-colors hover:bg-muted/40"
														onclick={() => openInspection(inspection.id)}
														data-testid={`inspection-open-${inspection.id}`}
													>
														<span class="font-medium" data-testid={`inspection-type-${inspection.id}`}>{formatStatusLabel(inspection.type)}</span>
														<span class="text-muted-foreground" data-testid={`inspection-summary-${inspection.id}`}>{formatStatusLabel(inspection.status)} · {formatDateOnly(inspection.scheduledFor)}</span>
													</button>
												</li>
											{/each}
										</ul>
									{/if}
									<div class="flex items-center justify-between">
										<Button variant="outline" size="sm" disabled={inspectionSkip === 0} onclick={() => inspectionSkip = Math.max(0, inspectionSkip - maintenancePageSize)}>Previous</Button>
										<span class="text-xs text-muted-foreground">{unitInspectionsQuery.data?.totalCount ?? 0} total</span>
										<Button variant="outline" size="sm" disabled={!unitInspectionsQuery.data || inspectionSkip + maintenancePageSize >= unitInspectionsQuery.data.totalCount} onclick={() => inspectionSkip += maintenancePageSize}>Next</Button>
									</div>
									</div>
								{/if}
							</section>
							{:else if activeView === 'recurring'}
							<UnitRecurringMaintenanceView
								propertyId={dashboard.unit.propertyId}
								propertyLabel={dashboard.propertyName}
								unitId={dashboard.unit.id}
								unitLabel={`Unit ${dashboard.unit.unitNumber}`}
								emptyMessage="No recurring tasks yet. Select the New recurring task button to add work that repeats on a schedule."
							/>
							{:else if activeView === 'turnover'}
							<section tabindex="-1" class="scroll-mt-4 outline-none" data-testid="unit-turnover-section"><TurnoverTab {dashboard} onScan={goScan} /></section>
							{:else}
							<section tabindex="-1" class="scroll-mt-4 outline-none" data-testid="unit-work-orders-section"><MaintenanceTab {dashboard} onScan={goScan} /></section>
							{/if}
						</div>
					</div>
					{:else}
					<div class="mt-4 flex-1 outline-none" role="tabpanel" aria-label="Documents & history">
						<div class="space-y-4" data-testid="unit-documents-history-surface">
							<div class="min-w-0 overflow-x-auto">
								<nav aria-label="Documents and history sections" data-testid="unit-documents-history-subnav">
									<div class={UNIT_SUBNAV_LIST_CLASS}>
										<button type="button" aria-current={activeView === 'documents' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setTab('documents-history', 'documents')}>Documents</button>
										<button type="button" aria-current={activeView === 'history' ? 'page' : undefined} class={UNIT_SUBNAV_ITEM_CLASS} onclick={() => setTab('documents-history', 'history')}>Activity</button>
									</div>
								</nav>
							</div>
							{#if activeView === 'history'}
							<section tabindex="-1" class="scroll-mt-4 outline-none" data-testid="unit-history-section"><TimelineTab unitId={id} /></section>
							{:else}
							<section tabindex="-1" class="scroll-mt-4 outline-none" data-testid="unit-documents-section">
								<DocumentsTab
									unitId={dashboard.unit.id}
									docs={dashboard.overview.pendingDocs}
									onScan={() => goScan({ returnTo: `/units/${dashboard.unit.id}?tab=documents-history&view=documents` })}
									onDocumentsChanged={refreshUnitDashboard}
								/>
							</section>
							{/if}
						</div>
					</div>
					{/if}
				</div>
			</div>
		</div>
	{/if}
</div>

{#if prepareMoveInPrefill}
	<PrepareMoveInDialog
		prefill={prepareMoveInPrefill}
		mode="manual"
		onclose={closePrepareMoveIn}
		onprepared={finishPrepareMoveIn}
	/>
{/if}

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
				Use this after keys are handed over. Deposit money is recorded in the account prepared
				from the approved application; this does not create another holding.
			</p>
			{#if moveInDialogLease.securityDeposit > 0}
				<div class="space-y-3 border-t pt-3">
					<div>
						<span class="mb-1 block text-xs text-muted-foreground">Deposit received date</span>
						<DatePicker bind:value={moveInDepositEffectiveOn} todayValue={moveInBusinessDate} testid="unit-move-in-deposit-date" />
						{#if moveInDepositErrors.effectiveOn}<p class="mt-1 text-xs text-destructive">{moveInDepositErrors.effectiveOn}</p>{/if}
					</div>
					<div>
						<span class="mb-1 block text-xs text-muted-foreground">Payment method</span>
						<Input bind:value={moveInDepositPaymentMethod} placeholder="Check, cash, ACH…" data-testid="unit-move-in-deposit-method" />
						{#if moveInDepositErrors.paymentMethod}<p class="mt-1 text-xs text-destructive">{moveInDepositErrors.paymentMethod}</p>{/if}
					</div>
					<div>
						<span class="mb-1 block text-xs text-muted-foreground">Reference (optional)</span>
						<Input bind:value={moveInDepositReference} placeholder="Check or confirmation number" />
					</div>
				</div>
			{/if}
		{:else}
			<p class="text-sm text-muted-foreground">This unit does not have a current lease to confirm.</p>
		{/if}
		<Dialog.Footer class="mt-4">
			<Button variant="outline" onclick={closeMoveInDialog}>Cancel</Button>
			<Button
				onclick={() => moveInDialogLease && submitMoveIn(moveInDialogLease)}
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
		<UnitFields bind:form={unitForm} errors={unitFormErrors} propertyType={dashboard?.unit.propertyType} testidPrefix="unit-detail" />
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
