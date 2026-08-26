<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { applications, type ApplicationResponse } from '$lib/api/endpoints/applications';
	import { documentTemplates } from '$lib/api/endpoints/document-templates';
	import {
		leaseManagements,
		type PrepareMoveInResponse
	} from '$lib/api/endpoints/lease-managements';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { units } from '$lib/api/endpoints/units';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import type { PrepareMoveInPrefill } from '$lib/leases/prepare-move-in-prefill';
	import {
		buildPrepareMoveInRequest,
		createPrepareMoveInForm,
		type PrepareMoveInForm,
		type PrepareMoveInFormErrors
	} from '$lib/leases/prepare-move-in-form';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import SimpleSelect from '$lib/components/shared/SimpleSelect.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Input } from '$lib/components/ui/input';
	import { CalendarClock, ChevronDown, FileSignature, UserPlus, Users } from '@lucide/svelte';

	let {
		prefill,
		mode = 'application',
		onclose,
		onprepared
	}: {
		prefill: PrepareMoveInPrefill;
		mode?: 'application' | 'manual';
		onclose: () => void;
		onprepared: (result: PrepareMoveInResponse) => void;
	} = $props();

	const queryClient = useQueryClient();
	// svelte-ignore state_referenced_locally
	const seededApplicationId = Number(prefill.applicationId) || 0;
	const manualMode = $derived(mode === 'manual' && seededApplicationId === 0);
	const portfolioId = $derived(getCurrentPortfolioId());
	// svelte-ignore state_referenced_locally
	const requiredTenantId = Number(prefill.tenantId) || 0;
	// svelte-ignore state_referenced_locally
	let form = $state<PrepareMoveInForm>(createPrepareMoveInForm(prefill));
	let errors = $state<PrepareMoveInFormErrors>({});
	let applicationSearch = $state('');
	let unitSearch = $state('');
	let templateSearch = $state('');
	let selectedUnitLabel = $state('');
	let selectedTenantLabel = $state('');
	let moreOpen = $state(false);
	let migrationOpen = $state(false);
	let movedInDateEdited = $state(false);
	let appliedApplicationId = 0;
	let appliedUnitId = 0;
	let operation: { fingerprint: string; key: string } | null = null;

	const moreFields: (keyof PrepareMoveInForm)[] = [
		'lateFeeAmount',
		'gracePeriodDays',
		'documentTemplateId',
		'partyEffectiveFrom'
	];
	const migrationFields: (keyof PrepareMoveInForm)[] = [
		'rentTrackingStartMode',
		'rentTrackingStartOn',
		'openingBalanceAmount',
		'openingBalanceEffectiveOn',
		'openingBalanceNote'
	];

	const approvedApplicationsQuery = createQuery(() => ({
		queryKey: ['applications', 'prepare-move-in', 'approved', applicationSearch],
		queryFn: () =>
			applications.listPage({
				status: 'Approved',
				search: applicationSearch || undefined,
				sort: '-reviewedAtUtc',
				take: 50
			}),
		enabled: !manualMode && seededApplicationId === 0
	}));

	const selectedApplicationId = $derived(Number(form.applicationId) || 0);
	const applicationQuery = createQuery(() => ({
		queryKey: ['application', selectedApplicationId, 'prepare-move-in'],
		queryFn: () => applications.get(selectedApplicationId),
		enabled: selectedApplicationId > 0
	}));
	const application = $derived<ApplicationResponse | undefined>(applicationQuery.data);

	$effect(() => {
		if (!application || appliedApplicationId === application.id) return;
		appliedApplicationId = application.id;
		form.unitId = application.unitId ? String(application.unitId) : '';
		form.tenantId = requiredTenantId
			? String(requiredTenantId)
			: application.approvedTenantId
				? String(application.approvedTenantId)
				: '';
		const requestedMoveIn = application.desiredMoveInDate?.slice(0, 10) ?? '';
		if (!form.plannedPossessionOn) form.plannedPossessionOn = requestedMoveIn;
		if (requestedMoveIn) form.termStartOn = requestedMoveIn;
	});

	// The move-in date follows the lease start unless the landlord sets it themselves.
	$effect(() => {
		if (movedInDateEdited) return;
		if (form.termStartOn) form.partyEffectiveFrom = form.termStartOn;
	});

	const selectedUnitId = $derived(Number(form.unitId) || 0);
	const unitQuery = createQuery(() => ({
		queryKey: ['unit', selectedUnitId, 'prepare-move-in'],
		queryFn: () => units.get(selectedUnitId),
		enabled: selectedUnitId > 0
	}));
	const selectedPropertyId = $derived(application?.propertyId ?? unitQuery.data?.propertyId ?? null);
	$effect(() => {
		const unit = unitQuery.data;
		if (!unit || appliedUnitId === unit.id) return;
		appliedUnitId = unit.id;
		if (manualMode && !selectedUnitLabel) {
			selectedUnitLabel = `Unit ${unit.unitNumber}`;
		}
		if (!form.baseRentAmount && Number.isFinite(unit.marketRent)) {
			form.baseRentAmount = String(unit.marketRent);
		}
	});

	const unitOptionsQuery = createQuery(() => ({
		queryKey: ['units', 'prepare-move-in', application?.propertyId, unitSearch],
		queryFn: () =>
			units.listWithHealthPage({
				propertyId: application?.propertyId ?? undefined,
				search: unitSearch || undefined,
				sort: 'unitNumber',
				take: 50
			}),
		enabled: Boolean(application && !application.unitId)
	}));

	const tenantQuery = createQuery(() => ({
		queryKey: ['tenant', Number(form.tenantId) || 0, 'prepare-move-in'],
		queryFn: () => tenants.get(Number(form.tenantId)),
		enabled: Number(form.tenantId) > 0
	}));

	const templatesQuery = createQuery(() => ({
		queryKey: ['document-templates', 'prepare-move-in', selectedPropertyId, templateSearch],
		queryFn: () =>
			documentTemplates.listPage({
				kind: 'Lease',
				status: 'Active',
				propertyId: selectedPropertyId ?? undefined,
				search: templateSearch || undefined,
				sort: 'name',
				take: 50
			})
	}));
	// The landlord already told us which lease form they use — don't make them pick it again.
	let defaultTemplateApplied = false;
	$effect(() => {
		if (defaultTemplateApplied || form.documentTemplateId) return;
		const preferred = (templatesQuery.data?.items ?? []).find((template) => template.defaultForPortfolio);
		if (!preferred) return;
		defaultTemplateApplied = true;
		form.documentTemplateId = String(preferred.id);
	});

	const selectedTemplateId = $derived(Number(form.documentTemplateId) || 0);
	const selectedTemplateQuery = createQuery(() => ({
		queryKey: ['document-template', selectedTemplateId, 'prepare-move-in'],
		queryFn: () => documentTemplates.get(selectedTemplateId),
		enabled: selectedTemplateId > 0
	}));

	async function loadManualUnitOptions(params: { search?: string; skip: number; take: number }) {
		const result = await units.listWithHealthPage({
			...params,
			sort: 'propertyName',
			status: 'Vacant'
		});
		return {
			...result,
			items: result.items.map((unit) => ({
				id: unit.id,
				label: `${unit.propertyName} · Unit ${unit.unitNumber}`,
				description: `${formatStatusLabel(unit.status)} · ${formatStatusLabel(unit.simpleStage)}`
			}))
		};
	}

	async function loadManualTenantOptions(params: { search?: string; skip: number; take: number }) {
		const result = await tenants.listPage(portfolioId, {
			...params,
			sort: 'lastName',
			availableForLease: true
		});
		return {
			...result,
			items: result.items.map((tenant) => ({
				id: tenant.id,
				label: tenant.fullName || `${tenant.firstName} ${tenant.lastName}`,
				description: tenant.email || tenant.phone || null
			}))
		};
	}

	function clearError(field: keyof PrepareMoveInForm) {
		if (!errors[field]) return;
		const { [field]: _removed, ...rest } = errors;
		errors = rest;
	}

	function contextError(): string | null {
		if (manualMode) return null;
		if (selectedApplicationId <= 0) return 'Choose an approved application.';
		if (!application) {
			if (applicationQuery.isLoading) return 'Loading the approved application…';
			return 'The approved application could not be loaded.';
		}
		if (application.status !== 'Approved')
			return 'Only an approved application can prepare a move-in.';
		if (!application.approvedTenantId) return 'This approved application has no linked tenant.';
		if (requiredTenantId && application.approvedTenantId !== requiredTenantId) {
			return 'Choose the approved application for the tenant that opened this workflow.';
		}
		if (application.unitId && selectedUnitId !== application.unitId) {
			return 'The requested unit from the approved application must remain selected.';
		}
		return null;
	}

	function templateError(): string | null {
		if (selectedTemplateId <= 0) return null;
		if (selectedTemplateQuery.isLoading) return 'Loading the selected lease template…';
		const template = selectedTemplateQuery.data;
		if (!template) return 'The selected lease template could not be loaded.';
		if (template.kind !== 'Lease' || template.status !== 'Active') {
			return 'Choose an active lease template.';
		}
		if (template.propertyId && template.propertyId !== selectedPropertyId) {
			return 'Choose a lease template for the requested property.';
		}
		return null;
	}

	function operationKey(request: object): string {
		const fingerprint = JSON.stringify(request);
		if (!operation || operation.fingerprint !== fingerprint) {
			operation = { fingerprint, key: crypto.randomUUID() };
		}
		return operation.key;
	}

	const prepareMutation = createMutation(() => ({
		mutationFn: ({
			request,
			key
		}: {
			request: NonNullable<ReturnType<typeof buildPrepareMoveInRequest>['request']>;
			key: string;
		}) => leaseManagements.prepareMoveIn(request, key),
		onSuccess: (result) => {
			showSuccess('Move-in prepared. The tenant and lease draft are ready.');
			queryClient.invalidateQueries({ queryKey: ['applications'] });
			queryClient.invalidateQueries({ queryKey: ['lease-managements'] });
			queryClient.invalidateQueries({ queryKey: ['units'] });
			onprepared(result);
		},
		onError: (error) => showError(apiErrorMessage(error, 'Could not prepare the move-in.'))
	}));

	function submit() {
		const validation = buildPrepareMoveInRequest(form, { requireApplication: !manualMode });
		const selectionError = contextError();
		if (selectionError) validation.errors.applicationId = selectionError;
		const selectedTemplateError = templateError();
		if (selectedTemplateError) validation.errors.documentTemplateId = selectedTemplateError;
		if (!validation.request || Object.keys(validation.errors).length > 0) {
			errors = validation.errors;
			if (moreFields.some((field) => errors[field])) moreOpen = true;
			if (migrationFields.some((field) => errors[field])) migrationOpen = true;
			return;
		}
		prepareMutation.mutate({ request: validation.request, key: operationKey(validation.request) });
	}
</script>

<Dialog.Root
	open
	onOpenChange={(open) => {
		if (!open && !prepareMutation.isPending) onclose();
	}}
>
	<Dialog.Content
		class="max-h-[90vh] max-w-2xl overflow-y-auto"
		onInteractOutside={(event) => {
			if (prepareMutation.isPending) event.preventDefault();
		}}
		onEscapeKeydown={(event) => {
			if (prepareMutation.isPending) event.preventDefault();
		}}
		data-testid="prepare-move-in-dialog"
	>
		<Dialog.Header>
			<Dialog.Title>{manualMode ? 'Create lease' : 'Prepare move-in'}</Dialog.Title>
		</Dialog.Header>

		<div class="space-y-6" data-testid="movein-review">
			{#if manualMode}
				<div class="rounded-xl border bg-muted/20 p-4" data-testid="prepare-move-in-manual-context">
					<p class="font-semibold">No application needed</p>
					<p class="text-sm text-muted-foreground">
						This sets up the tenant, the lease, and the rent schedule in one go.
					</p>
				</div>
			{:else if seededApplicationId > 0}
				<div class="rounded-xl border bg-muted/20 p-4" data-testid="prepare-move-in-locked-application">
					<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">
						Approved application
					</p>
					{#if applicationQuery.isLoading}
						<div class="mt-2 h-10 animate-pulse rounded-lg bg-muted"></div>
					{:else if application}
						<p class="mt-1 font-semibold">{application.firstName} {application.lastName}</p>
						<p class="text-sm text-muted-foreground">
							Application #{application.id} · {formatStatusLabel(application.status)}
						</p>
					{:else}
						<p class="mt-2 text-sm text-destructive">The application could not be loaded.</p>
					{/if}
				</div>
			{:else}
				<section class="space-y-2">
					<label class="block text-sm font-medium" for="prepare-application-search"
						>Find approved application</label
					>
					<Input
						id="prepare-application-search"
						bind:value={applicationSearch}
						placeholder="Search applicant name, email, or rental"
						data-testid="prepare-move-in-application-search"
					/>
					<SimpleSelect
						bind:value={form.applicationId}
						options={(approvedApplicationsQuery.data?.items ?? []).map((option) => ({
							value: String(option.id),
							label: `${option.firstName} ${option.lastName}${option.unitNumber ? ` · Unit ${option.unitNumber}` : ''}`
						}))}
						placeholder="Choose an approved application"
						ariaLabel="Approved application"
						onchange={() => clearError('applicationId')}
						testid="prepare-move-in-application-input"
					/>
					{#if approvedApplicationsQuery.isLoading}<p class="text-xs text-muted-foreground">
							Loading approved applications…
						</p>{/if}
					{#if approvedApplicationsQuery.isError}<p class="text-xs text-destructive">
							Approved applications could not be loaded.
						</p>{/if}
				</section>
			{/if}
			{#if errors.applicationId}<p
					class="text-xs text-destructive"
					data-testid="prepare-move-in-application-error"
				>
					{errors.applicationId}
				</p>{/if}

			<section class="space-y-3">
				<h3 class="text-sm font-semibold">Tenant</h3>
				{#if manualMode}
					<div class="space-y-3" data-testid="prepare-move-in-manual-tenant">
						<div class="grid grid-cols-2 gap-2" data-testid="prepare-move-in-tenant-source">
							<Button
								type="button"
								variant={form.createNewTenant ? 'outline' : 'default'}
								onclick={() => {
									form.createNewTenant = false;
									form.tenantId = '';
									selectedTenantLabel = '';
									clearError('tenantId');
								}}
								class="gap-2"><Users class="h-4 w-4" /> Tenant I already have</Button
							>
							<Button
								type="button"
								variant={form.createNewTenant ? 'default' : 'outline'}
								onclick={() => {
									form.createNewTenant = true;
									form.tenantId = '';
									selectedTenantLabel = '';
									clearError('tenantId');
								}}
								class="gap-2"><UserPlus class="h-4 w-4" /> New tenant</Button
							>
						</div>
						{#if form.createNewTenant}
							<div class="grid gap-3 sm:grid-cols-2" data-testid="prepare-move-in-new-tenant-fields">
								<div>
									<label class="mb-1 block text-sm font-medium" for="prepare-new-tenant-first"
										>First name</label
									>
									<Input
										id="prepare-new-tenant-first"
										bind:value={form.newTenantFirstName}
										oninput={() => clearError('newTenantFirstName')}
										data-testid="prepare-move-in-new-tenant-first-name"
									/>
									{#if errors.newTenantFirstName}<p class="mt-1 text-xs text-destructive">
											{errors.newTenantFirstName}
										</p>{/if}
								</div>
								<div>
									<label class="mb-1 block text-sm font-medium" for="prepare-new-tenant-last"
										>Last name</label
									>
									<Input
										id="prepare-new-tenant-last"
										bind:value={form.newTenantLastName}
										oninput={() => clearError('newTenantLastName')}
										data-testid="prepare-move-in-new-tenant-last-name"
									/>
									{#if errors.newTenantLastName}<p class="mt-1 text-xs text-destructive">
											{errors.newTenantLastName}
										</p>{/if}
								</div>
								<div>
									<label class="mb-1 block text-sm font-medium" for="prepare-new-tenant-email"
										>Email</label
									>
									<Input
										id="prepare-new-tenant-email"
										type="email"
										bind:value={form.newTenantEmail}
										oninput={() => clearError('newTenantEmail')}
										data-testid="prepare-move-in-new-tenant-email"
									/>
									{#if errors.newTenantEmail}<p class="mt-1 text-xs text-destructive">
											{errors.newTenantEmail}
										</p>{/if}
								</div>
								<div>
									<label class="mb-1 block text-sm font-medium" for="prepare-new-tenant-phone"
										>Phone</label
									>
									<Input
										id="prepare-new-tenant-phone"
										type="tel"
										inputmode="tel"
										mask="phone"
										bind:value={form.newTenantPhone}
										data-testid="prepare-move-in-new-tenant-phone"
									/>
								</div>
							</div>
						{:else}
							<RemoteRecordSelect
								queryKey={['manual-lease-tenants', portfolioId]}
								label="Tenant"
								hideLabel
								bind:value={form.tenantId}
								selectedLabel={selectedTenantLabel}
								placeholder="Choose a tenant"
								searchPlaceholder="Search tenants…"
								emptyLabel="No tenants match this search"
								testid="prepare-move-in-existing-tenant"
								loadPage={loadManualTenantOptions}
								onValueChange={(_value, option) => {
									selectedTenantLabel = option?.label ?? '';
									clearError('tenantId');
								}}
							/>
							{#if errors.tenantId}<p class="text-xs text-destructive">{errors.tenantId}</p>{/if}
						{/if}
					</div>
				{:else}
					<div class="flex items-start gap-3">
						<div
							class="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-primary/10 text-primary"
						>
							<Users class="h-4 w-4" />
						</div>
						<div>
							{#if tenantQuery.isLoading}<p class="text-sm text-muted-foreground">
									Loading tenant…
								</p>
							{:else if tenantQuery.data}<p class="font-semibold">
									{tenantQuery.data.firstName}
									{tenantQuery.data.lastName}
								</p>
								<p class="text-xs text-muted-foreground">Signs the lease first</p>
							{:else}<p class="text-sm text-destructive">No approved tenant is available.</p>{/if}
						</div>
					</div>
				{/if}
			</section>

			<section class="space-y-3">
				<h3 class="text-sm font-semibold">Which unit</h3>
				{#if manualMode}
					<RemoteRecordSelect
						queryKey={['manual-lease-units', portfolioId]}
						label="Which unit"
						hideLabel
						bind:value={form.unitId}
						selectedLabel={selectedUnitLabel}
						placeholder="Choose a vacant unit"
						searchPlaceholder="Search property or unit…"
						emptyLabel="No vacant units match this search"
						testid="prepare-move-in-manual-unit"
						loadPage={loadManualUnitOptions}
						onValueChange={(_value, option) => {
							selectedUnitLabel = option?.label ?? '';
							clearError('unitId');
						}}
					/>
				{:else if application?.unitId}
					<div class="rounded-xl border bg-muted/20 p-4" data-testid="prepare-move-in-locked-unit">
						<p class="font-medium">
							{application.propertyName ?? 'Requested property'}{application.unitNumber
								? ` · Unit ${application.unitNumber}`
								: ''}
						</p>
						<p class="text-xs text-muted-foreground">Carried over from the application</p>
					</div>
				{:else}
					<Input
						bind:value={unitSearch}
						placeholder="Search units in the requested property"
						data-testid="prepare-move-in-unit-search"
					/>
					<SimpleSelect
						bind:value={form.unitId}
						onchange={() => clearError('unitId')}
						options={(unitOptionsQuery.data?.items ?? []).map((unit) => ({
							value: String(unit.id),
							label: `${unit.propertyName} · Unit ${unit.unitNumber} · ${formatStatusLabel(unit.status)}`
						}))}
						placeholder="Choose a unit"
						ariaLabel="Which unit"
						testid="prepare-move-in-unit-input"
					/>
					{#if unitOptionsQuery.isLoading}<p class="text-xs text-muted-foreground">
							Loading units…
						</p>{/if}
					{#if unitOptionsQuery.isError}<p class="text-xs text-destructive">
							Units could not be loaded.
						</p>{/if}
				{/if}
				{#if errors.unitId}<p class="text-xs text-destructive" data-testid="prepare-move-in-unit-error">
						{errors.unitId}
					</p>{/if}
			</section>

			<section class="space-y-3">
				<h3 class="text-sm font-semibold">Lease dates</h3>
				<div class="grid gap-4 sm:grid-cols-2">
					<div>
						<p class="mb-1 block text-sm font-medium">Lease type</p>
						<SimpleSelect
							bind:value={form.termType}
							onchange={() => {
								form.termEndOn = '';
								clearError('termEndOn');
							}}
							options={[
								{ value: 'FixedTerm', label: 'Set end date' },
								{ value: 'MonthToMonth', label: 'Month to month' }
							]}
							ariaLabel="Lease type"
							testid="prepare-move-in-term-type-input"
						/>
					</div>
					<div>
						<label class="mb-1 block text-sm font-medium" for="prepare-term-start"
							>Lease starts</label
						>
						<DatePicker
							id="prepare-term-start"
							bind:value={form.termStartOn}
							onchange={() => clearError('termStartOn')}
							testid="prepare-move-in-term-start-input"
						/>
						{#if errors.termStartOn}<p class="mt-1 text-xs text-destructive">
								{errors.termStartOn}
							</p>{/if}
					</div>
					{#if form.termType === 'FixedTerm'}
						<div>
							<label class="mb-1 block text-sm font-medium" for="prepare-term-end">Lease ends</label>
							<DatePicker
								id="prepare-term-end"
								bind:value={form.termEndOn}
								min={form.termStartOn || undefined}
								onchange={() => clearError('termEndOn')}
								testid="prepare-move-in-term-end-input"
							/>
							{#if errors.termEndOn}<p class="mt-1 text-xs text-destructive">
									{errors.termEndOn}
								</p>{/if}
						</div>
					{/if}
				</div>
			</section>

			<section class="space-y-3">
				<h3 class="text-sm font-semibold">Rent</h3>
				<div class="grid gap-4 sm:grid-cols-2">
					<div>
						<label class="mb-1 block text-sm font-medium" for="prepare-rent">Monthly rent</label
						><Input
							id="prepare-rent"
							type="text"
							inputmode="decimal"
							mask="currency"
							bind:value={form.baseRentAmount}
							oninput={() => clearError('baseRentAmount')}
							data-testid="prepare-move-in-rent-input"
						/>{#if errors.baseRentAmount}<p class="mt-1 text-xs text-destructive">
								{errors.baseRentAmount}
							</p>{/if}
					</div>
					<div>
						<label class="mb-1 block text-sm font-medium" for="prepare-due-day"
							>Rent is due on day</label
						><Input
							id="prepare-due-day"
							type="text"
							inputmode="numeric"
							mask="integer"
							bind:value={form.rentDueDay}
							oninput={() => clearError('rentDueDay')}
							placeholder="1–31"
							data-testid="prepare-move-in-due-day-input"
						/>{#if errors.rentDueDay}<p class="mt-1 text-xs text-destructive">
								{errors.rentDueDay}
							</p>{/if}
					</div>
				</div>
			</section>

			<section class="space-y-3">
				<h3 class="text-sm font-semibold">Deposit</h3>
				<div class="grid gap-4 sm:grid-cols-2">
					<div>
						<label class="mb-1 block text-sm font-medium" for="prepare-deposit"
							>Deposit the tenant owes</label
						><Input
							id="prepare-deposit"
							type="text"
							inputmode="decimal"
							mask="currency"
							bind:value={form.securityDepositObligation}
							oninput={() => clearError('securityDepositObligation')}
							placeholder="Enter 0 when none"
							data-testid="prepare-move-in-deposit-input"
						/>{#if errors.securityDepositObligation}<p class="mt-1 text-xs text-destructive">
								{errors.securityDepositObligation}
							</p>{/if}
					</div>
				</div>
				<label class="flex items-start gap-3 rounded-xl border p-4">
					<Checkbox
						bind:checked={form.createSecurityDepositAccount}
						data-testid="prepare-move-in-create-deposit-account"
					/>
					<span
						><span class="block text-sm font-medium">Keep the deposit money separate</span><span
							class="block text-xs text-muted-foreground"
							>You will record the deposit payment when it arrives.</span
						></span
					>
				</label>
			</section>

			<details
				class="rounded-xl border px-4 py-3"
				bind:open={moreOpen}
				data-testid="movein-more"
			>
				<summary class="flex cursor-pointer list-none items-center justify-between gap-2">
					<span class="text-sm font-semibold">More details</span>
					<ChevronDown class="h-4 w-4 text-muted-foreground" />
				</summary>
				<div class="mt-4 grid gap-4 sm:grid-cols-2">
					<div>
						<label class="mb-1 block text-sm font-medium" for="prepare-late-fee">Late fee</label
						><Input
							id="prepare-late-fee"
							type="text"
							inputmode="decimal"
							mask="currency"
							bind:value={form.lateFeeAmount}
							oninput={() => clearError('lateFeeAmount')}
							placeholder="Enter 0 when none"
							data-testid="prepare-move-in-late-fee-input"
						/>{#if errors.lateFeeAmount}<p class="mt-1 text-xs text-destructive">
								{errors.lateFeeAmount}
							</p>{/if}
					</div>
					<div>
						<label class="mb-1 block text-sm font-medium" for="prepare-grace"
							>Days late before a fee</label
						><Input
							id="prepare-grace"
							type="text"
							inputmode="numeric"
							mask="integer"
							bind:value={form.gracePeriodDays}
							oninput={() => clearError('gracePeriodDays')}
							placeholder="0–31"
							data-testid="prepare-move-in-grace-input"
						/>{#if errors.gracePeriodDays}<p class="mt-1 text-xs text-destructive">
								{errors.gracePeriodDays}
							</p>{/if}
					</div>
					<div>
						<label class="mb-1 block text-sm font-medium" for="prepare-party-effective"
							>Move-in date</label
						>
						<DatePicker
							id="prepare-party-effective"
							bind:value={form.partyEffectiveFrom}
							onchange={() => {
								movedInDateEdited = true;
								clearError('partyEffectiveFrom');
							}}
							testid="prepare-move-in-party-effective-input"
						/>
						{#if errors.partyEffectiveFrom}<p class="mt-1 text-xs text-destructive">
								{errors.partyEffectiveFrom}
							</p>{/if}
					</div>
					<div class="sm:col-span-2">
						<p class="mb-1 block text-sm font-medium">Which lease to use</p>
						<Input
							id="prepare-template-search"
							aria-label="Search your lease documents"
							bind:value={templateSearch}
							placeholder="Search your lease documents"
							data-testid="prepare-move-in-template-search"
						/>
						<div class="mt-2">
							<SimpleSelect
								bind:value={form.documentTemplateId}
								onchange={() => clearError('documentTemplateId')}
								options={[
									{ value: '', label: 'Rental Command lease (recommended)' },
									...(templatesQuery.data?.items ?? []).map((template) => ({
										value: String(template.id),
										label: `${template.name}${template.defaultForPortfolio ? ' · your default' : ''}`
									}))
								]}
								ariaLabel="Which lease to use"
								testid="prepare-move-in-template-input"
							/>
						</div>
						<p class="mt-1 text-xs text-muted-foreground">
							Use the lease that comes with Rental Command, or one of your own PDFs.
						</p>
						{#if templatesQuery.isLoading}<p class="text-xs text-muted-foreground">
								Loading your lease documents…
							</p>{/if}
						{#if templatesQuery.isError}<p class="text-xs text-destructive">
								Your lease documents could not be loaded.
							</p>{/if}
						{#if errors.documentTemplateId}<p class="text-xs text-destructive">
								{errors.documentTemplateId}
							</p>{/if}
					</div>
				</div>
			</details>

			<details
				class="rounded-xl border px-4 py-3"
				bind:open={migrationOpen}
				data-testid="movein-migration"
			>
				<summary class="flex cursor-pointer list-none items-center justify-between gap-2">
					<span class="text-sm font-semibold">Moving an existing lease into Rental Command?</span>
					<ChevronDown class="h-4 w-4 text-muted-foreground" />
				</summary>
				<div class="mt-4 space-y-4">
					<div class="grid gap-4 sm:grid-cols-2">
						<div class={form.rentTrackingStartMode === 'CustomCutoffDate' ? '' : 'sm:col-span-2'}>
							<p class="mb-1 block text-sm font-medium">Start charging rent</p>
							<SimpleSelect
								bind:value={form.rentTrackingStartMode}
								onchange={() => {
									if (form.rentTrackingStartMode !== 'CustomCutoffDate') {
										form.rentTrackingStartOn = '';
									}
									clearError('rentTrackingStartMode');
									clearError('rentTrackingStartOn');
								}}
								options={[
									{ value: 'ForwardOnly', label: 'From today' },
									{ value: 'BackfillFromLeaseStart', label: 'Add the past rent since the lease started' },
									{ value: 'CustomCutoffDate', label: 'From a date I choose' }
								]}
								ariaLabel="Start charging rent"
								testid="prepare-move-in-rent-tracking-mode"
							/>
						</div>
						{#if form.rentTrackingStartMode === 'CustomCutoffDate'}
							<div>
								<label class="mb-1 block text-sm font-medium" for="prepare-rent-tracking-start"
									>Start rent on</label
								>
								<DatePicker
									id="prepare-rent-tracking-start"
									bind:value={form.rentTrackingStartOn}
									min={form.termStartOn || undefined}
									onchange={() => clearError('rentTrackingStartOn')}
									testid="prepare-move-in-rent-tracking-date"
								/>
								{#if errors.rentTrackingStartOn}<p class="mt-1 text-xs text-destructive">
										{errors.rentTrackingStartOn}
									</p>{/if}
							</div>
						{/if}
					</div>
					<div class="grid gap-4 sm:grid-cols-2">
						<div>
							<label class="mb-1 block text-sm font-medium" for="prepare-opening-amount"
								>Money already owed or paid ahead</label
							><Input
								id="prepare-opening-amount"
								type="text"
								inputmode="decimal"
								bind:value={form.openingBalanceAmount}
								oninput={() => clearError('openingBalanceAmount')}
								placeholder="Use a minus sign if the tenant is ahead"
								data-testid="prepare-move-in-opening-amount-input"
							/>{#if errors.openingBalanceAmount}<p class="mt-1 text-xs text-destructive">
									{errors.openingBalanceAmount}
								</p>{/if}
						</div>
						<div>
							<label class="mb-1 block text-sm font-medium" for="prepare-opening-date"
								>As of</label
							><DatePicker
								id="prepare-opening-date"
								bind:value={form.openingBalanceEffectiveOn}
								onchange={() => clearError('openingBalanceEffectiveOn')}
								testid="prepare-move-in-opening-date-input"
							/>{#if errors.openingBalanceEffectiveOn}<p class="mt-1 text-xs text-destructive">
									{errors.openingBalanceEffectiveOn}
								</p>{/if}
						</div>
						<div class="sm:col-span-2">
							<label class="mb-1 block text-sm font-medium" for="prepare-opening-note">Note</label
							><Input
								id="prepare-opening-note"
								bind:value={form.openingBalanceNote}
								oninput={() => clearError('openingBalanceNote')}
								maxlength={500}
								data-testid="prepare-move-in-opening-note-input"
							/>{#if errors.openingBalanceNote}<p class="mt-1 text-xs text-destructive">
									{errors.openingBalanceNote}
								</p>{/if}
						</div>
					</div>
				</div>
			</details>

			<div
				class="grid gap-3 rounded-xl border bg-muted/20 p-4 sm:grid-cols-[auto_1fr]"
				data-testid="prepare-move-in-review"
			>
				<CalendarClock class="mt-0.5 h-5 w-5 text-primary" />
				<div>
					<p class="font-medium">What happens next</p>
					<p class="text-sm text-muted-foreground">
						Rental Command sets up the tenant, the rent schedule, {form.createSecurityDepositAccount
							? 'the deposit, '
							: ''}and a lease ready for signing.
					</p>
				</div>
			</div>
		</div>

		<Dialog.Footer class="flex-col-reverse gap-2 sm:flex-row sm:justify-between">
			<Button
				variant="outline"
				onclick={onclose}
				disabled={prepareMutation.isPending}
				data-testid="prepare-move-in-cancel">Cancel</Button
			>
			<Button
				onclick={submit}
				disabled={prepareMutation.isPending}
				class="gap-2"
				data-testid="prepare-move-in-submit"
				><FileSignature class="h-4 w-4" />
				{prepareMutation.isPending ? 'Preparing…' : 'Prepare move-in'}</Button
			>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
