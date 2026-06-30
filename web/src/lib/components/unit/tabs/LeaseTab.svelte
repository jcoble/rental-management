<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { leases } from '$lib/api/endpoints/leases';
	import { tenants } from '$lib/api/endpoints/tenants';
	import type { Lease, UnitDashboard } from '$lib/types';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import LeaseDetail from '$lib/components/records/LeaseDetail.svelte';
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import LeaseTermFields from '$lib/components/forms/LeaseTermFields.svelte';
	import TenantMultiSelect from '$lib/components/forms/TenantMultiSelect.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Dialog from '$lib/components/ui/dialog';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { leaseRentTrackingErrors, leaseSchema, parseForm } from '$lib/schemas';
	import { defaultLeaseNumber } from '$lib/leases/lease-number';
	import { LEASE_STATUSES } from '$lib/leases/lease-list-state';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatDateOnly } from '$lib/utils/date';
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
	let createLeaseStep = $state(0);
	let completedCreateLeaseSteps = $state<number[]>([]);

	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId, 'unit-lease-create', 'available-for-lease'],
		queryFn: () =>
			tenants.listPage(portfolioId, { take: 200, sort: 'name', availableForLease: true }),
	}));

	const unitLeasesQuery = createQuery(() => ({
		queryKey: ['unit-leases', portfolioId, dashboard.unit.id],
		queryFn: () => leases.listPage(portfolioId, {
			unitId: dashboard.unit.id,
			take: 50,
			sort: '-startDate',
		}),
	}));

	const unitLabel = $derived(`${dashboard.propertyName} · Unit ${dashboard.unit.unitNumber}`);
	const unitLeases = $derived(unitLeasesQuery.data?.items ?? []);
	const availableTenants = $derived(tenantsQuery.data?.items ?? []);

	const createLeaseSteps: FormStepperStep[] = [
		{ id: 'tenants', label: 'Tenants', description: 'Existing or new' },
		{ id: 'identity', label: 'Lease #', description: 'Reference' },
		{ id: 'dates', label: 'Dates', description: 'Start and end' },
		{ id: 'money', label: 'Money', description: 'Rent and deposit' },
		{ id: 'status', label: 'Status', description: 'Tracking' },
	];
	const createLeaseStepFields = [
		['tenantId', 'tenant.firstName', 'tenant.lastName', 'tenant.email', 'tenant.phone'],
		['leaseNumber'],
		['startDate', 'endDate'],
		['monthlyRent', 'securityDeposit', 'lateFeeAmount', 'rentDueDay'],
		['status', 'rentTrackingStartMode', 'rentTrackingStartDate', 'notes'],
	] as const;

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

	function selectLease(leaseId: number) {
		goto(`/units/${dashboard.unit.id}?tab=lease&lease=${leaseId}`, {
			replaceState: true,
			keepFocus: true,
			noScroll: true,
		});
	}

	function leaseLabel(lease: Lease): string {
		return lease.leaseNumber?.trim() || `Lease #${lease.id}`;
	}

	function leaseTenantLabel(lease: Lease): string {
		if (lease.tenantName?.trim()) return lease.tenantName;
		const tenantNames = lease.tenants?.map((tenant) => tenant.name).filter(Boolean) ?? [];
		return tenantNames.length > 0 ? tenantNames.join(', ') : 'No tenant name';
	}

	function leaseTermLabel(lease: Lease): string {
		return `${formatDateOnly(lease.startDate)} - ${formatDateOnly(lease.endDate)}`;
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
			leaseNumber: defaultLeaseNumber(),
			propertyId: '',
			unitId: '',
			tenantId: '',
			tenantIds: [],
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
		if (form.tenantIds.length > 0 || form.tenantId) clearLeaseError('tenantId');
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
		createLeaseStep = 0;
		completedCreateLeaseSteps = [];
	}

	function openCreateLease() {
		resetCreateLeaseForm();
		showCreateLease = true;
	}

	function closeCreateLease() {
		showCreateLease = false;
		formErrors = {};
		tenantErrors = {};
		createLeaseStep = 0;
		completedCreateLeaseSteps = [];
	}

	const createLeaseMutation = createMutation(() => ({
		mutationFn: async ({
			leaseData,
			simpleTenant,
		}: {
			leaseData: Record<string, unknown>;
			simpleTenant: SimpleTenantForm | null;
		}) => {
			let tenantIds = Array.isArray(leaseData.tenantIds)
				? (leaseData.tenantIds as number[])
				: [Number(leaseData.tenantId)].filter((id) => id > 0);
			if (simpleTenant) {
				const tenant = await tenants.create(buildSimpleTenantPayload(portfolioId, simpleTenant));
				tenantIds = [tenant.id];
			}
			return leases.create({ ...leaseData, tenantId: tenantIds[0], tenantIds });
		},
		onSuccess: (lease, vars) => {
			showSuccess('Lease created.');
			closeCreateLease();
			queryClient.invalidateQueries({ queryKey: ['unit-dashboard', dashboard.unit.id] });
			queryClient.invalidateQueries({ queryKey: ['unit-leases', portfolioId, dashboard.unit.id] });
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

	function selectedCreateLeaseTenantIds() {
		return form.tenantIds
			.map((id) => Number(id))
			.filter((id) => Number.isInteger(id) && id > 0);
	}

	function createLeaseValidationState() {
		const tenantValidationErrors =
			tenantMode === 'new' ? validateSimpleTenantForm(tenantForm) : {};
		const selectedTenantIds = selectedCreateLeaseTenantIds();
		const leaseInput = {
			...form,
			propertyId: String(dashboard.unit.propertyId),
			unitId: String(dashboard.unit.id),
			tenantId: tenantMode === 'new' ? '1' : String(selectedTenantIds[0] ?? ''),
		};
		const result = parseForm(leaseSchema, leaseInput);
		const rentTrackingErrors = leaseRentTrackingErrors(leaseInput);
		const tenantSelectionErrors: Record<string, string> =
			tenantMode === 'existing' && selectedTenantIds.length === 0
				? { tenantId: 'Select at least one tenant' }
				: {};
		const nextFormErrors = { ...(result.errors ?? {}), ...rentTrackingErrors, ...tenantSelectionErrors };
		if (tenantMode === 'new') delete nextFormErrors.tenantId;
		return {
			data: result.data,
			formErrors: nextFormErrors,
			tenantErrors: tenantValidationErrors,
			selectedTenantIds,
		};
	}

	function createLeaseStepErrorFields(
		step: number,
		errors: Record<string, string>,
		simpleTenantErrors: Record<string, string>
	) {
		const visibleFields = new Set<string>(createLeaseStepFields[step] ?? []);
		const formEntries = Object.entries(errors).filter(([field]) => visibleFields.has(field));
		const tenantEntries = Object.entries(simpleTenantErrors).map(([field, message]) => [`tenant.${field}`, message] as const)
			.filter(([field]) => visibleFields.has(field));
		return [...formEntries, ...tenantEntries];
	}

	function firstCreateLeaseErrorStep(
		errors: Record<string, string>,
		simpleTenantErrors: Record<string, string>
	) {
		return createLeaseStepFields.findIndex((fields) => fields.some((field) => {
			if (errors[field]) return true;
			if (field.startsWith('tenant.')) return Boolean(simpleTenantErrors[field.replace('tenant.', '')]);
			return false;
		}));
	}

	function markCreateLeaseStepInvalid(step: number) {
		completedCreateLeaseSteps = completedCreateLeaseSteps.filter((completedStep) => completedStep < step);
	}

	function validateCreateLeaseStep(step: number) {
		const validation = createLeaseValidationState();
		const currentEntries = createLeaseStepErrorFields(step, validation.formErrors, validation.tenantErrors);
		const currentErrors = Object.fromEntries(currentEntries.filter(([field]) => !field.startsWith('tenant.')));
		const currentTenantErrors = Object.fromEntries(
			currentEntries
				.filter(([field]) => field.startsWith('tenant.'))
				.map(([field, message]) => [field.replace('tenant.', ''), message])
		);
		const currentFields = new Set<string>(createLeaseStepFields[step] ?? []);
		const nextFormErrors = Object.fromEntries(
			Object.entries(formErrors).filter(([field]) => !currentFields.has(field))
		);
		const nextTenantErrors = Object.fromEntries(
			Object.entries(tenantErrors).filter(([field]) => !currentFields.has(`tenant.${field}`))
		);
		formErrors = { ...nextFormErrors, ...currentErrors };
		tenantErrors = { ...nextTenantErrors, ...currentTenantErrors };
		const isValid = currentEntries.length === 0;
		if (!isValid) markCreateLeaseStepInvalid(step);
		return isValid;
	}

	function nextCreateLeaseStep() {
		if (!validateCreateLeaseStep(createLeaseStep)) return;
		if (!completedCreateLeaseSteps.includes(createLeaseStep)) {
			completedCreateLeaseSteps = [...completedCreateLeaseSteps, createLeaseStep];
		}
		createLeaseStep = Math.min(createLeaseStep + 1, createLeaseSteps.length - 1);
	}

	function submitCreateLease() {
		const validation = createLeaseValidationState();
		if (
			Object.keys(validation.formErrors).length > 0 ||
			Object.keys(validation.tenantErrors).length > 0
		) {
			formErrors = validation.formErrors;
			tenantErrors = validation.tenantErrors;
			const firstErrorStep = firstCreateLeaseErrorStep(validation.formErrors, validation.tenantErrors);
			if (firstErrorStep >= 0) {
				createLeaseStep = firstErrorStep;
				markCreateLeaseStepInvalid(firstErrorStep);
			}
			return;
		}
		if (!validation.data) return;
		formErrors = {};
		tenantErrors = {};
		createLeaseMutation.mutate({
			leaseData: { portfolioId, ...validation.data, tenantIds: validation.selectedTenantIds },
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
		<LeaseDetail
			leaseId={selectedLeaseId}
			expectedUnitId={dashboard.unit.id}
			onUnitMismatch={clearSelection}
			onDeleted={() => {
				queryClient.invalidateQueries({ queryKey: ['unit-dashboard', dashboard.unit.id] });
				queryClient.invalidateQueries({ queryKey: ['unit-leases', portfolioId, dashboard.unit.id] });
				clearSelection();
			}}
		/>
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

	<div class="rounded-lg border border-border bg-card p-4" data-testid="unit-lease-history">
		<div class="flex flex-wrap items-start justify-between gap-2">
			<div>
				<h3 class="text-base font-semibold">Lease history</h3>
				<p class="text-sm text-muted-foreground">Past and current leases attached to this unit.</p>
			</div>
			{#if unitLeasesQuery.isFetching}
				<span class="text-xs text-muted-foreground">Loading...</span>
			{:else if unitLeases.length > 0}
				<span class="text-xs text-muted-foreground">{unitLeases.length} {unitLeases.length === 1 ? 'lease' : 'leases'}</span>
			{/if}
		</div>

		{#if unitLeasesQuery.isError}
			<p class="mt-3 text-sm text-destructive">Lease history could not be loaded.</p>
		{:else if unitLeases.length === 0 && !unitLeasesQuery.isLoading}
			<p class="mt-3 text-sm text-muted-foreground">No lease history yet.</p>
		{:else}
			<div class="mt-3 divide-y divide-border" role="list">
				{#each unitLeases as lease (lease.id)}
					<button
						type="button"
						class={`grid w-full gap-2 py-3 text-left transition hover:bg-muted/40 sm:grid-cols-[minmax(0,1fr)_auto] ${lease.id === selectedLeaseId ? 'bg-muted' : ''}`}
						aria-current={lease.id === selectedLeaseId ? 'true' : undefined}
						onclick={() => selectLease(lease.id)}
						data-testid={`unit-lease-history-item-${lease.id}`}
					>
						<span class="min-w-0">
							<span class="block truncate font-medium">{leaseLabel(lease)}</span>
							<span class="block truncate text-sm text-muted-foreground">
								{leaseTenantLabel(lease)} · {leaseTermLabel(lease)}
							</span>
						</span>
						<span class="flex shrink-0 items-center gap-2">
							<StatusBadge status={lease.status} />
							<span class="text-sm font-medium tabular-nums">
								{new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(lease.monthlyRent)}
							</span>
						</span>
					</button>
				{/each}
			</div>
		{/if}
	</div>
</div>

<Dialog.Root open={showCreateLease} onOpenChange={(v) => { if (!v) closeCreateLease(); }}>
	<Dialog.Content class="max-h-[85vh] max-w-2xl overflow-y-auto" data-testid="unit-lease-create-dialog">
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

			<FormStepper
				steps={createLeaseSteps}
				bind:currentStep={createLeaseStep}
				completedSteps={completedCreateLeaseSteps}
				testid="unit-lease-create-stepper"
			>
				{#if createLeaseStep === 0}
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
									form.tenantIds = [];
									clearLeaseError('tenantId');
								}}
								data-testid="unit-lease-new-tenant-mode"
							>
								<UserPlus class="h-4 w-4" /> New tenant
							</Button>
						</div>

						{#if tenantMode === 'existing'}
							<TenantMultiSelect
								label="Tenants"
								tenants={availableTenants}
								bind:selectedIds={form.tenantIds}
								error={formErrors.tenantId}
								disabled={tenantsQuery.isLoading}
								testid="unit-lease-tenants-input"
							/>
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
									<Input
										data-testid="unit-lease-new-tenant-email"
										type="email"
										autocomplete="email"
										bind:value={tenantForm.email}
										placeholder="Email"
									/>
									{#if tenantErrors.email}
										<p class="mt-1 text-xs text-destructive" data-testid="unit-lease-new-tenant-email-error">{tenantErrors.email}</p>
									{/if}
								</div>
								<div>
									<span class="mb-1 block text-xs font-medium text-muted-foreground">Phone</span>
									<Input
										data-testid="unit-lease-new-tenant-phone"
										type="tel"
										autocomplete="tel"
										inputmode="tel"
										mask="phone"
										bind:value={tenantForm.phone}
										placeholder="Phone"
									/>
									{#if tenantErrors.phone}
										<p class="mt-1 text-xs text-destructive" data-testid="unit-lease-new-tenant-phone-error">{tenantErrors.phone}</p>
									{/if}
								</div>
							</div>
						{/if}
					</div>
				{:else if createLeaseStep === 1}
					<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} section="identity" testidPrefix="unit-lease-create" />
				{:else if createLeaseStep === 2}
					<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} section="dates" testidPrefix="unit-lease-create" />
				{:else if createLeaseStep === 3}
					<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} section="money" testidPrefix="unit-lease-create" />
				{:else}
					<LeaseTermFields bind:form errors={formErrors} statuses={LEASE_STATUSES} section="status" testidPrefix="unit-lease-create" />
				{/if}
			</FormStepper>
		</div>

		<Dialog.Footer class="mt-4">
			<Button variant="outline" onclick={closeCreateLease} data-testid="unit-lease-create-cancel">Cancel</Button>
			{#if createLeaseStep > 0}
				<Button
					variant="outline"
					onclick={() => (createLeaseStep = Math.max(createLeaseStep - 1, 0))}
					data-testid="unit-lease-create-back"
				>
					Back
				</Button>
			{/if}
			{#if createLeaseStep < createLeaseSteps.length - 1}
				<Button onclick={nextCreateLeaseStep} data-testid="unit-lease-create-next">Next</Button>
			{:else}
				<Button
					onclick={submitCreateLease}
					disabled={createLeaseMutation.isPending}
					data-testid="unit-lease-create-save"
				>
					{createLeaseMutation.isPending ? 'Saving…' : 'Save lease'}
				</Button>
			{/if}
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
