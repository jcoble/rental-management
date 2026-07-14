<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { units } from '$lib/api/endpoints/units';
	import { securityDeposits } from '$lib/api/endpoints/securityDeposits';
	import { appointments } from '$lib/api/endpoints/appointments';
	import { leaseManagements } from '$lib/api/endpoints/lease-managements';
	import * as Tabs from '$lib/components/ui/tabs';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import UnitHeader from '$lib/components/unit/UnitHeader.svelte';
	import ScanLauncher from '$lib/components/scan/ScanLauncher.svelte';
	import LifecycleRail from '$lib/components/unit/LifecycleRail.svelte';
	import UnitTimelineRail from '$lib/components/unit/UnitTimelineRail.svelte';
	import OverviewTab from '$lib/components/unit/tabs/OverviewTab.svelte';
	import ListingTab from '$lib/components/unit/tabs/ListingTab.svelte';
	import LeaseTab from '$lib/components/unit/tabs/LeaseTab.svelte';
	import ResidentsTab from '$lib/components/unit/tabs/ResidentsTab.svelte';
	import ApplicationsTab from '$lib/components/unit/tabs/ApplicationsTab.svelte';
	import LedgerTab from '$lib/components/unit/tabs/LedgerTab.svelte';
	import MaintenanceTab from '$lib/components/unit/tabs/MaintenanceTab.svelte';
	import TurnoverTab from '$lib/components/unit/tabs/TurnoverTab.svelte';
	import DocumentsTab from '$lib/components/unit/tabs/DocumentsTab.svelte';
	import TimelineTab from '$lib/components/unit/tabs/TimelineTab.svelte';
	import UnitFields from '$lib/components/forms/UnitFields.svelte';
	import {
		resolveUnitDestination,
		type UnitTab,
		type UnitView,
	} from '$lib/components/unit/unit-tabs';
	import { createUnitEditForm, type UnitEditForm } from '$lib/components/unit/unit-edit-form';
	import type { ScanContext } from '$lib/scan/scan-context';
	import { unitSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import type { UnitLeaseSummary } from '$lib/types';
	import { ArrowLeft, ExternalLink } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const id = $derived(Number(page.params.id));
	const emptyUnitForm: UnitEditForm = { unitNumber: '', bedrooms: '', bathrooms: '', marketRent: '' };

	const initialDestination = resolveUnitDestination(
		page.url.searchParams.get('tab'),
		page.url.searchParams.get('view'),
	);
	let activeTab = $state<UnitTab>(initialDestination.tab);
	let activeView = $state<UnitView | undefined>(initialDestination.view);
	let showEditUnit = $state(false);
	let showMoveInDialog = $state(false);
	let moveInDepositEffectiveOn = $state(new Date().toISOString().slice(0, 10));
	let moveInDepositPaymentMethod = $state('');
	let moveInDepositReference = $state('');
	let moveInDepositErrors = $state<Record<string, string>>({});
	let moveInOperation = $state({ fingerprint: '', depositKey: '', possessionKey: '' });
	let showScanLauncher = $state(false);
	let scanLauncherContext = $state<ScanContext>({});
	let handledMoveInActionKey = $state('');
	let unitForm = $state({ ...emptyUnitForm });
	let unitFormErrors = $state<Record<string, string>>({});

	$effect(() => {
		const destination = resolveUnitDestination(
			page.url.searchParams.get('tab'),
			page.url.searchParams.get('view'),
		);
		if (destination.tab !== activeTab) activeTab = destination.tab;
		if (destination.view !== activeView) activeView = destination.view;
	});

	const contextualParams = [
		'view',
		'wo',
		'app',
		'payment',
		'expense',
		'tenantAccount',
		'leaseManagement',
		'agreement',
		'ledger',
		'action',
	] as const;

	function setTab(tab: string) {
		const destination = resolveUnitDestination(tab);
		activeTab = destination.tab;
		activeView = destination.view;
		const url = new URL(page.url);
		for (const param of contextualParams) url.searchParams.delete(param);
		url.searchParams.set('tab', destination.tab);
		if (destination.view) url.searchParams.set('view', destination.view);
		goto(url, { keepFocus: true, noScroll: true });
	}

	function setView(view: UnitView) {
		const destination = resolveUnitDestination(activeTab, view);
		activeView = destination.view;
		const url = new URL(page.url);
		for (const param of contextualParams) url.searchParams.delete(param);
		url.searchParams.set('tab', activeTab);
		if (destination.view) url.searchParams.set('view', destination.view);
		goto(url, { keepFocus: true, noScroll: true });
	}

	function currentUnitReturnTo() {
		return `${page.url.pathname}${page.url.search}`;
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
			activeTab = 'tenant-lease';
			activeView = 'agreements';
			moveInDepositEffectiveOn = new Date().toISOString().slice(0, 10);
			moveInDepositPaymentMethod = '';
			moveInDepositReference = '';
			moveInDepositErrors = {};
			moveInOperation = { fingerprint: '', depositKey: '', possessionKey: '' };
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
		moveInDepositErrors = {};
		clearMoveInActionUrl();
	}

	const moveInMutation = createMutation(() => ({
		mutationFn: async ({
			lease,
			depositOperationKey,
			possessionOperationKey
		}: {
			lease: UnitLeaseSummary;
			depositOperationKey: string;
			possessionOperationKey: string;
		}) => {
			if (lease.securityDeposit > 0) {
				if (lease.tenantAccountId == null) {
					throw new Error('This move-in does not have a prepared security deposit account. Prepare the approved application before recording funds.');
				}
				const account = await securityDeposits.get(lease.tenantAccountId);
				await securityDeposits.fund(account.tenantAccountId, depositOperationKey, {
					securityDepositAccountId: account.securityDepositAccountId,
					amount: lease.securityDeposit,
					effectiveOn: moveInDepositEffectiveOn,
					description: `Security deposit received at move-in for ${lease.leaseNumber}`,
					paymentMethodSummary: moveInDepositPaymentMethod.trim(),
					...(moveInDepositReference.trim()
						? { externalReference: moveInDepositReference.trim() }
						: {}),
				});
			}

			await leaseManagements.givePossession(
				lease.leaseManagementId,
				{ unitId: id },
				possessionOperationKey
			);

			if (moveInAppointment) {
				await appointments.update(moveInAppointment.id, { status: 'Completed' });
			}

			return true;
		},
		onSuccess: () => {
			showSuccess('Possession given. Move-in confirmed.');
			moveInOperation = { fingerprint: '', depositKey: '', possessionKey: '' };
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
		});
		if (
			moveInOperation.fingerprint !== fingerprint ||
			!moveInOperation.depositKey ||
			!moveInOperation.possessionKey
		) {
			moveInOperation = {
				fingerprint,
				depositKey: crypto.randomUUID(),
				possessionKey: crypto.randomUUID()
			};
		}
		moveInMutation.mutate({
			lease,
			depositOperationKey: moveInOperation.depositKey,
			possessionOperationKey: moveInOperation.possessionKey
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
						<div class="min-w-0 overflow-x-auto sm:flex-1">
							<Tabs.List class="min-w-max" data-testid="unit-tabs">
								<Tabs.Trigger value="summary" data-testid="tab-summary">Summary</Tabs.Trigger>
								<Tabs.Trigger value="leasing" data-testid="tab-leasing">Leasing</Tabs.Trigger>
								<Tabs.Trigger value="tenant-lease" data-testid="tab-tenant-lease">Tenant &amp; lease</Tabs.Trigger>
								<Tabs.Trigger value="money" data-testid="tab-money">Money</Tabs.Trigger>
								<Tabs.Trigger value="maintenance" data-testid="tab-maintenance">Maintenance</Tabs.Trigger>
								<Tabs.Trigger value="documents-history" data-testid="tab-documents-history">Documents &amp; history</Tabs.Trigger>
							</Tabs.List>
						</div>
						<UnitTimelineRail activities={dashboard.recentTimeline} onViewAll={() => setTab('timeline')} />
					</div>

					<Tabs.Content value="summary" class="mt-4">
						<OverviewTab {dashboard} onOpenTab={setTab} />
					</Tabs.Content>
					<Tabs.Content value="leasing" class="mt-4">
						<Tabs.Root value={activeView ?? 'listing'} onValueChange={(view) => setView(view as UnitView)}>
							<Tabs.List data-testid="unit-leasing-tabs">
								<Tabs.Trigger value="listing">Listing</Tabs.Trigger>
								<Tabs.Trigger value="applications">Applications</Tabs.Trigger>
							</Tabs.List>
							<Tabs.Content value="listing" class="mt-4"><ListingTab unitId={dashboard.unit.id} /></Tabs.Content>
							<Tabs.Content value="applications" class="mt-4"><ApplicationsTab {dashboard} /></Tabs.Content>
						</Tabs.Root>
					</Tabs.Content>
					<Tabs.Content value="tenant-lease" class="mt-4">
						<Tabs.Root value={activeView ?? 'agreements'} onValueChange={(view) => setView(view as UnitView)}>
							<Tabs.List data-testid="unit-tenant-lease-tabs">
								<Tabs.Trigger value="agreements">Agreements</Tabs.Trigger>
								<Tabs.Trigger value="residents">Residents</Tabs.Trigger>
							</Tabs.List>
							<Tabs.Content value="agreements" class="mt-4">
								<LeaseTab {dashboard} onScan={() => goScan({ type: 'LeaseAgreement', propertyId: dashboard.unit.propertyId, unitId: dashboard.unit.id, leaseManagementId: dashboard.currentLease?.leaseManagementId ?? undefined, tenantAccountId: dashboard.currentLease?.tenantAccountId ?? undefined, returnTo: `/units/${dashboard.unit.id}?tab=tenant-lease&view=agreements` })} />
							</Tabs.Content>
							<Tabs.Content value="residents" class="mt-4"><ResidentsTab {dashboard} /></Tabs.Content>
						</Tabs.Root>
					</Tabs.Content>
					<Tabs.Content value="money" class="mt-4">
						<LedgerTab {dashboard} onScan={goScan} />
					</Tabs.Content>
					<Tabs.Content value="maintenance" class="mt-4">
						<div class="mb-3 flex flex-wrap items-center justify-between gap-3">
							<Tabs.Root value={activeView ?? 'work-orders'} onValueChange={(view) => setView(view as UnitView)}>
								<Tabs.List data-testid="unit-maintenance-tabs">
									<Tabs.Trigger value="work-orders">Work orders</Tabs.Trigger>
									<Tabs.Trigger value="turnover">Turnover</Tabs.Trigger>
								</Tabs.List>
							</Tabs.Root>
							<div class="flex flex-wrap gap-2">
								<Button href="/maintenance#inspections" variant="outline" size="sm" class="gap-1">Inspections <ExternalLink class="h-3.5 w-3.5" /></Button>
								<Button href="/maintenance/recurring" variant="outline" size="sm" class="gap-1">Recurring work <ExternalLink class="h-3.5 w-3.5" /></Button>
							</div>
						</div>
						{#if activeView === 'turnover'}
							<TurnoverTab {dashboard} onScan={goScan} />
						{:else}
							<MaintenanceTab {dashboard} onScan={goScan} />
						{/if}
					</Tabs.Content>
					<Tabs.Content value="documents-history" class="mt-4">
						<Tabs.Root value={activeView ?? 'documents'} onValueChange={(view) => setView(view as UnitView)}>
							<Tabs.List data-testid="unit-documents-history-tabs">
								<Tabs.Trigger value="documents">Documents</Tabs.Trigger>
								<Tabs.Trigger value="history">History</Tabs.Trigger>
							</Tabs.List>
							<Tabs.Content value="documents" class="mt-4">
								<DocumentsTab
									unitId={dashboard.unit.id}
									docs={dashboard.overview.pendingDocs}
									onScan={() => goScan({ returnTo: `/units/${dashboard.unit.id}?tab=documents-history&view=documents` })}
									onDocumentsChanged={refreshUnitDashboard}
								/>
							</Tabs.Content>
							<Tabs.Content value="history" class="mt-4"><TimelineTab unitId={id} /></Tabs.Content>
						</Tabs.Root>
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
				Use this after keys are handed over. Deposit money is recorded in the account prepared
				from the approved application; this does not create another holding.
			</p>
			{#if moveInDialogLease.securityDeposit > 0}
				<div class="space-y-3 border-t pt-3">
					<div>
						<span class="mb-1 block text-xs text-muted-foreground">Deposit received date</span>
						<Input bind:value={moveInDepositEffectiveOn} type="date" data-testid="unit-move-in-deposit-date" />
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
