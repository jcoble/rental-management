<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { leases } from '$lib/api/endpoints/leases';
	import { tenants } from '$lib/api/endpoints/tenants';
	import type { UnitDashboard } from '$lib/types';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import LeaseDetail from '$lib/components/records/LeaseDetail.svelte';
	import LeaseTermFields from '$lib/components/forms/LeaseTermFields.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { leaseRentTrackingErrors, leaseSchema, parseForm } from '$lib/schemas';
	import { LEASE_STATUSES } from '$lib/leases/lease-list-state';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import {
		buildSimpleTenantPayload,
		createSimpleTenantForm,
		createUnitLeaseForm,
		validateSimpleTenantForm,
		type SimpleTenantForm,
		type UnitLeaseCreateForm,
	} from '$lib/components/unit/unit-lease-create';
	import { FileText, ScanLine, ArrowLeft, Plus, UserPlus, Users } from '@lucide/svelte';

	let {
		dashboard,
		onScan,
	}: {
		dashboard: UnitDashboard;
		onScan: () => void;
	} = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	let showCreateLease = $state(false);
	let tenantMode = $state<'existing' | 'new'>('existing');
	let form = $state<UnitLeaseCreateForm>(createBlankLeaseForm());
	let tenantForm = $state<SimpleTenantForm>(createSimpleTenantForm());
	let formErrors = $state<Record<string, string>>({});
	let tenantErrors = $state<Record<string, string>>({});

	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId, 'unit-lease-create'],
		queryFn: () => tenants.list(portfolioId, { take: 200, sort: 'name' }),
	}));

	const selectedTenantLabel = $derived(
		tenantsQuery.data?.find((tenant) => String(tenant.id) === form.tenantId)
			? (tenantsQuery.data?.find((tenant) => String(tenant.id) === form.tenantId)?.fullName ||
				`${tenantsQuery.data?.find((tenant) => String(tenant.id) === form.tenantId)?.firstName} ${tenantsQuery.data?.find((tenant) => String(tenant.id) === form.tenantId)?.lastName}`)
			: ''
	);

	const unitLabel = $derived(`${dashboard.propertyName} · Unit ${dashboard.unit.unitNumber}`);

	// Which lease to show: an explicit ?lease=<id> (e.g. a prior lease) wins, otherwise the
	// unit's current lease. Inner lease tabs live in LeaseDetail's local state, so they never
	// collide with the unit Command Center's own ?tab=lease.
	const currentLeaseId = $derived(dashboard.currentLease?.id ?? null);
	const requestedLeaseId = $derived(Number(page.url.searchParams.get('lease')) || null);
	const selectedLeaseId = $derived(requestedLeaseId ?? currentLeaseId);
	// Show a "back to current lease" affordance only when viewing a non-current lease via ?lease=.
	const viewingPriorLease = $derived(
		requestedLeaseId !== null && requestedLeaseId !== currentLeaseId
	);

	// Clearing the selection drops ?lease= and falls back to the current lease (or empty state).
	function clearSelection() {
		goto('/units/' + dashboard.unit.id + '?tab=lease', {
			replaceState: true,
			keepFocus: true,
			noScroll: true,
		});
	}

	function clearLeaseError(field: string) {
		const next = clearFieldError(formErrors, field);
		if (next !== formErrors) formErrors = next;
	}

	function clearTenantError(field: string) {
		if (!tenantErrors[field]) return;
		const { [field]: _removed, ...next } = tenantErrors;
		tenantErrors = next;
	}

	function createBlankLeaseForm(): UnitLeaseCreateForm {
		return {
			leaseNumber: '',
			propertyId: '',
			unitId: '',
			tenantId: '',
			startDate: '',
			endDate: '',
			monthlyRent: '',
			securityDeposit: '',
			lateFeeAmount: '75',
			rentDueDay: '1',
			rentTrackingStartMode: 'ForwardOnly',
			rentTrackingStartDate: '',
			status: 'Draft',
			notes: '',
		};
	}

	function createCurrentUnitLeaseForm() {
		return {
			...createUnitLeaseForm({
				unit: {
					id: dashboard.unit.id,
					propertyId: dashboard.unit.propertyId,
					unitNumber: dashboard.unit.unitNumber,
					marketRent: dashboard.unit.marketRent,
				},
				propertyName: dashboard.propertyName,
			}),
			propertyId: String(dashboard.unit.propertyId),
			unitId: String(dashboard.unit.id),
		};
	}

	$effect(() => {
		form.propertyId = String(dashboard.unit.propertyId);
		form.unitId = String(dashboard.unit.id);
	});
	$effect(() => {
		if (form.tenantId) clearLeaseError('tenantId');
	});
	$effect(() => {
		if (form.leaseNumber.trim()) clearLeaseError('leaseNumber');
	});
	$effect(() => {
		if (form.startDate) clearLeaseError('startDate');
	});
	$effect(() => {
		if (form.endDate) clearLeaseError('endDate');
	});
	$effect(() => {
		if (form.monthlyRent) clearLeaseError('monthlyRent');
	});
	$effect(() => {
		if (form.securityDeposit) clearLeaseError('securityDeposit');
	});
	$effect(() => {
		if (form.lateFeeAmount) clearLeaseError('lateFeeAmount');
	});
	$effect(() => {
		if (form.rentDueDay) clearLeaseError('rentDueDay');
	});
	$effect(() => {
		if (form.rentTrackingStartDate) clearLeaseError('rentTrackingStartDate');
	});
	$effect(() => {
		if (form.rentTrackingStartMode !== 'CustomCutoffDate' && form.rentTrackingStartDate) {
			form.rentTrackingStartDate = '';
		}
	});
	$effect(() => {
		if (tenantForm.firstName.trim()) clearTenantError('firstName');
	});
	$effect(() => {
		if (tenantForm.lastName.trim()) clearTenantError('lastName');
	});
	$effect(() => {
		if (tenantForm.email.trim()) clearTenantError('email');
	});
	$effect(() => {
		if (tenantForm.phone.trim()) clearTenantError('phone');
	});

	function resetCreateLeaseForm() {
		form = createCurrentUnitLeaseForm();
		tenantForm = createSimpleTenantForm();
		tenantMode = 'existing';
		formErrors = {};
		tenantErrors = {};
	}

	function openCreateLease() {
		resetCreateLeaseForm();
		showCreateLease = true;
	}

	function closeCreateLease() {
		showCreateLease = false;
		formErrors = {};
		tenantErrors = {};
	}

	const createLeaseMutation = createMutation(() => ({
		mutationFn: async ({
			leaseData,
			simpleTenant,
		}: {
			leaseData: Record<string, unknown>;
			simpleTenant: SimpleTenantForm | null;
		}) => {
			let tenantId = Number(leaseData.tenantId);
			if (simpleTenant) {
				const tenant = await tenants.create(buildSimpleTenantPayload(portfolioId, simpleTenant));
				tenantId = tenant.id;
			}
			return leases.create({ ...leaseData, tenantId });
		},
		onSuccess: (lease, vars) => {
			showSuccess('Lease created.');
			closeCreateLease();
			queryClient.invalidateQueries({ queryKey: ['unit-dashboard', dashboard.unit.id] });
			queryClient.invalidateQueries({ queryKey: ['leases', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['units'] });
			queryClient.invalidateQueries({ queryKey: ['properties'] });
			if (vars.simpleTenant) queryClient.invalidateQueries({ queryKey: ['tenants', portfolioId] });
			goto(`/units/${dashboard.unit.id}?tab=lease&lease=${lease.id}`, {
				replaceState: true,
				keepFocus: true,
				noScroll: true,
			});
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitCreateLease() {
		const tenantValidationErrors =
			tenantMode === 'new' ? validateSimpleTenantForm(tenantForm) : {};
		const leaseInput = {
			...form,
			propertyId: String(dashboard.unit.propertyId),
			unitId: String(dashboard.unit.id),
			tenantId: tenantMode === 'new' ? '1' : form.tenantId,
		};
		const result = parseForm(leaseSchema, leaseInput);
		const rentTrackingErrors = leaseRentTrackingErrors(leaseInput);
		if (
			result.errors ||
			Object.keys(rentTrackingErrors).length > 0 ||
			Object.keys(tenantValidationErrors).length > 0
		) {
			const nextFormErrors = { ...(result.errors ?? {}), ...rentTrackingErrors };
			if (tenantMode === 'new') delete nextFormErrors.tenantId;
			formErrors = nextFormErrors;
			tenantErrors = tenantValidationErrors;
			return;
		}
		formErrors = {};
		tenantErrors = {};
		createLeaseMutation.mutate({
			leaseData: { portfolioId, ...result.data },
			simpleTenant: tenantMode === 'new' ? { ...tenantForm } : null,
		});
	}
</script>

<div class="space-y-4" data-testid="unit-lease-tab">
	{#if selectedLeaseId}
		{#if viewingPriorLease}
			<Button
				variant="outline"
				size="sm"
				class="gap-1"
				onclick={clearSelection}
				data-testid="lease-back-to-current"
			>
				<ArrowLeft class="h-4 w-4" /> Back to current lease
			</Button>
		{/if}
		<LeaseDetail leaseId={selectedLeaseId} onDeleted={clearSelection} />
	{:else}
		<DetailCard title="No current lease" icon={FileText} accent="muted" testid="lease-empty">
			<p class="text-sm text-muted-foreground">
				This unit has no current lease. Add a lease for this unit, or scan an existing signed lease to extract its terms.
			</p>
		</DetailCard>
	{/if}

	<div class="flex flex-wrap gap-2">
		<Button class="gap-2" onclick={openCreateLease} data-testid="unit-lease-add-button">
			<Plus class="h-4 w-4" /> Add Lease
		</Button>
		<Button variant="outline" class="gap-2" onclick={() => onScan()} data-testid="lease-scan">
			<ScanLine class="h-4 w-4" /> Scan / upload lease
		</Button>
	</div>
</div>

<Dialog.Root open={showCreateLease} onOpenChange={(v) => { if (!v) closeCreateLease(); }}>
	<Dialog.Content class="max-w-2xl" data-testid="unit-lease-create-dialog">
		<Dialog.Header>
			<Dialog.Title>Add lease</Dialog.Title>
			<Dialog.Description>
				This lease will be added to {unitLabel}. Choose an existing tenant or add the basic tenant record here.
			</Dialog.Description>
		</Dialog.Header>

		<div class="space-y-4" data-testid="unit-lease-create-form">
			<div class="grid gap-3 rounded-lg border bg-muted/30 p-3 text-sm sm:grid-cols-2">
				<div>
					<p class="text-xs font-medium text-muted-foreground">Property</p>
					<p class="font-medium">{dashboard.propertyName}</p>
				</div>
				<div>
					<p class="text-xs font-medium text-muted-foreground">Unit</p>
					<p class="font-medium">Unit {dashboard.unit.unitNumber}</p>
				</div>
			</div>

			<div class="space-y-3">
				<div class="flex flex-wrap gap-2" aria-label="Tenant mode">
					<Button
						type="button"
						size="sm"
						variant={tenantMode === 'existing' ? 'default' : 'outline'}
						class="gap-2"
						onclick={() => {
							tenantMode = 'existing';
							tenantErrors = {};
						}}
						data-testid="unit-lease-existing-tenant-mode"
					>
						<Users class="h-4 w-4" /> Existing tenant
					</Button>
					<Button
						type="button"
						size="sm"
						variant={tenantMode === 'new' ? 'default' : 'outline'}
						class="gap-2"
						onclick={() => {
							tenantMode = 'new';
							form.tenantId = '';
							clearLeaseError('tenantId');
						}}
						data-testid="unit-lease-new-tenant-mode"
					>
						<UserPlus class="h-4 w-4" /> New tenant
					</Button>
				</div>

				{#if tenantMode === 'existing'}
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Tenant</span>
						<Select.Root type="single" bind:value={form.tenantId}>
							<Select.Trigger
								class="w-full"
								disabled={tenantsQuery.isLoading || (tenantsQuery.data?.length ?? 0) === 0}
								data-testid="unit-lease-tenant-input"
							>
								{#if tenantsQuery.isLoading}
									Loading tenants...
								{:else if selectedTenantLabel}
									{selectedTenantLabel}
								{:else if (tenantsQuery.data?.length ?? 0) === 0}
									No tenants yet
								{:else}
									Select tenant
								{/if}
							</Select.Trigger>
							<Select.Content>
								<Select.Item value="" label="Select tenant">Select tenant</Select.Item>
								{#each tenantsQuery.data || [] as tenant}
									<Select.Item value={String(tenant.id)} label={tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}>
										{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}
									</Select.Item>
								{/each}
							</Select.Content>
						</Select.Root>
						{#if formErrors.tenantId}
							<p class="mt-1 text-xs text-destructive" data-testid="unit-lease-tenant-error">{formErrors.tenantId}</p>
						{/if}
					</div>
				{:else}
					<div class="grid gap-3 sm:grid-cols-2" data-testid="unit-lease-new-tenant-fields">
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">First name</span>
							<Input data-testid="unit-lease-new-tenant-first-name" bind:value={tenantForm.firstName} placeholder="First name" />
							{#if tenantErrors.firstName}
								<p class="mt-1 text-xs text-destructive" data-testid="unit-lease-new-tenant-first-name-error">{tenantErrors.firstName}</p>
							{/if}
						</div>
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Last name</span>
							<Input data-testid="unit-lease-new-tenant-last-name" bind:value={tenantForm.lastName} placeholder="Last name" />
							{#if tenantErrors.lastName}
								<p class="mt-1 text-xs text-destructive" data-testid="unit-lease-new-tenant-last-name-error">{tenantErrors.lastName}</p>
							{/if}
						</div>
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Email</span>
							<Input data-testid="unit-lease-new-tenant-email" bind:value={tenantForm.email} placeholder="Email" />
							{#if tenantErrors.email}
								<p class="mt-1 text-xs text-destructive" data-testid="unit-lease-new-tenant-email-error">{tenantErrors.email}</p>
							{/if}
						</div>
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Phone</span>
							<Input data-testid="unit-lease-new-tenant-phone" bind:value={tenantForm.phone} placeholder="Phone" />
							{#if tenantErrors.phone}
								<p class="mt-1 text-xs text-destructive" data-testid="unit-lease-new-tenant-phone-error">{tenantErrors.phone}</p>
							{/if}
						</div>
					</div>
				{/if}
			</div>

			<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} testidPrefix="unit-lease-create" />
		</div>

		<Dialog.Footer class="mt-4">
			<Button variant="outline" onclick={closeCreateLease} data-testid="unit-lease-create-cancel">Cancel</Button>
			<Button
				onclick={submitCreateLease}
				disabled={createLeaseMutation.isPending}
				data-testid="unit-lease-create-save"
			>
				{createLeaseMutation.isPending ? 'Saving…' : 'Save lease'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
