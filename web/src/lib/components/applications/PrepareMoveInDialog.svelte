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
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import StepperNextButton from '$lib/components/shared/StepperNextButton.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Input } from '$lib/components/ui/input';
	import { CalendarClock, FileSignature, UserPlus, Users } from '@lucide/svelte';

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
	let currentStep = $state(0);
	let completedSteps = $state<number[]>([]);
	let applicationSearch = $state('');
	let unitSearch = $state('');
	let templateSearch = $state('');
	let selectedUnitLabel = $state('');
	let selectedTenantLabel = $state('');
	let appliedApplicationId = 0;
	let appliedUnitId = 0;
	let operation: { fingerprint: string; key: string } | null = null;

	const steps: FormStepperStep[] = [
		{ id: 'context', label: 'Who & where', description: 'Application, tenant, unit' },
		{ id: 'agreement', label: 'Agreement', description: 'Template and term' },
		{ id: 'money', label: 'Money & review', description: 'Rent, deposit, opening balance' }
	];
	const stepFields: (keyof PrepareMoveInForm)[][] = [
		[
			'applicationId',
			'unitId',
			'tenantId',
			'newTenantFirstName',
			'newTenantLastName',
			'newTenantEmail',
			'partyEffectiveFrom'
		],
		['documentTemplateId', 'termType', 'termStartOn', 'termEndOn'],
		[
			'baseRentAmount',
			'rentDueDay',
			'securityDepositObligation',
			'lateFeeAmount',
			'gracePeriodDays',
			'rentTrackingStartMode',
			'rentTrackingStartOn',
			'openingBalanceAmount',
			'openingBalanceEffectiveOn',
			'openingBalanceNote'
		]
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
		if (!form.partyEffectiveFrom) form.partyEffectiveFrom = requestedMoveIn;
		if (!form.termStartOn) form.termStartOn = requestedMoveIn;
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
		queryKey: [
			'document-templates',
			'prepare-move-in',
			selectedPropertyId,
			templateSearch
		],
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

	function validateStep(step: number): boolean {
		const validation = buildPrepareMoveInRequest(form, { requireApplication: !manualMode });
		const fields = new Set(stepFields[step]);
		const relevant = Object.fromEntries(
			Object.entries(validation.errors).filter(([field]) =>
				fields.has(field as keyof PrepareMoveInForm)
			)
		) as PrepareMoveInFormErrors;
		if (step === 0) {
			const selectionError = contextError();
			if (selectionError) relevant.applicationId = selectionError;
		}
		if (step === 1) {
			const selectionError = templateError();
			if (selectionError) relevant.documentTemplateId = selectionError;
		}
		const retained = Object.fromEntries(
			Object.entries(errors).filter(([field]) => !fields.has(field as keyof PrepareMoveInForm))
		) as PrepareMoveInFormErrors;
		errors = { ...retained, ...relevant };
		return Object.keys(relevant).length === 0;
	}

	function nextStep() {
		if (!validateStep(currentStep)) return;
		if (!completedSteps.includes(currentStep)) completedSteps = [...completedSteps, currentStep];
		currentStep = Math.min(currentStep + 1, steps.length - 1);
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
			showSuccess('Move-in prepared. The tenant relationship and agreement draft are ready.');
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
			const firstInvalidStep = stepFields.findIndex((fields) =>
				fields.some((field) => errors[field])
			);
			if (firstInvalidStep >= 0) currentStep = firstInvalidStep;
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
		class="max-w-3xl"
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
			<Dialog.Description>
				{manualMode
					? 'Choose the rental, tenant, agreement terms, and opening money. This creates one planned tenant relationship, account, and agreement draft together.'
					: 'Confirm the approved application, exact rental, agreement terms, and opening money. This creates one planned tenant relationship, account, and agreement draft together.'}
			</Dialog.Description>
		</Dialog.Header>

		<FormStepper {steps} bind:currentStep {completedSteps} testid="prepare-move-in-stepper">
			{#if currentStep === 0}
				<div
					class="grid gap-4 md:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)]"
					data-testid="prepare-move-in-context-step"
				>
					<div class="space-y-4">
						{#if manualMode}
							<div
								class="rounded-xl border bg-muted/20 p-4"
								data-testid="prepare-move-in-manual-context"
							>
								<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">
									Manual lease
								</p>
								<p class="mt-1 font-semibold">No application required</p>
								<p class="text-sm text-muted-foreground">
									The lease, tenant relationship, rent account, and rent tracking are created by the
									move-in setup.
								</p>
							</div>
						{:else if seededApplicationId > 0}
							<div
								class="rounded-xl border bg-muted/20 p-4"
								data-testid="prepare-move-in-locked-application"
							>
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
							<div class="space-y-2">
								<label class="block text-sm font-medium" for="prepare-application-search">Find approved application</label>
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
							</div>
						{/if}
						{#if errors.applicationId}<p
								class="text-xs text-destructive"
								data-testid="prepare-move-in-application-error"
							>
								{errors.applicationId}
							</p>{/if}

						<div class="space-y-2">
							<p class="block text-sm font-medium">Exact rental</p>
							{#if manualMode}
								<RemoteRecordSelect
									queryKey={['manual-lease-units', portfolioId]}
									label="Exact rental"
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
								<div
									class="rounded-xl border bg-muted/20 p-4"
									data-testid="prepare-move-in-locked-unit"
								>
									<p class="font-medium">
										{application.propertyName ?? 'Requested property'}{application.unitNumber
											? ` · Unit ${application.unitNumber}`
											: ''}
									</p>
									<p class="text-xs text-muted-foreground">Carried from the approved application</p>
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
									placeholder="Choose the exact rental"
									ariaLabel="Exact rental"
									testid="prepare-move-in-unit-input"
								/>
								{#if unitOptionsQuery.isLoading}<p class="text-xs text-muted-foreground">
										Loading units…
									</p>{/if}
								{#if unitOptionsQuery.isError}<p class="text-xs text-destructive">
										Units could not be loaded.
									</p>{/if}
							{/if}
							{#if errors.unitId}<p
									class="text-xs text-destructive"
									data-testid="prepare-move-in-unit-error"
								>
									{errors.unitId}
								</p>{/if}
						</div>
					</div>

					<div class="space-y-4 border-t pt-4 md:border-l md:border-t-0 md:pl-5 md:pt-0">
						{#if manualMode}
							<div class="space-y-3" data-testid="prepare-move-in-manual-tenant">
								<div class="flex items-start gap-3">
									<div
										class="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-primary/10 text-primary"
									>
										<UserPlus class="h-4 w-4" />
									</div>
									<div>
										<p class="text-sm font-medium">Primary tenant</p>
										<p class="text-xs text-muted-foreground">
											Pick an existing tenant or create the primary tenant during this atomic move-in.
										</p>
									</div>
								</div>
								<div class="grid grid-cols-2 gap-2" data-testid="prepare-move-in-tenant-source">
									<Button
										type="button"
										variant={form.createNewTenant ? 'outline' : 'default'}
										onclick={() => {
											form.createNewTenant = false;
											form.tenantId = '';
											selectedTenantLabel = '';
											clearError('tenantId');
										}}>Use existing</Button
									>
									<Button
										type="button"
										variant={form.createNewTenant ? 'default' : 'outline'}
										onclick={() => {
											form.createNewTenant = true;
											form.tenantId = '';
											selectedTenantLabel = '';
											clearError('tenantId');
										}}>Create new</Button
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
										<div class="sm:col-span-2">
											<label class="mb-1 block text-sm font-medium" for="prepare-new-tenant-emergency"
												>Emergency contact</label
											>
											<Input
												id="prepare-new-tenant-emergency"
												bind:value={form.newTenantEmergencyContact}
												data-testid="prepare-move-in-new-tenant-emergency"
											/>
										</div>
									</div>
								{:else}
									<RemoteRecordSelect
										queryKey={['manual-lease-tenants', portfolioId]}
										label="Existing tenant"
										bind:value={form.tenantId}
										selectedLabel={selectedTenantLabel}
										placeholder="Choose an available tenant"
										searchPlaceholder="Search tenants…"
										emptyLabel="No available tenants match this search"
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
								<p class="text-sm font-medium">Primary tenant</p>
								{#if tenantQuery.isLoading}<p class="text-sm text-muted-foreground">
										Loading tenant…
									</p>
								{:else if tenantQuery.data}<p class="font-semibold">
										{tenantQuery.data.firstName}
										{tenantQuery.data.lastName}
									</p>
									<p class="text-xs text-muted-foreground">Required signer · signing order 1</p>
								{:else}<p class="text-sm text-destructive">No approved tenant is available.</p>{/if}
							</div>
						</div>
						{/if}
						<div>
							<label class="mb-1 block text-sm font-medium" for="prepare-party-effective"
								>Household relationship begins</label
							>
							<DatePicker
								id="prepare-party-effective"
								bind:value={form.partyEffectiveFrom}
								onchange={() => clearError('partyEffectiveFrom')}
								testid="prepare-move-in-party-effective-input"
							/>
							{#if errors.partyEffectiveFrom}<p class="mt-1 text-xs text-destructive">
									{errors.partyEffectiveFrom}
								</p>{/if}
						</div>
						<div>
							<label class="mb-1 block text-sm font-medium" for="prepare-possession"
								>Planned possession <span class="text-muted-foreground">(optional)</span></label
							>
							<DatePicker
								id="prepare-possession"
								bind:value={form.plannedPossessionOn}
								testid="prepare-move-in-possession-input"
							/>
							<p class="mt-1 text-xs text-muted-foreground">
								This plans the handoff; it does not mark the unit occupied.
							</p>
						</div>
					</div>
				</div>
			{:else if currentStep === 1}
				<div class="grid gap-4 md:grid-cols-2" data-testid="prepare-move-in-agreement-step">
					<div class="space-y-2 md:col-span-2">
						<p class="block text-sm font-medium">Lease document</p>
						<Input
							id="prepare-template-search"
							aria-label="Search active lease templates"
							bind:value={templateSearch}
							placeholder="Search active lease templates"
							data-testid="prepare-move-in-template-search"
						/>
						<SimpleSelect
							bind:value={form.documentTemplateId}
							onchange={() => clearError('documentTemplateId')}
							options={[
								{ value: '', label: 'Rental Command lease (recommended)' },
								...(templatesQuery.data?.items ?? []).map((template) => ({
									value: String(template.id),
									label: `${template.name}${template.defaultForPortfolio ? ' · portfolio default' : ''}`
								}))
							]}
							ariaLabel="Lease document"
							testid="prepare-move-in-template-input"
						/>
						<p class="text-xs text-muted-foreground">
							Use the supplied lease now, or choose an active landlord PDF prepared in Lease Templates.
						</p>
						{#if templatesQuery.isLoading}<p class="text-xs text-muted-foreground">
								Loading active lease templates…
							</p>{/if}
						{#if templatesQuery.isError}<p class="text-xs text-destructive">
								Lease templates could not be loaded.
							</p>{/if}
						{#if errors.documentTemplateId}<p class="text-xs text-destructive">
								{errors.documentTemplateId}
							</p>{/if}
					</div>
					<div>
						<p class="mb-1 block text-sm font-medium">Term type</p>
						<SimpleSelect
							bind:value={form.termType}
							onchange={() => {
								form.termEndOn = '';
								clearError('termEndOn');
							}}
							options={[{ value: 'FixedTerm', label: 'Fixed term' }, { value: 'MonthToMonth', label: 'Month to month' }]}
							ariaLabel="Term type"
							testid="prepare-move-in-term-type-input"
						/>
					</div>
					<div>
						<label class="mb-1 block text-sm font-medium" for="prepare-term-start"
							>Agreement starts</label
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
							<label class="mb-1 block text-sm font-medium" for="prepare-term-end"
								>Agreement ends</label
							>
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
			{:else}
				<div class="space-y-5" data-testid="prepare-move-in-money-step">
					<div class="grid gap-4 rounded-xl border bg-muted/20 p-4 sm:grid-cols-2">
						<div class={form.rentTrackingStartMode === 'CustomCutoffDate' ? '' : 'sm:col-span-2'}>
							<p class="mb-1 block text-sm font-medium">Begin rent charges</p>
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
									{ value: 'ForwardOnly', label: 'Start from the current date' },
									{ value: 'BackfillFromLeaseStart', label: 'Add past rent from the lease start' },
									{ value: 'CustomCutoffDate', label: 'Start from a custom date' }
								]}
								ariaLabel="Begin rent charges"
								testid="prepare-move-in-rent-tracking-mode"
							/>
							<p class="mt-1 text-xs text-muted-foreground">
								This controls the first rent period Rental Command posts to the tenant ledger.
							</p>
						</div>
						{#if form.rentTrackingStartMode === 'CustomCutoffDate'}
							<div>
								<label class="mb-1 block text-sm font-medium" for="prepare-rent-tracking-start"
									>Custom start date</label
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
					<div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
						<div>
							<label class="mb-1 block text-sm font-medium" for="prepare-rent">Base rent</label
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
								>Rent due day</label
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
						<div>
							<label class="mb-1 block text-sm font-medium" for="prepare-deposit"
								>Deposit obligation</label
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
								>Grace period days</label
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
					</div>

					<label class="flex items-start gap-3 rounded-xl border p-4">
						<Checkbox
							bind:checked={form.createSecurityDepositAccount}
							data-testid="prepare-move-in-create-deposit-account"
						/>
						<span
							><span class="block text-sm font-medium">Open a security-deposit account</span><span
								class="block text-xs text-muted-foreground"
								>Creates the dedicated deposit subledger; it does not record a payment.</span
							></span
						>
					</label>

					<div class="border-t pt-4">
						<div class="mb-3">
							<h3 class="text-sm font-semibold">
								Opening balance <span class="font-normal text-muted-foreground">(optional)</span>
							</h3>
							<p class="text-xs text-muted-foreground">
								Use only for a signed balance that already existed before Rental Command.
							</p>
						</div>
						<div class="grid gap-4 sm:grid-cols-2">
							<div>
								<label class="mb-1 block text-sm font-medium" for="prepare-opening-amount"
									>Amount owed or credit</label
								><Input
									id="prepare-opening-amount"
									type="text"
									inputmode="decimal"
									bind:value={form.openingBalanceAmount}
									oninput={() => clearError('openingBalanceAmount')}
									placeholder="Use a negative amount for tenant credit"
									data-testid="prepare-move-in-opening-amount-input"
								/>{#if errors.openingBalanceAmount}<p class="mt-1 text-xs text-destructive">
										{errors.openingBalanceAmount}
									</p>{/if}
							</div>
							<div>
								<label class="mb-1 block text-sm font-medium" for="prepare-opening-date"
									>Effective date</label
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
								<label class="mb-1 block text-sm font-medium" for="prepare-opening-note"
									>Opening balance note</label
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

					<div
						class="grid gap-3 rounded-xl border bg-muted/20 p-4 sm:grid-cols-[auto_1fr]"
						data-testid="prepare-move-in-review"
					>
						<CalendarClock class="mt-0.5 h-5 w-5 text-primary" />
						<div>
							<p class="font-medium">One atomic preparation</p>
							<p class="text-sm text-muted-foreground">
								Creates the planned Tenant & lease relationship, primary tenant party, tenant
								account, {form.createSecurityDepositAccount ? 'security-deposit account, ' : ''}and
								agreement draft from {selectedTemplateQuery.data?.name ?? 'the selected template'}.
							</p>
						</div>
					</div>
				</div>
			{/if}
		</FormStepper>

		<Dialog.Footer class="flex-col-reverse gap-2 sm:flex-row sm:justify-between">
			<Button
				variant="outline"
				onclick={onclose}
				disabled={prepareMutation.isPending}
				data-testid="prepare-move-in-cancel">Cancel</Button
			>
			<div class="flex gap-2">
				{#if currentStep > 0}<Button
						variant="outline"
						onclick={() => (currentStep = Math.max(0, currentStep - 1))}
						disabled={prepareMutation.isPending}
						data-testid="prepare-move-in-back">Back</Button
					>{/if}
				{#if currentStep < steps.length - 1}
					<StepperNextButton
						onclick={nextStep}
						disabled={applicationQuery.isLoading || selectedTemplateQuery.isLoading}
						testid="prepare-move-in-next"
						complete={completedSteps.includes(currentStep)}
					/>
				{:else}
					<Button
						onclick={submit}
						disabled={prepareMutation.isPending}
						class="gap-2"
						data-testid="prepare-move-in-submit"
						><FileSignature class="h-4 w-4" />
						{prepareMutation.isPending ? 'Preparing…' : 'Prepare move-in'}</Button
					>
				{/if}
			</div>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
