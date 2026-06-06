<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { owners } from '$lib/api/endpoints/owners';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { leases } from '$lib/api/endpoints/leases';
	import type { Owner, Property, Unit, Tenant, OwnerEntityType } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import {
		settingsSchema,
		ownerSchema,
		propertySchema,
		unitSchema,
		tenantSchema,
		leaseSchema,
		parseForm,
	} from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import Progress from '$lib/components/ui/Progress.svelte';
	import StateSelect from '$lib/components/shared/StateSelect.svelte';
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import {
		Building,
		UserCircle2,
		Home,
		Users,
		FileText,
		CheckCircle2,
		Check,
		ArrowLeft,
		ArrowRight,
		Plus,
		Trash2,
		PartyPopper,
		Sparkles,
		FileSpreadsheet,
	} from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	// ---------------------------------------------------------------------------
	// Wizard state
	// ---------------------------------------------------------------------------
	type StepKey = 'portfolio' | 'owner' | 'property' | 'tenants' | 'lease';
	const STEPS: { key: StepKey; label: string; icon: typeof Building }[] = [
		{ key: 'portfolio', label: 'Portfolio', icon: Building },
		{ key: 'owner', label: 'Owner', icon: UserCircle2 },
		{ key: 'property', label: 'Property', icon: Home },
		{ key: 'tenants', label: 'Tenants', icon: Users },
		{ key: 'lease', label: 'Lease', icon: FileText },
	];

	let stepIndex = $state(0);
	let finished = $state(false);
	const currentStep = $derived(STEPS[stepIndex]);

	// Remember what the wizard created so later steps can reference it.
	let createdOwner = $state<Owner | null>(null);
	let createdProperty = $state<Property | null>(null);
	let createdUnits = $state<Unit[]>([]);
	let createdTenants = $state<Tenant[]>([]);
	let createdLease = $state(false);
	let portfolioSaved = $state(false);

	// ---------------------------------------------------------------------------
	// Existing-data detection (so re-entering is safe and the user can skip ahead)
	// ---------------------------------------------------------------------------
	const portfolioQuery = createQuery(() => ({
		queryKey: ['portfolio', portfolioId],
		queryFn: () => portfolios.get(portfolioId),
	}));
	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId],
		queryFn: () => owners.list(portfolioId, { take: 200 }),
	}));
	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
	}));
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId, { take: 200 }),
	}));
	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId],
		queryFn: () => leases.list(portfolioId, { take: 50 }),
	}));

	// Onboarding is for LIVE accounts only. A Sandbox account is pre-seeded demo data — the user
	// should "Go Live" first (which wipes the demo data), so we bounce them to the dashboard.
	const sandboxQuery = createQuery(() => ({
		queryKey: ['sandbox-state', portfolioId],
		enabled: portfolioId > 0,
		queryFn: () => portfolios.sandboxState(),
		staleTime: 60_000,
	}));
	let redirectedFromSandbox = $state(false);
	$effect(() => {
		if (redirectedFromSandbox) return;
		if (sandboxQuery.data?.isSandbox === true) {
			redirectedFromSandbox = true;
			showError('Setup runs on a live account. Go live first to set up your real portfolio.');
			goto('/');
		}
	});

	const hasExistingOwners = $derived((ownersQuery.data?.length ?? 0) > 0);
	const hasExistingProperties = $derived((propertiesQuery.data?.length ?? 0) > 0);
	const hasExistingTenants = $derived((tenantsQuery.data?.length ?? 0) > 0);
	const hasExistingLeases = $derived((leasesQuery.data?.length ?? 0) > 0);

	// A step counts as "done" if the wizard created something for it OR data already exists.
	const stepDone = $derived<Record<StepKey, boolean>>({
		portfolio: portfolioSaved || !!portfolioQuery.data?.name,
		owner: !!createdOwner || hasExistingOwners,
		property: !!createdProperty || hasExistingProperties,
		tenants: createdTenants.length > 0 || hasExistingTenants,
		lease: createdLease || hasExistingLeases,
	});

	// On first load, jump straight to the first step that still needs the user. Existing
	// portfolios/owners/properties pre-complete earlier steps, so we don't make the user click
	// "Skip" through them. Runs once, only after the detection queries have settled, so manual
	// back/next still work afterward.
	let autoAdvanced = $state(false);
	const detectionReady = $derived(
		portfolioQuery.isSuccess &&
			ownersQuery.isSuccess &&
			propertiesQuery.isSuccess &&
			tenantsQuery.isSuccess &&
			leasesQuery.isSuccess
	);
	$effect(() => {
		if (autoAdvanced || finished || !detectionReady) return;
		// Don't reposition while we're bouncing a sandbox user to the dashboard.
		if (sandboxQuery.data?.isSandbox === true) return;
		autoAdvanced = true;
		const firstIncomplete = STEPS.findIndex((s) => !stepDone[s.key]);
		// All steps already satisfied → land on the last step (lease) rather than past the end.
		stepIndex = firstIncomplete === -1 ? STEPS.length - 1 : firstIncomplete;
	});

	// ---------------------------------------------------------------------------
	// Step 1 — Portfolio
	// ---------------------------------------------------------------------------
	let portfolioForm = $state({ name: '', managementCompanyName: '' });
	let portfolioErrors = $state<Record<string, string>>({});
	let portfolioPrefilled = false;

	$effect(() => {
		const p = portfolioQuery.data;
		if (p && !portfolioPrefilled) {
			portfolioPrefilled = true;
			portfolioForm.name = p.name ?? '';
			portfolioForm.managementCompanyName = p.managementCompanyName ?? '';
		}
	});

	const savePortfolioMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => portfolios.update(portfolioId, data),
		onSuccess: () => {
			portfolioSaved = true;
			showSuccess('Portfolio saved.');
			queryClient.invalidateQueries({ queryKey: ['portfolio', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['dashboard', portfolioId] });
			next();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitPortfolio() {
		const current = portfolioQuery.data;
		const result = parseForm(settingsSchema, {
			name: portfolioForm.name,
			description: '',
			managementCompanyName: portfolioForm.managementCompanyName,
			timeZone: current?.timeZone ?? '',
			status: current?.status ?? 'Active',
			settings: '',
		});
		if (result.errors) {
			portfolioErrors = result.errors;
			return;
		}
		portfolioErrors = {};
		savePortfolioMutation.mutate({
			name: result.data.name,
			managementCompanyName: result.data.managementCompanyName,
		});
	}

	// ---------------------------------------------------------------------------
	// Step 2 — Owner
	// ---------------------------------------------------------------------------
	const OWNER_ENTITY_TYPES: OwnerEntityType[] = ['Person', 'LLC', 'Trust'];
	let ownerForm = $state({ name: '', ownerEntityType: 'Person' as OwnerEntityType, email: '', taxId: '' });
	let ownerErrors = $state<Record<string, string>>({});

	const saveOwnerMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => owners.create(data),
		onSuccess: (owner) => {
			createdOwner = owner;
			showSuccess('Owner added.');
			queryClient.invalidateQueries({ queryKey: ['owners', portfolioId] });
			next();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitOwner() {
		const result = parseForm(ownerSchema, {
			name: ownerForm.name,
			ownerEntityType: ownerForm.ownerEntityType,
			email: ownerForm.email,
			taxId: ownerForm.taxId,
			address: '',
			phone: '',
		});
		if (result.errors) {
			ownerErrors = result.errors;
			return;
		}
		ownerErrors = {};
		saveOwnerMutation.mutate({ portfolioId, ...result.data });
	}

	// ---------------------------------------------------------------------------
	// Step 3 — Property + Units
	// ---------------------------------------------------------------------------
	const PROPERTY_TYPES = ['SingleFamily', 'MultiFamily', 'Condo', 'Townhome', 'Commercial', 'MixedUse'];
	let propertyForm = $state({
		name: '',
		type: 'SingleFamily',
		addressLine1: '',
		addressLine2: '',
		city: '',
		state: '',
		postalCode: '',
	});
	let propertyErrors = $state<Record<string, string>>({});

	// Inline unit rows. Defaults to one ready-to-fill row.
	const emptyUnit = () => ({ unitNumber: '', bedrooms: '', bathrooms: '', marketRent: '' });
	let unitRows = $state<ReturnType<typeof emptyUnit>[]>([emptyUnit()]);
	let unitRowErrors = $state<Record<string, string>[]>([{}]);

	function addUnitRow() {
		unitRows = [...unitRows, emptyUnit()];
		unitRowErrors = [...unitRowErrors, {}];
	}
	function removeUnitRow(i: number) {
		unitRows = unitRows.filter((_, idx) => idx !== i);
		unitRowErrors = unitRowErrors.filter((_, idx) => idx !== i);
	}

	const savePropertyMutation = createMutation(() => ({
		// Creates the property, then each non-empty unit row in sequence.
		mutationFn: async (vars: {
			property: Record<string, unknown>;
			units: Record<string, unknown>[];
		}) => {
			const property = await properties.create(vars.property);
			const units: Unit[] = [];
			for (const u of vars.units) {
				units.push(await properties.createUnit(property.id, u));
			}
			return { property, units };
		},
		onSuccess: ({ property, units }) => {
			createdProperty = property;
			createdUnits = units;
			showSuccess(
				units.length > 0
					? `Property and ${units.length} unit${units.length === 1 ? '' : 's'} added.`
					: 'Property added.'
			);
			queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
			next();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitProperty() {
		const propResult = parseForm(propertySchema, {
			...propertyForm,
			ownerEntityId: createdOwner ? String(createdOwner.id) : '',
		});
		if (propResult.errors) {
			propertyErrors = propResult.errors;
			return;
		}
		propertyErrors = {};

		// Validate only the rows the user actually filled in (skip fully-blank rows).
		const errors: Record<string, string>[] = unitRows.map(() => ({}));
		const validUnits: Record<string, unknown>[] = [];
		let hasUnitError = false;
		unitRows.forEach((row, i) => {
			const blank = !row.unitNumber.trim() && !row.bedrooms.trim() && !row.bathrooms.trim() && !row.marketRent.trim();
			if (blank) return;
			const res = parseForm(unitSchema, row);
			if (res.errors) {
				errors[i] = res.errors;
				hasUnitError = true;
			} else {
				validUnits.push(res.data);
			}
		});
		unitRowErrors = errors;
		if (hasUnitError) return;

		savePropertyMutation.mutate({
			property: { portfolioId, ...propResult.data },
			units: validUnits,
		});
	}

	// ---------------------------------------------------------------------------
	// Step 4 — Tenants
	// ---------------------------------------------------------------------------
	const emptyTenant = () => ({ firstName: '', lastName: '', email: '', phone: '' });
	let tenantRows = $state<ReturnType<typeof emptyTenant>[]>([emptyTenant()]);
	let tenantRowErrors = $state<Record<string, string>[]>([{}]);

	function addTenantRow() {
		tenantRows = [...tenantRows, emptyTenant()];
		tenantRowErrors = [...tenantRowErrors, {}];
	}
	function removeTenantRow(i: number) {
		tenantRows = tenantRows.filter((_, idx) => idx !== i);
		tenantRowErrors = tenantRowErrors.filter((_, idx) => idx !== i);
	}

	const saveTenantsMutation = createMutation(() => ({
		mutationFn: async (rows: Record<string, unknown>[]) => {
			const made: Tenant[] = [];
			for (const t of rows) {
				made.push(await tenants.create(t));
			}
			return made;
		},
		onSuccess: (made) => {
			createdTenants = [...createdTenants, ...made];
			showSuccess(`${made.length} tenant${made.length === 1 ? '' : 's'} added.`);
			queryClient.invalidateQueries({ queryKey: ['tenants', portfolioId] });
			next();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitTenants() {
		const errors: Record<string, string>[] = tenantRows.map(() => ({}));
		const valid: Record<string, unknown>[] = [];
		let hasError = false;
		tenantRows.forEach((row, i) => {
			const blank = !row.firstName.trim() && !row.lastName.trim() && !row.email.trim() && !row.phone.trim();
			if (blank) return;
			const res = parseForm(tenantSchema, { ...row, emergencyContact: '' });
			if (res.errors) {
				errors[i] = res.errors;
				hasError = true;
			} else {
				valid.push({ portfolioId, ...res.data });
			}
		});
		tenantRowErrors = errors;
		if (hasError) return;
		if (valid.length === 0) {
			// Nothing to add — treat like a skip so the user isn't stuck.
			next();
			return;
		}
		saveTenantsMutation.mutate(valid);
	}

	// ---------------------------------------------------------------------------
	// Step 5 — Lease
	// ---------------------------------------------------------------------------
	const today = new Date();
	const oneYear = new Date(today);
	oneYear.setFullYear(oneYear.getFullYear() + 1);
	const iso = (d: Date) => d.toISOString().slice(0, 10);

	let leaseForm = $state({
		tenantId: '',
		propertyId: '',
		unitId: '',
		startDate: iso(today),
		endDate: iso(oneYear),
		monthlyRent: '',
		securityDeposit: '',
		rentDueDay: '1',
	});
	let leaseErrors = $state<Record<string, string>>({});
	let leasePrefilled = false;

	// Property/unit/tenant pickers default to what was just created.
	const leaseProperties = $derived(propertiesQuery.data ?? []);
	const leaseTenants = $derived(tenantsQuery.data ?? []);

	const leaseUnitsQuery = createQuery(() => ({
		queryKey: ['onboarding-units', leaseForm.propertyId],
		enabled: !!leaseForm.propertyId,
		queryFn: () => properties.listUnits(Number(leaseForm.propertyId)),
	}));

	$effect(() => {
		// Prefill once the relevant queries have data — default to wizard-created items.
		if (leasePrefilled) return;
		if (currentStep.key !== 'lease') return;
		const propId = createdProperty?.id ?? leaseProperties[0]?.id;
		const tenant = createdTenants[0] ?? leaseTenants[0];
		if (propId == null && tenant == null) return;
		leasePrefilled = true;
		if (propId != null) leaseForm.propertyId = String(propId);
		if (tenant != null) leaseForm.tenantId = String(tenant.id);
	});

	$effect(() => {
		// Once units for the chosen property load, default the unit to the first one.
		const list = leaseUnitsQuery.data ?? [];
		if (!leaseForm.unitId && list.length > 0) {
			const preferred = createdUnits.find((u) => list.some((l) => l.id === u.id)) ?? list[0];
			leaseForm.unitId = String(preferred.id);
			if (!leaseForm.monthlyRent && preferred.marketRent != null) {
				leaseForm.monthlyRent = String(preferred.marketRent);
			}
		}
	});

	const saveLeaseMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => leases.create(data),
		onSuccess: () => {
			createdLease = true;
			showSuccess('Lease created.');
			queryClient.invalidateQueries({ queryKey: ['leases', portfolioId] });
			finished = true;
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitLease() {
		// Auto-generate a readable lease number from the unit + start date.
		const unit = (leaseUnitsQuery.data ?? []).find((u) => String(u.id) === leaseForm.unitId);
		const stamp = leaseForm.startDate.replace(/-/g, '');
		const leaseNumber = `L-${unit?.unitNumber ?? leaseForm.unitId}-${stamp}`;
		const result = parseForm(leaseSchema, {
			leaseNumber,
			propertyId: leaseForm.propertyId,
			unitId: leaseForm.unitId,
			tenantId: leaseForm.tenantId,
			startDate: leaseForm.startDate,
			endDate: leaseForm.endDate,
			monthlyRent: leaseForm.monthlyRent,
			securityDeposit: leaseForm.securityDeposit || '0',
			lateFeeAmount: '0',
			rentDueDay: leaseForm.rentDueDay,
			status: 'Active',
			notes: '',
		});
		if (result.errors) {
			leaseErrors = result.errors;
			return;
		}
		leaseErrors = {};
		saveLeaseMutation.mutate({ portfolioId, ...result.data });
	}

	// ---------------------------------------------------------------------------
	// Navigation
	// ---------------------------------------------------------------------------
	function next() {
		if (stepIndex < STEPS.length - 1) {
			stepIndex += 1;
		} else {
			finished = true;
		}
	}
	function back() {
		if (stepIndex > 0) stepIndex -= 1;
	}
	function skip() {
		next();
	}
	function goToStep(i: number) {
		stepIndex = i;
	}

	const anyPending = $derived(
		savePortfolioMutation.isPending ||
			saveOwnerMutation.isPending ||
			savePropertyMutation.isPending ||
			saveTenantsMutation.isPending ||
			saveLeaseMutation.isPending
	);

	// Labels for select triggers.
	const leasePropertyLabel = $derived(
		leaseProperties.find((p) => String(p.id) === leaseForm.propertyId)?.name ?? 'Select property'
	);
	const leaseUnitLabel = $derived.by(() => {
		const u = (leaseUnitsQuery.data ?? []).find((x) => String(x.id) === leaseForm.unitId);
		return u ? `Unit ${u.unitNumber}` : 'Select unit';
	});
	const leaseTenantLabel = $derived.by(() => {
		const t = leaseTenants.find((x) => String(x.id) === leaseForm.tenantId);
		return t ? t.fullName || `${t.firstName} ${t.lastName}` : 'Select tenant';
	});
</script>

<svelte:head>
	<title>Set up your portfolio - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto bg-muted/30 p-6 pb-20" data-testid="onboarding-page">
	<div class="mx-auto max-w-2xl">
		{#if finished}
			<!-- All set -->
			<Card.Root class="mt-10" data-testid="onboarding-complete">
				<Card.Content class="flex flex-col items-center gap-4 px-8 py-12 text-center">
					<div class="flex h-16 w-16 items-center justify-center rounded-full bg-success/15 text-success">
						<PartyPopper class="h-8 w-8" />
					</div>
					<h1 class="text-2xl font-bold">You're all set!</h1>
					<p class="max-w-md text-sm text-muted-foreground">
						Your portfolio is ready to go. You can always add more properties, tenants, and
						leases from the menu on the left. Welcome aboard.
					</p>
					<Button class="mt-2 gap-2" data-testid="onboarding-finish-dashboard" onclick={() => goto('/')}>
						<Sparkles class="h-4 w-4" />
						Go to my dashboard
					</Button>
				</Card.Content>
			</Card.Root>
		{:else}
			<!-- Header + progress -->
			<div class="mb-6">
				<h1 class="text-2xl font-bold" data-testid="onboarding-title">Let's set up your portfolio</h1>
				<p class="mt-1 text-sm text-muted-foreground">
					A few quick steps to go from empty to up-and-running. The computer does the typing — you
					just confirm.
				</p>
			</div>

			<!-- Bulk import shortcut -->
			<div
				class="mb-4 flex flex-wrap items-center justify-between gap-2 rounded-md border border-border bg-muted/40 px-3 py-2.5"
				data-testid="onboarding-spreadsheet-import"
			>
				<span class="text-sm text-foreground">
					Already have your tenants, properties, or units in a spreadsheet?
				</span>
				<Button
					variant="outline"
					size="sm"
					class="gap-1.5"
					href="/import"
					data-testid="onboarding-import-spreadsheet"
				>
					<FileSpreadsheet class="h-4 w-4" />
					Import from a spreadsheet
				</Button>
			</div>

			<!-- Step indicator -->
			<div class="mb-4" data-testid="onboarding-steps">
				<Progress value={stepIndex + 1} max={STEPS.length} class="mb-3" />
				<div class="flex items-center justify-between">
					{#each STEPS as step, i}
						{@const active = i === stepIndex}
						{@const done = stepDone[step.key]}
						<button
							type="button"
							class="flex flex-1 flex-col items-center gap-1 text-center"
							data-testid="onboarding-step-tab-{step.key}"
							onclick={() => goToStep(i)}
						>
							<span
								class="flex h-8 w-8 items-center justify-center rounded-full border text-xs transition-colors
									{active
									? 'border-primary bg-primary text-primary-foreground'
									: done
										? 'border-success bg-success/15 text-success'
										: 'border-border bg-background text-muted-foreground'}"
							>
								{#if done && !active}
									<Check class="h-4 w-4" />
								{:else}
									<step.icon class="h-4 w-4" />
								{/if}
							</span>
							<span class="text-[11px] {active ? 'font-medium text-foreground' : 'text-muted-foreground'}">
								{step.label}
							</span>
						</button>
					{/each}
				</div>
			</div>

			<Card.Root>
				<Card.Content class="p-6">
					<!-- ============ Step 1: Portfolio ============ -->
					{#if currentStep.key === 'portfolio'}
						<div data-testid="onboarding-step-portfolio">
							<div class="mb-4 flex items-center gap-2">
								<Building class="h-5 w-5 text-primary" />
								<h2 class="text-lg font-semibold">Name your portfolio</h2>
							</div>
							<p class="mb-4 text-sm text-muted-foreground">
								This is the name for your whole rental business. You can change it anytime in
								Settings.
							</p>
							<div class="grid gap-4">
								<div>
									<label for="ob-portfolio-name" class="mb-1 block text-xs font-medium text-muted-foreground">
										Portfolio name
									</label>
									<Input
										id="ob-portfolio-name"
										data-testid="onboarding-portfolio-name"
										bind:value={portfolioForm.name}
										placeholder="e.g. Smith Family Rentals"
									/>
									{#if portfolioErrors.name}<p class="mt-1 text-xs text-destructive">{portfolioErrors.name}</p>{/if}
								</div>
								<div>
									<label for="ob-portfolio-company" class="mb-1 block text-xs font-medium text-muted-foreground">
										Management company <span class="font-normal">(optional)</span>
									</label>
									<Input
										id="ob-portfolio-company"
										data-testid="onboarding-portfolio-company"
										bind:value={portfolioForm.managementCompanyName}
										placeholder="e.g. Smith Property Management"
									/>
								</div>
							</div>
						</div>

					<!-- ============ Step 2: Owner ============ -->
					{:else if currentStep.key === 'owner'}
						<div data-testid="onboarding-step-owner">
							<div class="mb-4 flex items-center gap-2">
								<UserCircle2 class="h-5 w-5 text-primary" />
								<h2 class="text-lg font-semibold">Who owns the properties?</h2>
							</div>
							<p class="mb-4 text-sm text-muted-foreground">
								Add the owner — this could be you (a person), or an LLC or trust that holds the
								property. We'll use this for owner reports and tax forms later.
							</p>
							{#if hasExistingOwners && !createdOwner}
								<div class="mb-4 rounded-md border border-success/40 bg-success/10 px-3 py-2 text-sm text-foreground" data-testid="onboarding-owner-existing">
									You already have {ownersQuery.data?.length} owner{(ownersQuery.data?.length ?? 0) === 1 ? '' : 's'} on file. You can add another or skip ahead.
								</div>
							{/if}
							<div class="grid gap-4 sm:grid-cols-2">
								<div class="sm:col-span-2">
									<label for="ob-owner-name" class="mb-1 block text-xs font-medium text-muted-foreground">Owner name</label>
									<Input
										id="ob-owner-name"
										data-testid="onboarding-owner-name"
										bind:value={ownerForm.name}
										placeholder="e.g. John Smith or Smith Holdings LLC"
									/>
									{#if ownerErrors.name}<p class="mt-1 text-xs text-destructive">{ownerErrors.name}</p>{/if}
								</div>
								<div>
									<span class="mb-1 block text-xs font-medium text-muted-foreground">Type</span>
									<Select.Root type="single" bind:value={ownerForm.ownerEntityType}>
										<Select.Trigger class="w-full" data-testid="onboarding-owner-type">
											{ownerForm.ownerEntityType}
										</Select.Trigger>
										<Select.Content>
											{#each OWNER_ENTITY_TYPES as t}
												<Select.Item value={t} label={t}>{t}</Select.Item>
											{/each}
										</Select.Content>
									</Select.Root>
								</div>
								<div>
									<label for="ob-owner-email" class="mb-1 block text-xs font-medium text-muted-foreground">
										Email <span class="font-normal">(optional)</span>
									</label>
									<Input
										id="ob-owner-email"
										data-testid="onboarding-owner-email"
										bind:value={ownerForm.email}
										placeholder="owner@example.com"
									/>
									{#if ownerErrors.email}<p class="mt-1 text-xs text-destructive">{ownerErrors.email}</p>{/if}
								</div>
								<div class="sm:col-span-2">
									<label for="ob-owner-taxid" class="mb-1 block text-xs font-medium text-muted-foreground">
										Tax ID / SSN <span class="font-normal">(optional)</span>
									</label>
									<Input
										id="ob-owner-taxid"
										data-testid="onboarding-owner-taxid"
										bind:value={ownerForm.taxId}
										placeholder="For 1099s and tax reports"
									/>
								</div>
							</div>
						</div>

					<!-- ============ Step 3: Property + Units ============ -->
					{:else if currentStep.key === 'property'}
						<div data-testid="onboarding-step-property">
							<div class="mb-4 flex items-center gap-2">
								<Home class="h-5 w-5 text-primary" />
								<h2 class="text-lg font-semibold">Add a property</h2>
							</div>
							<p class="mb-4 text-sm text-muted-foreground">
								Enter the property's address, then list the rentable units inside it (an apartment,
								a house counts as one unit, a duplex is two, and so on).
							</p>
							{#if hasExistingProperties && !createdProperty}
								<div class="mb-4 rounded-md border border-success/40 bg-success/10 px-3 py-2 text-sm text-foreground" data-testid="onboarding-property-existing">
									You already have {propertiesQuery.data?.length} propert{(propertiesQuery.data?.length ?? 0) === 1 ? 'y' : 'ies'}. Add another or skip ahead.
								</div>
							{/if}
							<div class="grid gap-4 sm:grid-cols-2">
								<div class="sm:col-span-2">
									<label for="ob-prop-name" class="mb-1 block text-xs font-medium text-muted-foreground">Property name</label>
									<Input
										id="ob-prop-name"
										data-testid="onboarding-property-name"
										bind:value={propertyForm.name}
										placeholder="e.g. 123 Main St Duplex"
									/>
									{#if propertyErrors.name}<p class="mt-1 text-xs text-destructive">{propertyErrors.name}</p>{/if}
								</div>
								<div>
									<span class="mb-1 block text-xs font-medium text-muted-foreground">Type</span>
									<Select.Root type="single" bind:value={propertyForm.type}>
										<Select.Trigger class="w-full" data-testid="onboarding-property-type">
											{propertyForm.type}
										</Select.Trigger>
										<Select.Content>
											{#each PROPERTY_TYPES as t}
												<Select.Item value={t} label={t}>{t}</Select.Item>
											{/each}
										</Select.Content>
									</Select.Root>
								</div>
								<div>
									<label for="ob-prop-address" class="mb-1 block text-xs font-medium text-muted-foreground">Street address</label>
									<AddressAutocomplete
										id="ob-prop-address"
										testid="onboarding-property-address"
										bind:value={propertyForm.addressLine1}
										placeholder="123 Main St"
										onresolved={(a) => {
											if (a.city) propertyForm.city = a.city;
											if (a.state) propertyForm.state = a.state;
											if (a.zip) propertyForm.postalCode = a.zip;
										}}
									/>
									{#if propertyErrors.addressLine1}<p class="mt-1 text-xs text-destructive">{propertyErrors.addressLine1}</p>{/if}
								</div>
								<div>
									<label for="ob-prop-address2" class="mb-1 block text-xs font-medium text-muted-foreground">Apt / Suite / Unit # <span class="text-muted-foreground/60">(optional)</span></label>
									<Input id="ob-prop-address2" data-testid="onboarding-property-address2" bind:value={propertyForm.addressLine2} placeholder="Unit 4B" />
								</div>
								<div>
									<label for="ob-prop-city" class="mb-1 block text-xs font-medium text-muted-foreground">City</label>
									<Input id="ob-prop-city" data-testid="onboarding-property-city" bind:value={propertyForm.city} placeholder="City" />
									{#if propertyErrors.city}<p class="mt-1 text-xs text-destructive">{propertyErrors.city}</p>{/if}
								</div>
								<div>
									<label for="ob-prop-state" class="mb-1 block text-xs font-medium text-muted-foreground">State</label>
									<StateSelect id="ob-prop-state" testid="onboarding-property-state" bind:value={propertyForm.state} placeholder="State" />
									{#if propertyErrors.state}<p class="mt-1 text-xs text-destructive">{propertyErrors.state}</p>{/if}
								</div>
								<div>
									<label for="ob-prop-zip" class="mb-1 block text-xs font-medium text-muted-foreground">ZIP</label>
									<Input id="ob-prop-zip" data-testid="onboarding-property-zip" bind:value={propertyForm.postalCode} placeholder="ZIP" />
									{#if propertyErrors.postalCode}<p class="mt-1 text-xs text-destructive">{propertyErrors.postalCode}</p>{/if}
								</div>
							</div>

							<!-- Units -->
							<div class="mt-6">
								<h3 class="mb-2 text-sm font-semibold">Units</h3>
								<div class="space-y-3" data-testid="onboarding-units">
									{#each unitRows as row, i (i)}
										<div class="rounded-md border border-border bg-background p-3" data-testid="onboarding-unit-row">
											<div class="grid gap-2 sm:grid-cols-4">
												<div>
													<span class="mb-1 block text-[11px] text-muted-foreground">Unit #</span>
													<Input data-testid="onboarding-unit-number-{i}" bind:value={row.unitNumber} placeholder="1, A, etc." />
													{#if unitRowErrors[i]?.unitNumber}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].unitNumber}</p>{/if}
												</div>
												<div>
													<span class="mb-1 block text-[11px] text-muted-foreground">Beds</span>
													<Input data-testid="onboarding-unit-beds-{i}" bind:value={row.bedrooms} placeholder="2" />
													{#if unitRowErrors[i]?.bedrooms}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].bedrooms}</p>{/if}
												</div>
												<div>
													<span class="mb-1 block text-[11px] text-muted-foreground">Baths</span>
													<Input data-testid="onboarding-unit-baths-{i}" bind:value={row.bathrooms} placeholder="1" />
													{#if unitRowErrors[i]?.bathrooms}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].bathrooms}</p>{/if}
												</div>
												<div>
													<span class="mb-1 block text-[11px] text-muted-foreground">Market rent</span>
													<div class="flex items-center gap-1">
														<Input data-testid="onboarding-unit-rent-{i}" bind:value={row.marketRent} placeholder="1500" />
														{#if unitRows.length > 1}
															<button
																type="button"
																class="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
																aria-label="Remove unit"
																data-testid="onboarding-unit-remove-{i}"
																onclick={() => removeUnitRow(i)}
															>
																<Trash2 class="h-4 w-4" />
															</button>
														{/if}
													</div>
													{#if unitRowErrors[i]?.marketRent}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].marketRent}</p>{/if}
												</div>
											</div>
										</div>
									{/each}
								</div>
								<Button variant="outline" size="sm" class="mt-2 gap-1" data-testid="onboarding-add-unit" onclick={addUnitRow}>
									<Plus class="h-4 w-4" />
									Add another unit
								</Button>
							</div>
						</div>

					<!-- ============ Step 4: Tenants ============ -->
					{:else if currentStep.key === 'tenants'}
						<div data-testid="onboarding-step-tenants">
							<div class="mb-4 flex items-center gap-2">
								<Users class="h-5 w-5 text-primary" />
								<h2 class="text-lg font-semibold">Add your tenants</h2>
							</div>
							<p class="mb-4 text-sm text-muted-foreground">
								Add the people renting from you. You can add more than one — leave a row blank to
								skip it.
							</p>
							{#if hasExistingTenants && createdTenants.length === 0}
								<div class="mb-4 rounded-md border border-success/40 bg-success/10 px-3 py-2 text-sm text-foreground" data-testid="onboarding-tenants-existing">
									You already have {tenantsQuery.data?.length} tenant{(tenantsQuery.data?.length ?? 0) === 1 ? '' : 's'}. Add more or skip ahead.
								</div>
							{/if}
							<div class="space-y-3" data-testid="onboarding-tenants-list">
								{#each tenantRows as row, i (i)}
									<div class="rounded-md border border-border bg-background p-3" data-testid="onboarding-tenant-row">
										<div class="grid gap-2 sm:grid-cols-2">
											<div>
												<span class="mb-1 block text-[11px] text-muted-foreground">First name</span>
												<Input data-testid="onboarding-tenant-first-{i}" bind:value={row.firstName} placeholder="First name" />
												{#if tenantRowErrors[i]?.firstName}<p class="mt-1 text-[11px] text-destructive">{tenantRowErrors[i].firstName}</p>{/if}
											</div>
											<div>
												<span class="mb-1 block text-[11px] text-muted-foreground">Last name</span>
												<Input data-testid="onboarding-tenant-last-{i}" bind:value={row.lastName} placeholder="Last name" />
												{#if tenantRowErrors[i]?.lastName}<p class="mt-1 text-[11px] text-destructive">{tenantRowErrors[i].lastName}</p>{/if}
											</div>
											<div>
												<span class="mb-1 block text-[11px] text-muted-foreground">Email (optional)</span>
												<Input data-testid="onboarding-tenant-email-{i}" bind:value={row.email} placeholder="tenant@example.com" />
												{#if tenantRowErrors[i]?.email}<p class="mt-1 text-[11px] text-destructive">{tenantRowErrors[i].email}</p>{/if}
											</div>
											<div>
												<span class="mb-1 block text-[11px] text-muted-foreground">Phone (optional)</span>
												<div class="flex items-center gap-1">
													<Input data-testid="onboarding-tenant-phone-{i}" bind:value={row.phone} placeholder="(555) 555-5555" />
													{#if tenantRows.length > 1}
														<button
															type="button"
															class="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
															aria-label="Remove tenant"
															data-testid="onboarding-tenant-remove-{i}"
															onclick={() => removeTenantRow(i)}
														>
															<Trash2 class="h-4 w-4" />
														</button>
													{/if}
												</div>
											</div>
										</div>
									</div>
								{/each}
							</div>
							<Button variant="outline" size="sm" class="mt-2 gap-1" data-testid="onboarding-add-tenant" onclick={addTenantRow}>
								<Plus class="h-4 w-4" />
								Add another tenant
							</Button>
						</div>

					<!-- ============ Step 5: Lease ============ -->
					{:else if currentStep.key === 'lease'}
						<div data-testid="onboarding-step-lease">
							<div class="mb-4 flex items-center gap-2">
								<FileText class="h-5 w-5 text-primary" />
								<h2 class="text-lg font-semibold">Create the first lease</h2>
							</div>
							<p class="mb-4 text-sm text-muted-foreground">
								Link a tenant to a unit and set the rent. We've filled in what we can from the
								steps above — just confirm.
							</p>
							<div class="mb-4 flex flex-wrap items-center justify-between gap-2 rounded-md border border-border bg-muted/40 px-3 py-2.5" data-testid="onboarding-lease-bulk-import">
								<span class="text-sm text-foreground">
									Have existing leases on paper? Import them all at once instead.
								</span>
								<Button variant="outline" size="sm" class="gap-1" href="/scan/batch" data-testid="onboarding-import-leases">
									<FileText class="h-4 w-4" />
									Import leases
								</Button>
							</div>
							{#if leaseProperties.length === 0 || leaseTenants.length === 0}
								<div class="rounded-md border border-warning/40 bg-warning/10 px-3 py-3 text-sm text-foreground" data-testid="onboarding-lease-blocked">
									You'll need at least one property with a unit and one tenant before you can create a
									lease. You can skip this and add it later from the Leases page.
								</div>
							{:else}
								<div class="grid gap-4 sm:grid-cols-2">
									<div>
										<span class="mb-1 block text-xs font-medium text-muted-foreground">Tenant</span>
										<Select.Root type="single" bind:value={leaseForm.tenantId}>
											<Select.Trigger class="w-full" data-testid="onboarding-lease-tenant">{leaseTenantLabel}</Select.Trigger>
											<Select.Content>
												{#each leaseTenants as t}
													<Select.Item value={String(t.id)} label={t.fullName || `${t.firstName} ${t.lastName}`}>
														{t.fullName || `${t.firstName} ${t.lastName}`}
													</Select.Item>
												{/each}
											</Select.Content>
										</Select.Root>
										{#if leaseErrors.tenantId}<p class="mt-1 text-xs text-destructive">{leaseErrors.tenantId}</p>{/if}
									</div>
									<div>
										<span class="mb-1 block text-xs font-medium text-muted-foreground">Property</span>
										<Select.Root type="single" bind:value={leaseForm.propertyId} onValueChange={() => { leaseForm.unitId = ''; }}>
											<Select.Trigger class="w-full" data-testid="onboarding-lease-property">{leasePropertyLabel}</Select.Trigger>
											<Select.Content>
												{#each leaseProperties as p}
													<Select.Item value={String(p.id)} label={p.name}>{p.name}</Select.Item>
												{/each}
											</Select.Content>
										</Select.Root>
									</div>
									<div>
										<span class="mb-1 block text-xs font-medium text-muted-foreground">Unit</span>
										<Select.Root type="single" bind:value={leaseForm.unitId} disabled={!leaseForm.propertyId}>
											<Select.Trigger class="w-full" data-testid="onboarding-lease-unit" disabled={!leaseForm.propertyId}>
												{leaseUnitLabel}
											</Select.Trigger>
											<Select.Content>
												{#each leaseUnitsQuery.data ?? [] as u}
													<Select.Item value={String(u.id)} label="Unit {u.unitNumber}">Unit {u.unitNumber} ({u.status})</Select.Item>
												{/each}
											</Select.Content>
										</Select.Root>
										{#if leaseErrors.unitId}<p class="mt-1 text-xs text-destructive">{leaseErrors.unitId}</p>{/if}
									</div>
									<div>
										<label for="ob-lease-rent" class="mb-1 block text-xs font-medium text-muted-foreground">Monthly rent</label>
										<Input id="ob-lease-rent" data-testid="onboarding-lease-rent" bind:value={leaseForm.monthlyRent} placeholder="1500" />
										{#if leaseErrors.monthlyRent}<p class="mt-1 text-xs text-destructive">{leaseErrors.monthlyRent}</p>{/if}
									</div>
									<div>
										<label for="ob-lease-start" class="mb-1 block text-xs font-medium text-muted-foreground">Start date</label>
										<DatePicker id="ob-lease-start" testid="onboarding-lease-start" bind:value={leaseForm.startDate} placeholder="Start date" />
										{#if leaseErrors.startDate}<p class="mt-1 text-xs text-destructive">{leaseErrors.startDate}</p>{/if}
									</div>
									<div>
										<label for="ob-lease-end" class="mb-1 block text-xs font-medium text-muted-foreground">End date</label>
										<DatePicker id="ob-lease-end" testid="onboarding-lease-end" bind:value={leaseForm.endDate} placeholder="End date" />
										{#if leaseErrors.endDate}<p class="mt-1 text-xs text-destructive">{leaseErrors.endDate}</p>{/if}
									</div>
									<div>
										<label for="ob-lease-deposit" class="mb-1 block text-xs font-medium text-muted-foreground">Security deposit</label>
										<Input id="ob-lease-deposit" data-testid="onboarding-lease-deposit" bind:value={leaseForm.securityDeposit} placeholder="1500" />
										{#if leaseErrors.securityDeposit}<p class="mt-1 text-xs text-destructive">{leaseErrors.securityDeposit}</p>{/if}
									</div>
									<div>
										<label for="ob-lease-dueday" class="mb-1 block text-xs font-medium text-muted-foreground">Rent due day</label>
										<Input id="ob-lease-dueday" data-testid="onboarding-lease-dueday" bind:value={leaseForm.rentDueDay} placeholder="1" />
										{#if leaseErrors.rentDueDay}<p class="mt-1 text-xs text-destructive">{leaseErrors.rentDueDay}</p>{/if}
									</div>
								</div>
							{/if}
						</div>
					{/if}
				</Card.Content>
			</Card.Root>

			<!-- Footer nav -->
			<div class="mt-4 flex items-center justify-between gap-2">
				<Button
					variant="ghost"
					class="gap-1"
					data-testid="onboarding-back"
					disabled={stepIndex === 0 || anyPending}
					onclick={back}
				>
					<ArrowLeft class="h-4 w-4" />
					Back
				</Button>
				<div class="flex items-center gap-2">
					<Button variant="outline" data-testid="onboarding-skip" disabled={anyPending} onclick={skip}>
						Skip this step
					</Button>

					{#if currentStep.key === 'portfolio'}
						<Button class="gap-1" data-testid="onboarding-next" disabled={anyPending} onclick={submitPortfolio}>
							{savePortfolioMutation.isPending ? 'Saving…' : 'Save & continue'}
							<ArrowRight class="h-4 w-4" />
						</Button>
					{:else if currentStep.key === 'owner'}
						<Button class="gap-1" data-testid="onboarding-next" disabled={anyPending} onclick={submitOwner}>
							{saveOwnerMutation.isPending ? 'Saving…' : 'Save & continue'}
							<ArrowRight class="h-4 w-4" />
						</Button>
					{:else if currentStep.key === 'property'}
						<Button class="gap-1" data-testid="onboarding-next" disabled={anyPending} onclick={submitProperty}>
							{savePropertyMutation.isPending ? 'Saving…' : 'Save & continue'}
							<ArrowRight class="h-4 w-4" />
						</Button>
					{:else if currentStep.key === 'tenants'}
						<Button class="gap-1" data-testid="onboarding-next" disabled={anyPending} onclick={submitTenants}>
							{saveTenantsMutation.isPending ? 'Saving…' : 'Save & continue'}
							<ArrowRight class="h-4 w-4" />
						</Button>
					{:else if currentStep.key === 'lease'}
						{#if leaseProperties.length === 0 || leaseTenants.length === 0}
							<Button class="gap-1" data-testid="onboarding-finish" onclick={() => (finished = true)}>
								<CheckCircle2 class="h-4 w-4" />
								Finish
							</Button>
						{:else}
							<Button class="gap-1" data-testid="onboarding-finish" disabled={anyPending} onclick={submitLease}>
								{saveLeaseMutation.isPending ? 'Creating…' : 'Create lease & finish'}
								<CheckCircle2 class="h-4 w-4" />
							</Button>
						{/if}
					{/if}
				</div>
			</div>
		{/if}
	</div>
</div>
