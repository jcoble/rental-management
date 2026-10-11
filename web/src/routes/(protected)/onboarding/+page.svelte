<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { browser } from '$app/environment';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { owners } from '$lib/api/endpoints/owners';
	import { properties } from '$lib/api/endpoints/properties';
	import { units } from '$lib/api/endpoints/units';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { leaseManagements } from '$lib/api/endpoints/lease-managements';
	import { scan } from '$lib/api/scan';
	import { notifications } from '$lib/api/endpoints/notifications';
	import type { Owner, Property, Unit, Tenant, OwnerEntityType, RentalStructure } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { getCurrentUser } from '$lib/stores/auth.svelte';
	import {
		settingsSchema,
		ownerSchema,
		propertySchema,
		unitSchemaForPropertyType,
		isResidentialDwellingType,
		tenantSchema,
		leaseSchema,
		parseForm,
	} from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import Progress from '$lib/components/ui/Progress.svelte';
	import StateSelect from '$lib/components/shared/StateSelect.svelte';
	import TimeZoneSelect from '$lib/components/shared/TimeZoneSelect.svelte';
	import { guessBrowserTimeZone } from '$lib/data/timezones';
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import WizardStepScaffold from '$lib/components/onboarding/WizardStepScaffold.svelte';
	import LeasePhotoPrefill from '$lib/components/onboarding/LeasePhotoPrefill.svelte';
	import LeaseFirstImport from '$lib/components/scan/LeaseFirstImport.svelte';
	import LeaseScanSignatureChoice, { type LeaseScanReviewDisposition } from '$lib/components/scan/LeaseScanSignatureChoice.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import {
		WIZARD_STEPS,
		CORE_WIZARD_STEPS,
		type WizardStepKey,
		type WizardStepMeta,
	} from '$lib/onboarding/wizard-steps';
	import { celebrateMilestone, bigFinale } from '$lib/onboarding/celebrations';
	import {
		areOnboardingDetectionQueriesReady,
		hasOnboardingDetectionQueryFailed,
		resolveInitialOnboardingState,
		shouldFinishAfterOptionalStep,
	} from '$lib/onboarding/onboarding-flow-state';
	import { isPersonalAlertSetupComplete } from '$lib/onboarding/alert-completion';
	import {
		type OnboardingLeaseConfirmInput,
	} from '$lib/onboarding/lease-scan-confirm';
	import { createLeaseSubmissionCoordinator } from '$lib/onboarding/lease-submission';
	import { addCalendarYear } from '$lib/utils/parse-date';
	import { businessDateOrToday } from '$lib/utils/business-date';
	import {
		NEW_ONBOARDING_OWNER_VALUE,
		onboardingOwnerFormFromOwner,
		onboardingOwnerRecordOptions,
		ownerEntityIdForOnboarding,
	} from '$lib/onboarding/owner-selection';
	import {
		NEW_ONBOARDING_PROPERTY_VALUE,
		buildOnboardingPropertyPayload,
		onboardingPropertyFormFromProperty,
		onboardingPropertyPrefillCandidate,
		onboardingPropertyRecordOptions,
	} from '$lib/onboarding/property-payload';
	import { defaultLeaseNumber } from '$lib/leases/lease-number';
	import { formatPropertyType, propertyTypeOptions } from '$lib/properties/property-labels';
	import {
		Check,
		ArrowLeft,
		ArrowRight,
		Plus,
		Trash2,
		PartyPopper,
		Sparkles,
		FileSpreadsheet,
		FileText,
		CheckCircle2,
		Building,
		UserCircle2,
		Home,
		Users,
		Bell,
		ListChecks,
		PiggyBank,
	} from '@lucide/svelte';
	import { formatAccountingCurrency } from '$lib/accounting/accounting-display';

	const queryClient = useQueryClient();
	const leaseSubmission = createLeaseSubmissionCoordinator(scan);
	const portfolioId = $derived(getCurrentPortfolioId());
	const SELECTOR_PAGE_SIZE = 20;

	// ---------------------------------------------------------------------------
	// Step model. The ordered flow is the core entity steps (portfolio → lease)
	// followed by the optional personal-alert step. The
	// step metadata (titles, explanations, where-to-find, docs links) lives in the
	// shared registry so Settings can deep-link into the exact same steps.
	// ---------------------------------------------------------------------------
	const STEP_ICONS = { Building, UserCircle2, Home, Users, FileText, Bell } as const;
	const STEPS = WIZARD_STEPS;
	// Stepper redesign (TSK-602): group the flat 8-step row into labeled sections so it reads as
	// "where am I in the journey" instead of 8 squeezed, identical-looking circles. Replaces the
	// separate "Guided import" phase rail, which duplicated this same progress as a second UI.
	const STEP_GROUPS: { label: string; keys: WizardStepKey[] }[] = [
		{ label: 'Setup', keys: ['portfolio', 'owner'] },
		{ label: 'Property', keys: ['import', 'property'] },
		{ label: 'People', keys: ['tenants', 'lease'] },
		{ label: 'Notify', keys: ['notifications'] },
	];

	let stepIndex = $state(0);
	let finished = $state(false);
	const currentStep = $derived<WizardStepMeta>(STEPS[stepIndex]);

	// Where the user came from (e.g. a Settings section). On finishing a step we send them back.
	let fromParam = $state<string | null>(null);

	// Remember what the wizard created so later steps can reference it.
	let createdOwner = $state<Owner | null>(null);
	let createdProperty = $state<Property | null>(null);
	let createdUnits = $state<Unit[]>([]);
	let createdTenants = $state<Tenant[]>([]);
	let createdLease = $state(false);
	let leaseImportCreatedSpine = $state(false);
	let portfolioSaved = $state(false);
	let returnToFinishAfterOptional = $state(false);

	// ---------------------------------------------------------------------------
	// Resumable progress — persisted per portfolio so a reload resumes in place.
	// ---------------------------------------------------------------------------
	const progressKey = $derived(`rc.onboarding.step.${portfolioId}`);
	function persistStep(key: WizardStepKey) {
		if (!browser || portfolioId <= 0) return;
		try {
			localStorage.setItem(progressKey, key);
		} catch {
			/* storage may be unavailable */
		}
	}
	function readPersistedStep(): WizardStepKey | null {
		if (!browser || portfolioId <= 0) return null;
		try {
			const v = localStorage.getItem(progressKey) as WizardStepKey | null;
			return v && STEPS.some((s) => s.key === v) ? v : null;
		} catch {
			return null;
		}
	}

	// ---------------------------------------------------------------------------
	// Existing-data detection (so re-entering is safe and the user can skip ahead)
	// ---------------------------------------------------------------------------
	const portfolioQuery = createQuery(() => ({
		queryKey: ['portfolio', portfolioId],
		queryFn: () => portfolios.get(portfolioId),
	}));
	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId, 'onboarding-first-page'],
		queryFn: () => owners.listPage(portfolioId, { skip: 0, take: SELECTOR_PAGE_SIZE, sort: 'name' }),
	}));
	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId, 'onboarding-first-page'],
		queryFn: () => properties.listPage(portfolioId, { skip: 0, take: SELECTOR_PAGE_SIZE, sort: 'name' }),
	}));
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId, 'onboarding-first-page'],
		queryFn: () => tenants.listPage(portfolioId, { skip: 0, take: SELECTOR_PAGE_SIZE, sort: 'lastName' }),
	}));
	const leasesQuery = createQuery(() => ({
		queryKey: ['lease-managements', 'onboarding'],
		queryFn: () => leaseManagements.listPage({ take: 50 }),
	}));
	const myAlertsQuery = createQuery(() => ({
		queryKey: ['notification-settings', 'my-alerts'],
		enabled: portfolioId > 0,
		queryFn: () => notifications.myAlerts.get(),
	}));
	const gettingStartedSignalsQuery = createQuery(() => ({
		queryKey: ['getting-started', portfolioId],
		enabled: portfolioId > 0,
		queryFn: () => portfolios.gettingStarted(),
		staleTime: 60_000,
	}));

	async function loadOwnerOptions(params: { search?: string; skip: number; take: number }) {
		const result = await owners.listPage(portfolioId, { ...params, sort: 'name' });
		return {
			...result,
			items: result.items.map((owner) => ({ id: owner.id, label: ownerOptionLabel(owner) })),
		};
	}

	async function loadPropertyOptions(params: { search?: string; skip: number; take: number }) {
		const result = await properties.listPage(portfolioId, { ...params, sort: 'name' });
		return {
			...result,
			items: result.items.map((property) => ({
				id: property.id,
				label: property.name,
				description: `${property.addressLine1}, ${property.city}, ${property.state}`,
			})),
		};
	}

	async function loadTenantOptions(params: { search?: string; skip: number; take: number }) {
		const result = await tenants.listPage(portfolioId, { ...params, sort: 'lastName' });
		return {
			...result,
			items: result.items.map((tenant) => ({
				id: tenant.id,
				label: tenant.fullName || `${tenant.firstName} ${tenant.lastName}`,
				description: tenant.email || tenant.phone || null,
			})),
		};
	}

	// Sandbox/example-data state still matters for copy and progress framing, but it must not block
	// re-entering Guided Setup. Users need to test the setup/import flow before going live.
	const sandboxQuery = createQuery(() => ({
		queryKey: ['sandbox-state', portfolioId],
		enabled: portfolioId > 0,
		queryFn: () => portfolios.sandboxState(),
		staleTime: 60_000,
	}));

	const hasExistingOwners = $derived((ownersQuery.data?.totalCount ?? 0) > 0);
	const hasExistingProperties = $derived((propertiesQuery.data?.totalCount ?? 0) > 0);
	const hasExistingTenants = $derived((tenantsQuery.data?.totalCount ?? 0) > 0);
	const hasExistingLeases = $derived((leasesQuery.data?.totalCount ?? 0) > 0);
	// A step counts as "done" if the wizard handled it OR data already exists.
	const stepDone = $derived<Record<WizardStepKey, boolean>>({
		portfolio: portfolioSaved || !!portfolioQuery.data?.name,
		owner: !!createdOwner || hasExistingOwners,
		import: leaseImportCreatedSpine || createdLease || hasExistingLeases,
		property: leaseImportCreatedSpine || !!createdProperty || hasExistingProperties,
		tenants: leaseImportCreatedSpine || createdTenants.length > 0 || hasExistingTenants,
		lease: leaseImportCreatedSpine || createdLease || hasExistingLeases,
		notifications: isPersonalAlertSetupComplete(gettingStartedSignalsQuery.data?.hasNotificationEmail),
	});

	// A1: the real "set-up spine" — a property, a tenant, and a lease all exist. The wizard lets a user
	// Skip straight through, so we only show the "You're all set!" celebration when this is truly true;
	// otherwise we tell the truth ("You can finish anytime") and point back to what's left.
	const coreSpineComplete = $derived(stepDone.property && stepDone.tenants && stepDone.lease);

	// Celebrations (TSK-599) fire ONLY on a genuine, first-time milestone completion — the
	// user actually created records on a core step — never on a plain step advance or Skip,
	// and at most once per step. (Previously next() fired confetti on every forward click,
	// which is why it felt constant and disconnected from "I finished a phase".)
	const MILESTONE_STEPS: WizardStepKey[] = ['owner', 'import', 'property', 'tenants', 'lease'];
	const celebratedSteps = new Set<WizardStepKey>();

	// Lease-first import: the embedded LeaseFirstImport scanned a lease and created the whole
	// chain (property + unit + tenant + lease) in one transaction. Mark the lease done, refresh
	// the lists so later visits show the saved records, then move to the finished setup state.
	type LeaseImportCompleteResult = {
		leaseManagementId?: number | null;
		agreementId?: number | null;
		propertyId?: number | null;
		unitId?: number | null;
		tenantId?: number | null;
	};

	function handleLeaseImportComplete(result: LeaseImportCompleteResult) {
		if (!result.leaseManagementId || !result.agreementId) {
			showError('The scan finished, but no lease was created. Please review the draft or try again.');
			return;
		}
		leaseImportCreatedSpine = true;
		createdLease = true;
		showSuccess('Imported! We created the property, unit, tenant, and lease from your document.');
		queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['tenants', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['lease-managements'] });
		queryClient.invalidateQueries({ queryKey: ['owners', portfolioId] });
		bigFinale();
		finishFlow();
	}

	// ---------------------------------------------------------------------------
	// Initial positioning. Priority:
	//   1. ?step=<key> in the URL (deep-link from Settings / dashboard) — honored verbatim.
	//   2. otherwise resume the persisted step if it isn't already done.
	//   3. otherwise jump to the first incomplete CORE step.
	// Runs once after detection settles so manual back/next work afterward.
	// ---------------------------------------------------------------------------
	let autoAdvanced = $state(false);
	// Build the arrays before reducing them so every TanStack result property is read on the first
	// derived pass. A short-circuiting &&/|| chain leaves later query properties untracked; if those
	// requests settle before an earlier query, their proxy never notifies this component and setup
	// can remain on the detection screen until a manual refetch.
	const detectionReady = $derived(
		areOnboardingDetectionQueriesReady([
			portfolioQuery.isSuccess,
			ownersQuery.isSuccess,
			propertiesQuery.isSuccess,
			tenantsQuery.isSuccess,
			leasesQuery.isSuccess,
			gettingStartedSignalsQuery.isSuccess,
		])
	);
	const detectionFailed = $derived(
		hasOnboardingDetectionQueryFailed([
			portfolioQuery.isError,
			ownersQuery.isError,
			propertiesQuery.isError,
			tenantsQuery.isError,
			leasesQuery.isError,
			gettingStartedSignalsQuery.isError,
		])
	);
	function retryExistingDataDetection() {
		void portfolioQuery.refetch();
		void ownersQuery.refetch();
		void propertiesQuery.refetch();
		void tenantsQuery.refetch();
		void leasesQuery.refetch();
		void gettingStartedSignalsQuery.refetch();
	}
	$effect(() => {
		if (autoAdvanced || finished || !detectionReady) return;
		autoAdvanced = true;

		fromParam = page.url.searchParams.get('from');
		const resolved = resolveInitialOnboardingState({
			requested: page.url.searchParams.get('step') as WizardStepKey | null,
			persisted: readPersistedStep(),
			stepDone,
		});
		stepIndex = STEPS.findIndex((s) => s.key === resolved.stepKey);
		if (resolved.finished) finishFlow();
	});

	// Persist whenever the step changes (after the initial positioning has run).
	$effect(() => {
		if (autoAdvanced && !finished && currentStep) persistStep(currentStep.key);
	});

	// ---------------------------------------------------------------------------
	// Step: Portfolio
	// ---------------------------------------------------------------------------
	let portfolioForm = $state({ name: '', managementCompanyName: '', timeZone: guessBrowserTimeZone() });
	let portfolioErrors = $state<Record<string, string>>({});
	let portfolioPrefilled = false;

	$effect(() => {
		const p = portfolioQuery.data;
		if (p && !portfolioPrefilled) {
			portfolioPrefilled = true;
			portfolioForm.name = p.name ?? '';
			portfolioForm.managementCompanyName = p.managementCompanyName ?? '';
			if (p.timeZone) portfolioForm.timeZone = p.timeZone;
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
			timeZone: portfolioForm.timeZone || current?.timeZone || '',
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
			timeZone: result.data.timeZone,
		});
	}

	// ---------------------------------------------------------------------------
	// Step: Owner
	// ---------------------------------------------------------------------------
	const OWNER_ENTITY_TYPES: OwnerEntityType[] = ['Person', 'LLC', 'Trust'];
	let ownerForm = $state({ name: '', ownerEntityType: 'Person' as OwnerEntityType, email: '', taxId: '' });
	let ownerErrors = $state<Record<string, string>>({});
	let selectedOwnerId = $state(NEW_ONBOARDING_OWNER_VALUE);
	let selectedRemoteOwner = $state<Owner | null>(null);
	let ownerDeleteTarget = $state<Owner | null>(null);
	let ownerSelectionPrefilled = false;
	const ownerRecordOptions = $derived(onboardingOwnerRecordOptions({
		createdOwner,
		existingOwners: ownersQuery.data?.items ?? []
	}));

	function ownerById(id: string): Owner | null {
		if (selectedRemoteOwner && String(selectedRemoteOwner.id) === id) return selectedRemoteOwner;
		return (ownerRecordOptions.find((owner) => String(owner.id) === id) as Owner | undefined) ?? null;
	}

	function ownerOptionLabel(owner: { name?: string | null }): string {
		return owner.name?.trim() || 'Unnamed owner';
	}

	function ownerIdFromSelection(): number | null {
		if (selectedOwnerId === NEW_ONBOARDING_OWNER_VALUE) return null;
		const id = Number(selectedOwnerId);
		return Number.isFinite(id) && id > 0 ? id : null;
	}

	function selectOwnerRecord(value: string | undefined) {
		selectedOwnerId = value || NEW_ONBOARDING_OWNER_VALUE;
		ownerErrors = {};
		if (selectedOwnerId === NEW_ONBOARDING_OWNER_VALUE) {
			ownerForm = { name: '', ownerEntityType: 'Person', email: '', taxId: '' };
			return;
		}
		const owner = ownerById(selectedOwnerId);
		if (owner) {
			ownerForm = onboardingOwnerFormFromOwner(owner);
		}
	}

	async function selectRemoteOwnerRecord(value: string, option: { label: string } | null) {
		if (!value) {
			selectedRemoteOwner = null;
			selectOwnerRecord(NEW_ONBOARDING_OWNER_VALUE);
			return;
		}
		selectedOwnerId = value;
		const owner = await owners.get(Number(value));
		selectedRemoteOwner = owner;
		ownerForm = onboardingOwnerFormFromOwner(owner);
		if (option?.label && !owner.name) ownerForm.name = option.label;
	}

	const ownerSelectionLabel = $derived.by(() => {
		if (selectedOwnerId === NEW_ONBOARDING_OWNER_VALUE) {
			return ownerRecordOptions.length > 0 ? 'Add a new owner' : 'Add first owner';
		}
		return ownerById(selectedOwnerId)?.name ?? 'Choose owner';
	});
	const selectedOwnerRecord = $derived.by(() =>
		selectedOwnerId === NEW_ONBOARDING_OWNER_VALUE ? null : ownerById(selectedOwnerId)
	);
	function ownerAssignedPropertyCount(owner: Owner | null): number {
		return owner?.assignedPropertyCount ?? 0;
	}
	const ownerDeleteAssignedCount = $derived.by(() =>
		ownerAssignedPropertyCount(ownerDeleteTarget)
	);

	$effect(() => {
		if (ownerSelectionPrefilled) return;
		const owner = createdOwner ?? ownerRecordOptions[0] ?? null;
		if (!owner) return;
		ownerSelectionPrefilled = true;
		selectedOwnerId = String(owner.id);
		ownerForm = onboardingOwnerFormFromOwner(owner);
	});

	// TSK-209: the landlord IS the first owner, so confirm-don't-retype — pre-fill name + email from
	// their account. Editable (they might own through an LLC). Only when they don't already have an owner
	// on file, and only once, so we never clobber what they're typing. `$state` because the template
	// reads it (to show the "filled from your account" hint).
	let ownerPrefilled = $state(false);
	$effect(() => {
		if (ownerPrefilled) return;
		if (hasExistingOwners) return; // a self-owner / existing owner already exists — adding another
		const me = getCurrentUser();
		if (!me) return;
		ownerPrefilled = true;
		if (!ownerForm.name.trim() && me.displayName?.trim()) ownerForm.name = me.displayName.trim();
		if (!ownerForm.email.trim() && me.email?.trim()) ownerForm.email = me.email.trim();
	});

	type SaveOwnerVariables = {
		ownerId: number | null;
		data: Record<string, unknown>;
	};

	const saveOwnerMutation = createMutation<Owner, Error, SaveOwnerVariables>(() => ({
		mutationFn: ({ ownerId, data }: SaveOwnerVariables) =>
			ownerId == null ? owners.create(data) : owners.update(ownerId, data),
		onSuccess: (owner, vars) => {
			if (vars.ownerId == null) {
				createdOwner = owner;
			}
			selectedOwnerId = String(owner.id);
			if (selectedPropertyId === NEW_ONBOARDING_PROPERTY_VALUE && !propertyForm.ownerEntityId) {
				propertyForm.ownerEntityId = String(owner.id);
			}
			ownerForm = onboardingOwnerFormFromOwner(owner);
			showSuccess(vars.ownerId == null ? 'Owner added.' : 'Owner saved.');
			queryClient.invalidateQueries({ queryKey: ['owners', portfolioId] });
			next();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));
	type DeleteOwnerVariables = {
		id: number;
	};
	const deleteOwnerMutation = createMutation<void, Error, DeleteOwnerVariables>(() => ({
		mutationFn: ({ id }) => owners.delete(id),
		onSuccess: (_result, vars) => {
			const { id } = vars;
			showSuccess('Owner deleted.');
			ownerDeleteTarget = null;
			if (createdOwner?.id === id) createdOwner = null;
			if (selectedOwnerId === String(id)) selectOwnerRecord(NEW_ONBOARDING_OWNER_VALUE);
			if (propertyForm.ownerEntityId === String(id)) propertyForm.ownerEntityId = '';
			queryClient.invalidateQueries({ queryKey: ['owners', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
		},
		onError: (err) => {
			ownerDeleteTarget = null;
			showError(apiErrorMessage(err, 'Owner could not be deleted.'));
		},
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
		saveOwnerMutation.mutate({
			ownerId: ownerIdFromSelection(),
			data: { portfolioId, ...result.data }
		});
	}

	// ---------------------------------------------------------------------------
	// Step: Property + Units. Split into sub-steps so the user sees 1–2 fields at a
	// time instead of one dense form: address → details → units.
	// ---------------------------------------------------------------------------
	const PROPERTY_SUBSTEPS = ['address', 'units'] as const;
	type PropertySubstep = (typeof PROPERTY_SUBSTEPS)[number];
	let propertySub = $state<PropertySubstep>('address');
	let propertyForm = $state({
		name: '',
		type: 'SingleFamily',
		rentalStructure: '' as RentalStructure | '',
		addressLine1: '',
		addressLine2: '',
		city: '',
		state: '',
		postalCode: '',
		ownerEntityId: '',
	});
	let propertyErrors = $state<Record<string, string>>({});
	let selectedPropertyId = $state(NEW_ONBOARDING_PROPERTY_VALUE);
	let selectedRemoteProperty = $state<Property | null>(null);
	let propertyDeleteTarget = $state<Property | null>(null);
	let unitDeleteTarget = $state<Unit | null>(null);
	let propertySelectionPrefilled = false;
	let propertySelectionTouched = false;
	const propertyRecordOptions = $derived(onboardingPropertyRecordOptions({
		createdProperty,
		existingProperties: propertiesQuery.data?.items ?? []
	}));

	function propertyById(id: string): Property | null {
		if (selectedRemoteProperty && String(selectedRemoteProperty.id) === id) return selectedRemoteProperty;
		return (propertyRecordOptions.find((property) => String(property.id) === id) as Property | undefined) ?? null;
	}

	function propertyOptionLabel(property: { name?: string | null }): string {
		return property.name?.trim() || 'Unnamed property';
	}

	function propertyIdFromSelection(): number | null {
		if (selectedPropertyId === NEW_ONBOARDING_PROPERTY_VALUE) return null;
		const id = Number(selectedPropertyId);
		return Number.isFinite(id) && id > 0 ? id : null;
	}

	function defaultPropertyOwnerEntityId(): string {
		return ownerEntityIdForOnboarding({
			selectedOwnerId,
			createdOwner,
			existingOwners: ownerRecordOptions
		});
	}

	function selectPropertyRecord(value: string | undefined) {
		propertySelectionTouched = true;
		selectedPropertyId = value || NEW_ONBOARDING_PROPERTY_VALUE;
		propertyErrors = {};
		if (selectedPropertyId === NEW_ONBOARDING_PROPERTY_VALUE) {
			propertyForm = {
				name: '',
				type: 'SingleFamily',
				rentalStructure: '',
				addressLine1: '',
				addressLine2: '',
				city: '',
				state: '',
				postalCode: '',
				ownerEntityId: defaultPropertyOwnerEntityId(),
			};
			unitRows = [emptyUnit()];
			unitRowErrors = [{}];
			propertySub = 'address';
			return;
		}
		const property = propertyById(selectedPropertyId);
		if (property) {
			propertyForm = onboardingPropertyFormFromProperty(property);
			unitRows = [emptyUnit()];
			unitRowErrors = [{}];
			propertySub = 'address';
		}
	}

	async function selectRemotePropertyRecord(value: string) {
		propertySelectionTouched = true;
		if (!value) {
			selectedRemoteProperty = null;
			selectPropertyRecord(NEW_ONBOARDING_PROPERTY_VALUE);
			return;
		}
		selectedPropertyId = value;
		const property = await properties.get(Number(value));
		selectedRemoteProperty = property;
		propertyForm = onboardingPropertyFormFromProperty(property);
		unitRows = [emptyUnit()];
		unitRowErrors = [{}];
		propertySub = 'address';
	}

	const propertySelectionLabel = $derived.by(() => {
		if (selectedPropertyId === NEW_ONBOARDING_PROPERTY_VALUE) {
			return propertyRecordOptions.length > 0 ? 'Add a new property' : 'Add first property';
		}
		return propertyById(selectedPropertyId)?.name ?? 'Choose property';
	});
	const selectedPropertyRecord = $derived.by(() =>
		selectedPropertyId === NEW_ONBOARDING_PROPERTY_VALUE ? null : propertyById(selectedPropertyId)
	);

	$effect(() => {
		if (propertySelectionPrefilled) return;
		const property = onboardingPropertyPrefillCandidate({
			createdProperty,
			propertyRecordOptions,
			selectionTouched: propertySelectionTouched
		});
		if (!property) return;
		propertySelectionPrefilled = true;
		selectedPropertyId = String(property.id);
		propertyForm = onboardingPropertyFormFromProperty(property);
	});

	const selectedPropertyUnitsQuery = createQuery(() => {
		const propertyId = propertyIdFromSelection();
		return {
			queryKey: ['onboarding-property-units', propertyId],
			enabled: propertyId != null && propertyId > 0,
			queryFn: () => properties.listUnits(propertyId!),
		};
	});

	const selectedPropertyUnits = $derived(selectedPropertyUnitsQuery.data ?? []);
	let propertyOwnerDefaulted = false;

	$effect(() => {
		if (propertyOwnerDefaulted || currentStep.key !== 'property') return;
		if (selectedPropertyId !== NEW_ONBOARDING_PROPERTY_VALUE || propertyForm.ownerEntityId) return;
		const defaultOwnerId = defaultPropertyOwnerEntityId();
		if (!defaultOwnerId) return;
		propertyOwnerDefaulted = true;
		propertyForm.ownerEntityId = defaultOwnerId;
	});

	function unitSummary(unit: Unit): string {
		const compactNumber = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 });
		const rent = formatAccountingCurrency(unit.marketRent ?? 0, 'USD', true);
		return `${compactNumber.format(unit.bedrooms ?? 0)} bd · ${compactNumber.format(unit.bathrooms ?? 0)} ba · ${rent}/mo`;
	}

	const emptyUnit = () => ({ unitNumber: '', floorPlan: '', bedrooms: '', bathrooms: '', squareFeet: '', marketRent: '', notes: '' });
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

	// RentalStructure is an explicit persisted choice. Property type does not imply whether an
	// address has one rentable space or several (for example, a mixed-use address may have either).
	const isSingleRental = $derived(propertyForm.rentalStructure === 'SingleRental');
	const isResidentialUnit = $derived(isResidentialDwellingType(propertyForm.type));
	let unitCount = $state('');

	function unitsAreDefaultEmpty() {
		return (
			unitRows.length === 1 &&
			!unitRows[0].unitNumber &&
			!unitRows[0].floorPlan &&
			!unitRows[0].bedrooms &&
			!unitRows[0].bathrooms &&
			!unitRows[0].squareFeet &&
			!unitRows[0].marketRent &&
			!unitRows[0].notes
		);
	}
	// A single rental still gets one canonical Unit underneath, but the setup copy presents the
	// address as one rental rather than asking the landlord to understand duplicate-looking records.
	function seedUnitsForStructure() {
		if (!unitsAreDefaultEmpty()) return; // never clobber units the user already entered
		if (isSingleRental && propertyIdFromSelection() == null) {
			unitRows = [
				{ unitNumber: propertyForm.name.trim() || '1', floorPlan: '', bedrooms: '1', bathrooms: '1', squareFeet: '', marketRent: '0', notes: '' },
			];
			unitRowErrors = [{}];
		}
	}
	// Multi-unit: generate N blank rows (numbered 1..N) from the count the user entered.
	function generateUnitRows() {
		const n = Math.max(1, Math.min(50, parseInt(unitCount, 10) || 0));
		unitRows = Array.from({ length: n }, (_, i) => ({
			unitNumber: String(i + 1),
			floorPlan: '',
			bedrooms: '',
			bathrooms: '',
			squareFeet: '',
			marketRent: '',
			notes: '',
		}));
		unitRowErrors = unitRows.map(() => ({}));
	}

	type SavePropertyVariables = {
		propertyId: number | null;
		property: Record<string, unknown> & { rentalStructure: RentalStructure };
		units: Record<string, unknown>[];
	};

	const savePropertyMutation = createMutation<
		{ property: Property; units: Unit[]; updated: boolean },
		Error,
		SavePropertyVariables
	>(() => ({
		mutationFn: (vars: SavePropertyVariables) => properties.setup({
			...(vars.propertyId == null ? {} : { propertyId: vars.propertyId }),
			property: vars.property,
			units: vars.units
		}),
		onSuccess: ({ property, units, updated }) => {
			createdProperty = property;
			createdUnits = units;
			selectedPropertyId = String(property.id);
			propertyForm = onboardingPropertyFormFromProperty(property);
			showSuccess(
				property.rentalStructure === 'SingleRental'
					? updated ? 'Rental saved.' : 'Rental added.'
					: units.length > 0
					? `Property and ${units.length} unit${units.length === 1 ? '' : 's'} added.`
					: updated
						? 'Property saved.'
						: 'Property added.'
			);
			queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['units'] });
			queryClient.invalidateQueries({ queryKey: ['onboarding-property-units', property.id] });
			propertySub = 'address';
			next();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));
	const deletePropertyMutation = createMutation(() => ({
		mutationFn: (id: number) => properties.delete(id),
		onSuccess: (_result, id) => {
			showSuccess('Property deleted.');
			propertyDeleteTarget = null;
			if (createdProperty?.id === id) {
				createdProperty = null;
				createdUnits = [];
			}
			if (selectedPropertyId === String(id)) selectPropertyRecord(NEW_ONBOARDING_PROPERTY_VALUE);
			queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['units'] });
			queryClient.invalidateQueries({ queryKey: ['dashboard', portfolioId] });
		},
		onError: (err) => {
			propertyDeleteTarget = null;
			showError(apiErrorMessage(err, 'Property could not be deleted.'));
		},
	}));
	const deleteUnitMutation = createMutation(() => ({
		mutationFn: (id: number) => properties.deleteUnit(id),
		onSuccess: (_result, id) => {
			showSuccess('Unit deleted.');
			unitDeleteTarget = null;
			createdUnits = createdUnits.filter((unit) => unit.id !== id);
			queryClient.invalidateQueries({ queryKey: ['onboarding-property-units', propertyIdFromSelection()] });
			queryClient.invalidateQueries({ queryKey: ['properties', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['units'] });
			queryClient.invalidateQueries({ queryKey: ['dashboard', portfolioId] });
		},
		onError: (err) => {
			unitDeleteTarget = null;
			showError(apiErrorMessage(err, 'Unit could not be deleted.'));
		},
	}));

	// Validate just the address fields before advancing the property sub-step.
	function propertyAddressValid(): boolean {
		const errs: Record<string, string> = {};
		if (!propertyForm.rentalStructure) errs.rentalStructure = 'Choose one rental or multiple rentals';
		if (!propertyForm.name.trim()) errs.name = 'Name is required';
		if (!propertyForm.addressLine1.trim()) errs.addressLine1 = 'Address is required';
		if (!propertyForm.city.trim()) errs.city = 'City is required';
		if (!propertyForm.state.trim()) errs.state = 'State is required';
		if (!propertyForm.postalCode.trim()) errs.postalCode = 'ZIP is required';
		propertyErrors = errs;
		return Object.keys(errs).length === 0;
	}

	function submitProperty() {
		const rentalStructure = propertyForm.rentalStructure;
		if (!rentalStructure) {
			propertyErrors = { ...propertyErrors, rentalStructure: 'Choose one rental or multiple rentals' };
			propertySub = 'address';
			return;
		}
		const propertyPayload = buildOnboardingPropertyPayload({
			propertyForm,
			selectedOwnerId,
			createdOwner,
			existingOwners: ownerRecordOptions
		});
		const propResult = parseForm(
			propertySchema,
			propertyPayload
		);
		if (propResult.errors) {
			propertyErrors = propResult.errors;
			propertySub = 'address';
			return;
		}
		propertyErrors = {};
		const { ownerEntityId: _ownerSelectionField, ...validatedProperty } = propResult.data;

		const errors: Record<string, string>[] = unitRows.map(() => ({}));
		const validUnits: Record<string, unknown>[] = [];
		let hasUnitError = false;
		unitRows.forEach((row, i) => {
			const blank = !row.unitNumber.trim() && !row.floorPlan.trim() && !row.bedrooms.trim() && !row.bathrooms.trim() && !row.squareFeet.trim() && !row.marketRent.trim() && !row.notes.trim();
			if (blank) return;
			const res = parseForm(unitSchemaForPropertyType(propertyForm.type), row);
			if (res.errors) {
				errors[i] = res.errors;
				hasUnitError = true;
			} else {
				validUnits.push(res.data);
			}
		});
		if (propertyIdFromSelection() == null && validUnits.length === 0) {
			errors[0] = { ...errors[0], unitNumber: 'Add the details for at least one rental' };
			hasUnitError = true;
		}
		unitRowErrors = errors;
		if (hasUnitError) return;

		savePropertyMutation.mutate({
			propertyId: propertyIdFromSelection(),
			property: {
				portfolioId,
				...validatedProperty,
				rentalStructure,
				ownerships: propertyPayload.ownerships,
				clearOwnership: propertyPayload.clearOwnership
			},
			units: validUnits
		});
	}

	// ---------------------------------------------------------------------------
	// Step: Tenants
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
		mutationFn: (rows: Record<string, unknown>[]) => tenants.createGuidedSetupBatch(rows),
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
				valid.push(res.data);
			}
		});
		tenantRowErrors = errors;
		if (hasError) return;
		if (valid.length === 0) {
			next();
			return;
		}
		saveTenantsMutation.mutate(valid);
	}

	// ---------------------------------------------------------------------------
	// Step: Lease (with snap-a-photo pre-fill)
	// ---------------------------------------------------------------------------
	const today = new Date();
	const oneYear = new Date(today);
	oneYear.setFullYear(oneYear.getFullYear() + 1);

	let leaseForm = $state({
		tenantId: '',
		propertyId: '',
		unitId: '',
		startDate: businessDateOrToday(undefined, today),
		endDate: businessDateOrToday(undefined, oneYear),
		monthlyRent: '',
		securityDeposit: '',
		lateFeeAmount: '0',
		leaseNumber: defaultLeaseNumber(today),
		rentDueDay: '1',
	});
	let leaseEndDateAutoDefault = $state(leaseForm.endDate);
	let leaseReviewDisposition = $state<LeaseScanReviewDisposition | ''>('');
	let leaseDocumentTemplateId = $state('');
	let selectedLeasePropertyLabel = $state('');
	let selectedLeaseUnitLabel = $state('');
	let selectedLeaseTenantLabel = $state('');
	// Pre-fill security deposit with monthly rent (common default) once — stays
	// editable; if the user clears it we do not re-fill.
	let securityDepositDefaulted = $state(false);
	$effect(() => {
		if (!securityDepositDefaulted && leaseForm.monthlyRent && !leaseForm.securityDeposit) {
			securityDepositDefaulted = true;
			leaseForm.securityDeposit = leaseForm.monthlyRent;
		}
	});
	let leaseErrors = $state<Record<string, string>>({});
	let leasePrefilled = false;
	let leasePrefillDraftId = $state<number | null>(null);
	const leaseSignatureChoiceInvalid = $derived(
		leasePrefillDraftId != null &&
		!leaseReviewDisposition
	);

	async function loadLeaseUnitOptions(params: { search?: string; skip: number; take: number }) {
		if (!leaseForm.propertyId) return { items: [], totalCount: 0, skip: params.skip, take: params.take };
		const result = await units.listWithHealthPage({
			...params,
			propertyId: Number(leaseForm.propertyId),
			sort: 'unitNumber',
		});
		return {
			...result,
			items: result.items.map((unit) => ({
				id: unit.id,
				label: `Unit ${unit.unitNumber}`,
				description: unit.status,
			})),
		};
	}

	const leaseProperties = $derived(propertiesQuery.data?.items ?? []);
	const leaseTenants = $derived(tenantsQuery.data?.items ?? []);

	$effect(() => {
		if (leasePrefilled) return;
		if (currentStep.key !== 'lease') return;
		const propId = createdProperty?.id ?? leaseProperties[0]?.id;
		const tenant = createdTenants[0] ?? leaseTenants[0];
		if (propId == null && tenant == null) return;
		leasePrefilled = true;
		if (propId != null) {
			leaseForm.propertyId = String(propId);
			selectedLeasePropertyLabel =
				createdProperty?.id === propId ? createdProperty.name : (leaseProperties.find((property) => property.id === propId)?.name ?? '');
		}
		if (tenant != null) {
			leaseForm.tenantId = String(tenant.id);
			selectedLeaseTenantLabel = tenant.fullName || `${tenant.firstName} ${tenant.lastName}`;
		}
	});

	$effect(() => {
		const preferred = createdUnits.find((unit) => unit.propertyId === Number(leaseForm.propertyId));
		if (!leaseForm.unitId && preferred) {
			leaseForm.unitId = String(preferred.id);
			selectedLeaseUnitLabel = `Unit ${preferred.unitNumber}`;
			if (!leaseForm.monthlyRent && preferred.marketRent != null) leaseForm.monthlyRent = String(preferred.marketRent);
		}
	});
	function handleLeaseStartDateChange(iso: string) {
		leaseForm.startDate = iso;
		if (!iso) return;
		const defaultEndDate = addCalendarYear(iso);
		if (!defaultEndDate) return;
		if (!leaseForm.endDate || leaseForm.endDate === leaseEndDateAutoDefault) {
			leaseForm.endDate = defaultEndDate;
			leaseEndDateAutoDefault = defaultEndDate;
		}
	}

	function handleLeaseEndDateChange(iso: string) {
		leaseForm.endDate = iso;
		if (iso !== leaseEndDateAutoDefault) leaseEndDateAutoDefault = '';
	}

	// Apply photo-extracted (and user-confirmed) lease terms onto the form. Never overwrites a value
	// the user already typed — the confirmed extraction fills blanks, the user stays in control.
	function applyLeasePrefill(values: {
		draftId: number;
		leaseNumber?: string;
		startDate?: string;
		endDate?: string;
		monthlyRent?: string;
		securityDeposit?: string;
		lateFee?: string;
		rentDueDay?: string;
	}) {
		leasePrefillDraftId = values.draftId;
		if (values.leaseNumber) leaseForm.leaseNumber = values.leaseNumber;
		if (values.startDate) handleLeaseStartDateChange(values.startDate);
		if (values.endDate) {
			leaseForm.endDate = values.endDate;
			leaseEndDateAutoDefault = '';
		}
		if (values.monthlyRent) leaseForm.monthlyRent = values.monthlyRent;
		if (values.securityDeposit) leaseForm.securityDeposit = values.securityDeposit;
		if (values.lateFee) leaseForm.lateFeeAmount = values.lateFee;
		if (values.rentDueDay) leaseForm.rentDueDay = values.rentDueDay;
		showSuccess('Filled in from your lease — please double-check the values.');
	}

	type LeaseSubmitPayload = {
		data: OnboardingLeaseConfirmInput & {
			portfolioId: number;
			status: string;
			notes?: string | null;
			moveInDate?: string | null;
		};
		prefillDraftId: number | null;
	};

	const saveLeaseMutation = createMutation<unknown, Error, LeaseSubmitPayload>(() => ({
		mutationFn: async ({ data, prefillDraftId }: LeaseSubmitPayload): Promise<unknown> => {
			return leaseSubmission.submit(data, prefillDraftId);
		},
		onSuccess: () => {
			createdLease = true;
			leasePrefillDraftId = null;
			leaseSubmission.clear();
			showSuccess('Lease created.');
			queryClient.invalidateQueries({ queryKey: ['lease-managements'] });
			queryClient.invalidateQueries({ queryKey: ['properties'] });
			queryClient.invalidateQueries({ queryKey: ['tenants'] });
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			next();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitLease() {
		const leaseNumber =
			leaseForm.leaseNumber.trim() || defaultLeaseNumber(new Date(leaseForm.startDate || Date.now()));
		const result = parseForm(leaseSchema, {
			leaseNumber,
			propertyId: leaseForm.propertyId,
			unitId: leaseForm.unitId,
			tenantId: leaseForm.tenantId,
			startDate: leaseForm.startDate,
			endDate: leaseForm.endDate,
			monthlyRent: leaseForm.monthlyRent,
			securityDeposit: leaseForm.securityDeposit || '0',
			lateFeeAmount: leaseForm.lateFeeAmount || '0',
			rentDueDay: leaseForm.rentDueDay,
			status: 'Active',
			notes: '',
		});
		if (result.errors) {
			leaseErrors = result.errors;
			return;
		}
		leaseErrors = {};
		saveLeaseMutation.mutate({
			data: {
				portfolioId,
				...result.data,
				reviewDisposition: leaseReviewDisposition as LeaseScanReviewDisposition,
				documentTemplateId:
					leaseReviewDisposition === 'NeedsSignatures' && leaseDocumentTemplateId
						? Number(leaseDocumentTemplateId)
						: null
			},
			prefillDraftId: leasePrefillDraftId
		});
	}

	// ---------------------------------------------------------------------------
	// Step: My alerts — writes the same canonical per-user endpoint as Settings.
	// ---------------------------------------------------------------------------
	let alertForm = $state({
		enableInApp: true,
		enableMobilePush: true,
		enableEmail: true,
		enableSms: false,
	});
	let alertFormPrefilled = false;
	$effect(() => {
		const saved = myAlertsQuery.data;
		if (saved && !alertFormPrefilled) {
			alertFormPrefilled = true;
			alertForm = {
				enableInApp: saved.enableInApp,
				enableMobilePush: saved.enableMobilePush,
				enableEmail: saved.enableEmail,
				enableSms: saved.enableSms,
			};
		}
	});
	const saveMyAlertsMutation = createMutation(() => ({
		mutationFn: () => notifications.myAlerts.update(alertForm),
		onSuccess: async () => {
			showSuccess('Your alert preferences were saved.');
			await queryClient.invalidateQueries({ queryKey: ['notification-settings', 'my-alerts'] });
			await queryClient.invalidateQueries({ queryKey: ['getting-started', portfolioId] });
			await Promise.all([myAlertsQuery.refetch(), gettingStartedSignalsQuery.refetch()]);
			next();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ---------------------------------------------------------------------------
	// Navigation
	// ---------------------------------------------------------------------------
	function finishFlow() {
		finished = true;
		returnToFinishAfterOptional = false;
		if (browser && portfolioId > 0) {
			try {
				localStorage.removeItem(progressKey);
			} catch {
				/* ignore */
			}
		}
	}

	function next({ celebrate = true }: { celebrate?: boolean } = {}) {
		// Deep-linked from a Settings section → return there once the step is handled.
		if (fromParam === 'settings' && currentStep.settingsAnchor) {
			goto(`/settings#${currentStep.settingsAnchor}`);
			return;
		}
		// Did the user just finish a real milestone (records created on a core step) for the
		// first time? Only then do we reward — once per step, and never via Skip.
		const reachedMilestone =
			celebrate &&
			MILESTONE_STEPS.includes(currentStep.key) &&
			stepDone[currentStep.key] &&
			!celebratedSteps.has(currentStep.key);
		if (reachedMilestone) celebratedSteps.add(currentStep.key);

		// The last CORE step (lease) is the natural "done" point — show the finale and offer the
		// optional personal-alert step from there rather than forcing the user through it.
		if (currentStep.key === 'lease') {
			bigFinale();
			finishFlow();
			return;
		}
		if (shouldFinishAfterOptionalStep({
			currentStepKey: currentStep.key,
			returnToFinishAfterOptional,
		})) {
			bigFinale();
			finishFlow();
			return;
		}
		if (stepIndex < STEPS.length - 1) {
			if (reachedMilestone) celebrateMilestone();
			stepIndex += 1;
			syncStepUrl(STEPS[stepIndex].key);
		} else {
			bigFinale();
			finishFlow();
		}
	}
	function back() {
		if (currentStep.key === 'property' && propertySub !== 'address') {
			propertySub = PROPERTY_SUBSTEPS[PROPERTY_SUBSTEPS.indexOf(propertySub) - 1];
			return;
		}
		if (stepIndex > 0) {
			// When stepping back into the property step from a later step, always
			// land on the address sub-step so Back reads as exactly one step.
			if (STEPS[stepIndex - 1]?.key === 'property') {
				propertySub = 'address';
			}
			stepIndex -= 1;
			syncStepUrl(STEPS[stepIndex].key);
		}
	}
	function skip() {
		next({ celebrate: false });
	}
	function syncStepUrl(key: WizardStepKey) {
		if (!browser || page.url.searchParams.get('step') === key) return;
		const url = new URL(page.url);
		url.searchParams.set('step', key);
		void goto(`${url.pathname}${url.search}`, { replaceState: true, keepFocus: true, noScroll: true });
	}
	function goToStep(key: WizardStepKey) {
		stepIndex = STEPS.findIndex((s) => s.key === key);
		syncStepUrl(key);
	}
	function openOptionalStepFromFinished(key: WizardStepKey) {
		returnToFinishAfterOptional = true;
		finished = false;
		goToStep(key);
	}
	function openImportCenterFromFinished() {
		returnToFinishAfterOptional = false;
		finished = false;
		// Resume at the first incomplete core step (same fallback resolveInitialOnboardingState
		// uses), or 'property' if everything core is already done — there's always more to add.
		const next = CORE_WIZARD_STEPS.find((s) => !stepDone[s.key]);
		goToStep(next?.key ?? 'property');
	}

	const anyPending = $derived(
		savePortfolioMutation.isPending ||
			saveOwnerMutation.isPending ||
			deleteOwnerMutation.isPending ||
			savePropertyMutation.isPending ||
			deletePropertyMutation.isPending ||
			deleteUnitMutation.isPending ||
			saveTenantsMutation.isPending ||
			saveLeaseMutation.isPending ||
			saveMyAlertsMutation.isPending
	);

	const coreCount = $derived(CORE_WIZARD_STEPS.length);
	const leaseBlocked = $derived(leaseProperties.length === 0 || leaseTenants.length === 0);

</script>

<svelte:head>
	<title>Set up your portfolio - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto bg-muted/30 p-6 pb-20" data-testid="onboarding-page">
	<div class="mx-auto max-w-5xl">
		{#if detectionFailed}
			<Card.Root class="mt-10" data-testid="onboarding-detection-error">
				<Card.Content class="flex flex-col items-center gap-4 px-8 py-12 text-center">
					<h1 class="text-2xl font-bold">We couldn't load your setup</h1>
					<p class="max-w-md text-sm text-muted-foreground">
						Your existing portfolio has not been changed. Try loading it again before continuing setup.
					</p>
					<Button variant="outline" data-testid="onboarding-detection-retry" onclick={retryExistingDataDetection}>
						Try again
					</Button>
				</Card.Content>
			</Card.Root>
		{:else if !detectionReady || !autoAdvanced}
			<Card.Root class="mt-10" data-testid="onboarding-detection-loading">
				<Card.Content class="flex flex-col items-center gap-3 px-8 py-12 text-center">
					<h1 class="text-xl font-semibold">Checking your existing setup…</h1>
					<p class="max-w-md text-sm text-muted-foreground">
						We're finding the portfolio, properties, people, and leases you've already added.
					</p>
				</Card.Content>
			</Card.Root>
		{:else if finished}
			<Card.Root class="mt-10" data-testid="onboarding-complete">
				<Card.Content class="flex flex-col items-center gap-4 px-8 py-12 text-center">
					{#if coreSpineComplete}
						<!-- A1: genuine completion — a property, tenant, and lease all exist. -->
						<div class="flex h-16 w-16 items-center justify-center rounded-full bg-success/15 text-success">
							<PartyPopper class="h-8 w-8" />
						</div>
						<h1 class="text-2xl font-bold" data-testid="onboarding-complete-title">You're all set!</h1>
						<p class="max-w-md text-sm text-muted-foreground">
							Your portfolio is ready to go. You can always add more properties, tenants, and leases
							from the menu on the left.
						</p>
					{:else}
						<!-- A1: the user skipped one or more essentials — don't claim they're done. -->
						<div class="flex h-16 w-16 items-center justify-center rounded-full bg-primary/10 text-primary">
							<ListChecks class="h-8 w-8" />
						</div>
						<h1 class="text-2xl font-bold" data-testid="onboarding-complete-title">You can finish anytime</h1>
						<p class="max-w-md text-sm text-muted-foreground">
							You skipped a few essentials. Add a property, a tenant, and a lease whenever you're
							ready — your getting-started checklist keeps track of what's left.
						</p>
						<div class="mt-1 flex flex-col items-stretch gap-2 sm:flex-row">
							{#if !stepDone.property}
								<Button variant="outline" class="gap-2" data-testid="onboarding-finish-property" onclick={() => { finished = false; goToStep('property'); }}>
									<Home class="h-4 w-4" />
									Add a property
								</Button>
							{:else if !stepDone.tenants}
								<Button variant="outline" class="gap-2" data-testid="onboarding-finish-tenants" onclick={() => { finished = false; goToStep('tenants'); }}>
									<Users class="h-4 w-4" />
									Add a tenant
								</Button>
							{:else if !stepDone.lease}
								<Button variant="outline" class="gap-2" data-testid="onboarding-finish-lease" onclick={() => { finished = false; goToStep('lease'); }}>
									<FileText class="h-4 w-4" />
									Create a lease
								</Button>
							{/if}
							<Button variant="outline" class="gap-2" data-testid="onboarding-finish-checklist" onclick={() => goto('/get-started?view=checklist')}>
								<ListChecks class="h-4 w-4" />
								See my checklist
							</Button>
						</div>
					{/if}
					<!-- Optional add-ons the user can still set up, clearly marked optional. -->
					<div class="mt-2 flex flex-col items-stretch gap-2 sm:flex-row">
						<Button variant="outline" class="gap-2" data-testid="onboarding-return-import" onclick={openImportCenterFromFinished}>
							<FileSpreadsheet class="h-4 w-4" />
							Import more records
						</Button>
						<Button variant="outline" class="gap-2" href="/deposits" data-testid="onboarding-setup-deposits">
							<PiggyBank class="h-4 w-4" />
							Add security deposits
						</Button>
						{#if !stepDone.notifications}
							<Button variant="outline" class="gap-2" data-testid="onboarding-setup-notifications" onclick={() => openOptionalStepFromFinished('notifications')}>
								<Bell class="h-4 w-4" />
								Configure my alerts
							</Button>
						{/if}
					</div>
					<Button class="mt-2 gap-2" data-testid="onboarding-finish-dashboard" onclick={() => goto('/')}>
						<Sparkles class="h-4 w-4" />
						Go to my dashboard
					</Button>
				</Card.Content>
			</Card.Root>
		{:else}
			<div class="mb-6">
				<h1 class="text-2xl font-bold" data-testid="onboarding-title">Let's set up your portfolio</h1>
				<p class="mt-1 text-sm text-muted-foreground">
					A few quick steps to go from empty to up-and-running. The computer does the typing — you
					just confirm.
				</p>
			</div>

			<!-- Step indicator (TSK-602): one progress source, grouped into sections so it reads as
			     "where am I" instead of 8 squeezed circles. Replaces the old dual stepper + "Guided
			     import" phase rail (same progress, shown twice, two different visual languages). -->
			<div class="mb-5" data-testid="onboarding-steps">
				<div class="mb-3 flex items-center gap-3">
					<Progress value={Math.min(stepIndex + 1, coreCount)} max={coreCount} class="flex-1" />
					<span class="shrink-0 text-xs font-medium tabular-nums text-muted-foreground">
						Step {Math.min(stepIndex + 1, coreCount)} of {coreCount}
					</span>
				</div>
				<div class="flex flex-wrap gap-2">
					{#each STEP_GROUPS as group (group.label)}
						{@const groupSteps = group.keys.map((k) => STEPS.find((s) => s.key === k)).filter((s) => !!s)}
						{@const groupOptional = groupSteps.every((s) => !s.core)}
						{@const groupActive = groupSteps.some((s) => s.key === currentStep.key)}
						<div
							class="flex items-center gap-2 rounded-lg border px-2.5 py-2 transition-colors {groupActive ? 'border-primary/40 bg-primary/5' : 'border-border bg-muted/20'}"
							data-testid="onboarding-step-group-{group.label.toLowerCase()}"
						>
							<span class="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
								{group.label}{#if groupOptional}{' '}<span class="ml-1 normal-case text-muted-foreground/70">optional</span>{/if}
							</span>
							<div class="flex items-center gap-1">
								{#each groupSteps as step, i (step.key)}
									{@const active = step.key === currentStep.key}
									{@const done = stepDone[step.key]}
									{@const StepIcon = STEP_ICONS[step.icon]}
									{#if i > 0}<span class="h-px w-2.5 shrink-0 bg-border" aria-hidden="true"></span>{/if}
									<button
										type="button"
										class="flex items-center gap-1.5 rounded-full border py-1 pl-1 pr-1 text-xs transition-colors {active ? 'pr-2.5' : ''}
											{active
											? 'border-primary bg-primary text-primary-foreground'
											: done
												? 'border-success bg-success/15 text-success'
												: 'border-border bg-background text-muted-foreground'}"
										data-testid="onboarding-step-tab-{step.key}"
										title={step.label}
										aria-label={step.label}
										onclick={() => goToStep(step.key)}
									>
										<span class="flex h-6 w-6 shrink-0 items-center justify-center">
											{#if done && !active}
												<Check class="h-4 w-4" />
											{:else}
												<StepIcon class="h-4 w-4" />
											{/if}
										</span>
										{#if active}<span class="font-medium">{step.label}</span>{/if}
									</button>
								{/each}
							</div>
						</div>
					{/each}
				</div>
			</div>

			<Card.Root>
				<Card.Content class="p-6">
					<!-- ============ Portfolio ============ -->
					{#if currentStep.key === 'portfolio'}
						<WizardStepScaffold step={currentStep}>
							<div class="grid gap-4">
								<div>
									<label for="ob-portfolio-name" class="mb-1 block text-xs font-medium text-muted-foreground">Portfolio name</label>
									<Input id="ob-portfolio-name" data-testid="onboarding-portfolio-name" data-coach="onboarding-portfolio" bind:value={portfolioForm.name} placeholder="e.g. Smith Family Rentals" />
									{#if portfolioErrors.name}<p class="mt-1 text-xs text-destructive">{portfolioErrors.name}</p>{/if}
								</div>
								<div>
									<label for="ob-portfolio-company" class="mb-1 block text-xs font-medium text-muted-foreground">
										Management company <span class="font-normal">(optional)</span>
									</label>
									<Input id="ob-portfolio-company" data-testid="onboarding-portfolio-company" bind:value={portfolioForm.managementCompanyName} placeholder="e.g. Smith Property Management" />
								</div>
								<div>
									<label for="ob-portfolio-timezone" class="mb-1 block text-xs font-medium text-muted-foreground">Time zone</label>
									<TimeZoneSelect id="ob-portfolio-timezone" testid="onboarding-portfolio-timezone" bind:value={portfolioForm.timeZone} />
									<p class="mt-1 text-xs text-muted-foreground">Used for rent reminders, late-fee timing, and your daily briefing.</p>
								</div>
							</div>
						</WizardStepScaffold>

					<!-- ============ Owner ============ -->
					{:else if currentStep.key === 'owner'}
						<WizardStepScaffold step={currentStep}>
							{#if hasExistingOwners && !createdOwner}
								<div class="mb-4 rounded-md border border-success/40 bg-success/10 px-3 py-2 text-sm text-foreground" data-testid="onboarding-owner-existing">
									You already have {ownersQuery.data?.totalCount} owner{(ownersQuery.data?.totalCount ?? 0) === 1 ? '' : 's'} on file. Choose one to review, add another, or skip ahead.
								</div>
							{:else if ownerPrefilled}
								<div class="mb-4 rounded-md border border-primary/40 bg-primary/10 px-3 py-2 text-sm text-foreground" data-testid="onboarding-owner-prefilled">
									We filled this in from your account — just confirm it's right. Own through an LLC or trust? Change the name and type below.
								</div>
							{/if}
							{#if ownerRecordOptions.length > 0}
								<div class="mb-4 rounded-md border border-border bg-muted/30 p-3" data-testid="onboarding-owner-selector-panel">
									<label for="ob-owner-selector" class="mb-1 block text-xs font-medium text-muted-foreground">Owner record</label>
									<div class="flex items-center gap-2">
										<div class="min-w-0 flex-1">
											<RemoteRecordSelect
												queryKey={['onboarding-owner-records', portfolioId]}
												label="Owner record"
												value={selectedOwnerId === NEW_ONBOARDING_OWNER_VALUE ? '' : selectedOwnerId}
												selectedLabel={ownerSelectionLabel}
												placeholder="Add a new owner"
												clearLabel="Add a new owner"
												searchPlaceholder="Search owners…"
												testid="onboarding-owner-record-select"
												loadPage={loadOwnerOptions}
												onValueChange={selectRemoteOwnerRecord}
											/>
										</div>
										{#if selectedOwnerRecord}
											<Button
												variant="outline"
												size="icon"
												class="text-muted-foreground hover:border-destructive/60 hover:bg-destructive/10 hover:text-destructive"
												aria-label={`Delete owner ${ownerOptionLabel(selectedOwnerRecord)}`}
												title="Delete owner"
												data-testid="onboarding-owner-delete"
												disabled={deleteOwnerMutation.isPending}
												onclick={() => (ownerDeleteTarget = selectedOwnerRecord)}
											>
												<Trash2 class="h-4 w-4" />
											</Button>
										{/if}
									</div>
									<p class="mt-1 text-xs text-muted-foreground">
										Pick an owner to edit these fields, or start a fresh owner record. Owners assigned
										to properties must be reassigned or cleared before they can be deleted.
									</p>
								</div>
							{/if}
							<div class="grid gap-4 sm:grid-cols-2">
								<div class="sm:col-span-2">
									<label for="ob-owner-name" class="mb-1 block text-xs font-medium text-muted-foreground">Owner name</label>
									<Input id="ob-owner-name" data-testid="onboarding-owner-name" data-coach="onboarding-owner" bind:value={ownerForm.name} placeholder="e.g. John Smith or Smith Holdings LLC" />
									{#if ownerErrors.name}<p class="mt-1 text-xs text-destructive">{ownerErrors.name}</p>{/if}
								</div>
								<div>
									<span class="mb-1 block text-xs font-medium text-muted-foreground">Type</span>
									<Select.Root type="single" bind:value={ownerForm.ownerEntityType}>
										<Select.Trigger class="w-full" data-testid="onboarding-owner-type">{ownerForm.ownerEntityType}</Select.Trigger>
										<Select.Content>
											{#each OWNER_ENTITY_TYPES as t}
												<Select.Item value={t} label={t}>{t}</Select.Item>
											{/each}
										</Select.Content>
									</Select.Root>
								</div>
								<div>
									<label for="ob-owner-email" class="mb-1 block text-xs font-medium text-muted-foreground">Email <span class="font-normal">(optional)</span></label>
									<Input id="ob-owner-email" type="email" autocomplete="email" data-testid="onboarding-owner-email" bind:value={ownerForm.email} placeholder="owner@example.com" />
									{#if ownerErrors.email}<p class="mt-1 text-xs text-destructive">{ownerErrors.email}</p>{/if}
								</div>
								<div class="sm:col-span-2">
									<label for="ob-owner-taxid" class="mb-1 block text-xs font-medium text-muted-foreground">Tax ID / SSN <span class="font-normal">(optional)</span></label>
									<Input id="ob-owner-taxid" data-testid="onboarding-owner-taxid" bind:value={ownerForm.taxId} placeholder="For 1099s and tax reports" />
								</div>
							</div>
						</WizardStepScaffold>

					<!-- ============ Property + Units (sub-stepped) ============ -->
					{:else if currentStep.key === 'property'}
						<WizardStepScaffold step={currentStep}>
							{#if hasExistingProperties && !createdProperty}
								<div class="mb-4 rounded-md border border-success/40 bg-success/10 px-3 py-2 text-sm text-foreground" data-testid="onboarding-property-existing">
									You already have {propertiesQuery.data?.totalCount} propert{(propertiesQuery.data?.totalCount ?? 0) === 1 ? 'y' : 'ies'}. Choose one to review, add another, or skip ahead.
								</div>
							{/if}
							{#if propertyRecordOptions.length > 0}
								<div class="mb-4 rounded-md border border-border bg-muted/30 p-3" data-testid="onboarding-property-selector-panel">
									<label for="ob-property-selector" class="mb-1 block text-xs font-medium text-muted-foreground">Property record</label>
									<div class="flex items-center gap-2">
										<div class="min-w-0 flex-1">
											<RemoteRecordSelect
												queryKey={['onboarding-property-records', portfolioId]}
												label="Property record"
												value={selectedPropertyId === NEW_ONBOARDING_PROPERTY_VALUE ? '' : selectedPropertyId}
												selectedLabel={propertySelectionLabel}
												placeholder="Add a new property"
												clearLabel="Add a new property"
												searchPlaceholder="Search properties…"
												testid="onboarding-property-record-select"
												loadPage={loadPropertyOptions}
												onValueChange={(value) => void selectRemotePropertyRecord(value)}
											/>
										</div>
										{#if selectedPropertyRecord}
											<Button
												variant="outline"
												size="icon"
												class="text-muted-foreground hover:border-destructive/60 hover:bg-destructive/10 hover:text-destructive"
												aria-label={`Delete property ${propertyOptionLabel(selectedPropertyRecord)}`}
												title="Delete property"
												data-testid="onboarding-property-delete"
												disabled={deletePropertyMutation.isPending}
												onclick={() => (propertyDeleteTarget = selectedPropertyRecord)}
											>
												<Trash2 class="h-4 w-4" />
											</Button>
										{/if}
									</div>
									<p class="mt-1 text-xs text-muted-foreground">Pick a property to edit these fields, or start a fresh property record.</p>
								</div>
							{/if}

							{#if propertySub === 'address'}
								<div class="grid gap-4" data-testid="onboarding-property-sub-address">
									<div>
										<RemoteRecordSelect
											queryKey={['onboarding-property-owners', portfolioId]}
											label="Owner"
											bind:value={propertyForm.ownerEntityId}
											selectedLabel={ownerRecordOptions.find((owner) => String(owner.id) === propertyForm.ownerEntityId)?.name}
											placeholder="No owner assigned"
											clearLabel="No owner assigned"
											searchPlaceholder="Search owners…"
											testid="onboarding-property-owner"
											loadPage={loadOwnerOptions}
										/>
										<p class="mt-1 text-xs text-muted-foreground">
											You can leave this unassigned during onboarding. Assign an owner before reports
											or owner statements need to be correct.
										</p>
									</div>
									<div>
										<label for="ob-prop-name" class="mb-1 block text-xs font-medium text-muted-foreground">Property name</label>
										<Input id="ob-prop-name" data-testid="onboarding-property-name" data-coach="onboarding-property" bind:value={propertyForm.name} placeholder="e.g. 123 Main St Duplex" />
										{#if propertyErrors.name}<p class="mt-1 text-xs text-destructive">{propertyErrors.name}</p>{/if}
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
									<div class="grid gap-3 sm:grid-cols-3">
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
											<Input id="ob-prop-zip" type="text" inputmode="numeric" autocomplete="postal-code" maxlength={10} mask="zip" data-testid="onboarding-property-zip" bind:value={propertyForm.postalCode} placeholder="ZIP" />
											{#if propertyErrors.postalCode}<p class="mt-1 text-xs text-destructive">{propertyErrors.postalCode}</p>{/if}
										</div>
									</div>
									<div>
										<fieldset>
											<legend class="mb-1 text-xs font-medium text-muted-foreground">How many rentals are at this address?</legend>
											<div class="grid gap-2 sm:grid-cols-2">
												<label class="cursor-pointer rounded-lg border p-3 transition-colors {propertyForm.rentalStructure === 'SingleRental' ? 'border-primary bg-primary/10' : 'border-border hover:bg-muted/40'}">
													<input class="sr-only" type="radio" name="onboarding-rental-structure" value="SingleRental" bind:group={propertyForm.rentalStructure} data-testid="onboarding-single-rental" />
													<span class="block text-sm font-semibold">One rental</span>
													<span class="mt-1 block text-xs text-muted-foreground">One house, condo, townhome, or other rentable space. Rental Command keeps its technical unit record out of your way.</span>
												</label>
												<label class="cursor-pointer rounded-lg border p-3 transition-colors {propertyForm.rentalStructure === 'MultiRental' ? 'border-primary bg-primary/10' : 'border-border hover:bg-muted/40'}">
													<input class="sr-only" type="radio" name="onboarding-rental-structure" value="MultiRental" bind:group={propertyForm.rentalStructure} data-testid="onboarding-multi-rental" />
													<span class="block text-sm font-semibold">Multiple rentals</span>
													<span class="mt-1 block text-xs text-muted-foreground">A duplex, apartment building, or another address with separate rentals.</span>
												</label>
											</div>
											{#if propertyErrors.rentalStructure}<p class="mt-1 text-xs text-destructive">{propertyErrors.rentalStructure}</p>{/if}
										</fieldset>
									</div>
									<div>
										<span class="mb-1 block text-xs font-medium text-muted-foreground">What kind of property is it?</span>
										<Select.Root type="single" bind:value={propertyForm.type}>
											<Select.Trigger class="w-full" data-testid="onboarding-property-type">{formatPropertyType(propertyForm.type)}</Select.Trigger>
											<Select.Content>
												{#each propertyTypeOptions as option}
													<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
												{/each}
											</Select.Content>
										</Select.Root>
									</div>
									<div>
										<label for="ob-prop-address2" class="mb-1 block text-xs font-medium text-muted-foreground">Apt / Suite / Unit # <span class="text-muted-foreground/60">(optional)</span></label>
										<Input id="ob-prop-address2" data-testid="onboarding-property-address2" bind:value={propertyForm.addressLine2} placeholder="Unit 4B" />
									</div>
								</div>
							{:else}
								<div data-testid="onboarding-property-sub-units">
									<h3 class="mb-1 text-sm font-semibold">{isSingleRental ? 'Rental details' : 'Units'}</h3>
									<p class="mb-3 text-xs text-muted-foreground">
										{isSingleRental
											? 'Check the details for this one rental. Rental Command keeps the underlying Unit record synchronized for leases, payments, and maintenance.'
											: 'Add one row for each rentable unit. Units with history are preserved and cannot be deleted from onboarding.'}
									</p>
									{#if selectedPropertyRecord}
										<div class="mb-4 rounded-md border border-border bg-muted/30 p-3" data-testid="onboarding-existing-units">
											<div class="mb-2 flex items-center justify-between gap-3">
												<div>
													<p class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Saved units</p>
													<p class="text-xs text-muted-foreground">
														Delete only draft units that have no history; otherwise leave them in place.
													</p>
												</div>
												<Button
													variant="outline"
													size="sm"
													onclick={() => selectedPropertyUnitsQuery.refetch()}
													disabled={selectedPropertyUnitsQuery.isFetching}
													data-testid="onboarding-existing-units-refresh"
												>
													Refresh
												</Button>
											</div>
											{#if selectedPropertyUnitsQuery.isLoading}
												<p class="text-sm text-muted-foreground">Loading units…</p>
											{:else if selectedPropertyUnits.length === 0}
												<p class="text-sm text-muted-foreground">No saved units on this property yet.</p>
											{:else}
												<div class="divide-y divide-border rounded-md border border-border bg-background">
													{#each selectedPropertyUnits as unit (unit.id)}
														<div class="flex items-center justify-between gap-3 px-3 py-2" data-testid="onboarding-existing-unit-{unit.id}">
															<div class="min-w-0">
																<p class="truncate text-sm font-medium">{isSingleRental ? 'Rental' : `Unit ${unit.unitNumber}`}</p>
																<p class="text-xs text-muted-foreground">
																	{unitSummary(unit)}
																</p>
															</div>
															{#if !isSingleRental}<Button
																variant="outline"
																size="icon"
																class="text-muted-foreground hover:border-destructive/60 hover:bg-destructive/10 hover:text-destructive"
																aria-label={`Delete unit ${unit.unitNumber}`}
																title="Delete unit"
																data-testid="onboarding-existing-unit-delete-{unit.id}"
																disabled={deleteUnitMutation.isPending}
																onclick={() => (unitDeleteTarget = unit)}
															>
																<Trash2 class="h-4 w-4" />
															</Button>{/if}
														</div>
													{/each}
												</div>
											{/if}
										</div>
									{/if}
									{#if isSingleRental}
										<p class="mb-3 rounded-md border border-primary/30 bg-primary/5 p-3 text-xs text-muted-foreground" data-testid="onboarding-single-unit-note">
											{selectedPropertyRecord
												? 'This address is one rental. Rental Command will keep its existing underlying Unit instead of creating a duplicate.'
												: 'This address is one rental. Just check its beds, baths, and rent below; Rental Command creates the one underlying Unit automatically.'}
										</p>
									{:else}
										<div class="mb-3 flex items-end gap-2" data-testid="onboarding-unit-count">
											<div>
												<span class="mb-1 block text-[11px] text-muted-foreground">How many units does this property have?</span>
												<Input type="text" inputmode="numeric" mask="integer" class="w-28" data-testid="onboarding-unit-count-input" bind:value={unitCount} placeholder="e.g. 4" />
											</div>
											<Button variant="outline" size="sm" data-testid="onboarding-unit-count-apply" onclick={generateUnitRows} disabled={!unitCount}>Add that many</Button>
										</div>
									{/if}
									{#if !(isSingleRental && selectedPropertyRecord)}
									<div class="space-y-3" data-testid="onboarding-units">
										{#each unitRows as row, i (i)}
											<div class="rounded-md border border-border bg-background p-3" data-testid="onboarding-unit-row">
													<div class="grid gap-2 {isResidentialUnit ? (isSingleRental ? 'sm:grid-cols-3' : 'sm:grid-cols-4') : (isSingleRental ? 'sm:grid-cols-1' : 'sm:grid-cols-2')}" data-testid="onboarding-unit-fields">
														{#if !isSingleRental}<div>
														<span class="mb-1 block text-[11px] text-muted-foreground">Unit #</span>
														<Input data-testid="onboarding-unit-number-{i}" bind:value={row.unitNumber} placeholder="1, A, etc." />
														{#if unitRowErrors[i]?.unitNumber}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].unitNumber}</p>{/if}
													</div>{/if}
														{#if isResidentialUnit}
															<div>
															<span class="mb-1 block text-[11px] text-muted-foreground">Beds</span>
															<Input type="text" inputmode="numeric" mask="integer" data-testid="onboarding-unit-beds-{i}" bind:value={row.bedrooms} placeholder="2" />
															{#if unitRowErrors[i]?.bedrooms}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].bedrooms}</p>{/if}
															</div>
															<div>
															<span class="mb-1 block text-[11px] text-muted-foreground">Baths</span>
															<Input type="text" inputmode="decimal" mask="decimal" data-testid="onboarding-unit-baths-{i}" bind:value={row.bathrooms} placeholder="1" />
															{#if unitRowErrors[i]?.bathrooms}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].bathrooms}</p>{/if}
															</div>
														{/if}
														<div>
														<span class="mb-1 block text-[11px] text-muted-foreground">Market rent</span>
														<div class="flex items-center gap-1">
															<Input type="text" inputmode="decimal" mask="currency" data-testid="onboarding-unit-rent-{i}" bind:value={row.marketRent} placeholder="1500" />
															{#if unitRows.length > 1}
																<button type="button" class="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-destructive/10 hover:text-destructive" aria-label="Remove unit" data-testid="onboarding-unit-remove-{i}" onclick={() => removeUnitRow(i)}>
																	<Trash2 class="h-4 w-4" />
																</button>
															{/if}
															</div>
														{#if unitRowErrors[i]?.marketRent}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].marketRent}</p>{/if}
													</div>
												</div>
											<details class="mt-3" data-testid="onboarding-unit-more-details">
												<summary class="cursor-pointer text-xs font-medium text-muted-foreground" data-testid="onboarding-unit-more-details-toggle">More unit details</summary>
												<div class="mt-3 grid gap-2 sm:grid-cols-3">
													<div>
														<span class="mb-1 block text-[11px] text-muted-foreground">Square feet</span>
														<Input type="text" inputmode="numeric" mask="integer" data-testid="onboarding-unit-square-feet-{i}" bind:value={row.squareFeet} placeholder="1425" />
														{#if unitRowErrors[i]?.squareFeet}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].squareFeet}</p>{/if}
													</div>
													<div>
														<span class="mb-1 block text-[11px] text-muted-foreground">Floor plan</span>
														<Input data-testid="onboarding-unit-floor-plan-{i}" bind:value={row.floorPlan} placeholder="Garden 2B" />
														{#if unitRowErrors[i]?.floorPlan}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].floorPlan}</p>{/if}
													</div>
													<div>
														<span class="mb-1 block text-[11px] text-muted-foreground">Notes</span>
														<Input data-testid="onboarding-unit-notes-{i}" bind:value={row.notes} placeholder="Access or parking notes" />
														{#if unitRowErrors[i]?.notes}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].notes}</p>{/if}
													</div>
												</div>
											</details>
											</div>
										{/each}
									</div>
									{:else}
										<p class="text-xs text-muted-foreground">This rental already has its saved details. Saving here updates the address without creating a second Unit.</p>
									{/if}
									{#if !isSingleRental}
										<Button variant="outline" size="sm" class="mt-2 gap-1" data-testid="onboarding-add-unit" onclick={addUnitRow}>
											<Plus class="h-4 w-4" />
											Add another unit
										</Button>
									{/if}
								</div>
							{/if}
						</WizardStepScaffold>

					<!-- ============ Tenants ============ -->
					{:else if currentStep.key === 'tenants'}
						<WizardStepScaffold step={currentStep}>
							{#if hasExistingTenants && createdTenants.length === 0}
								<div class="mb-4 rounded-md border border-success/40 bg-success/10 px-3 py-2 text-sm text-foreground" data-testid="onboarding-tenants-existing">
									You already have {tenantsQuery.data?.totalCount} tenant{(tenantsQuery.data?.totalCount ?? 0) === 1 ? '' : 's'}. Add more or skip ahead.
								</div>
							{/if}
							<div class="space-y-3" data-testid="onboarding-tenants-list">
								{#each tenantRows as row, i (i)}
									<div class="rounded-md border border-border bg-background p-3" data-testid="onboarding-tenant-row">
										<div class="grid gap-2 sm:grid-cols-2">
											<div>
												<span class="mb-1 block text-[11px] text-muted-foreground">First name</span>
												<Input data-testid="onboarding-tenant-first-{i}" data-coach={i === 0 ? 'onboarding-tenants' : undefined} bind:value={row.firstName} placeholder="First name" />
												{#if tenantRowErrors[i]?.firstName}<p class="mt-1 text-[11px] text-destructive">{tenantRowErrors[i].firstName}</p>{/if}
											</div>
											<div>
												<span class="mb-1 block text-[11px] text-muted-foreground">Last name</span>
												<Input data-testid="onboarding-tenant-last-{i}" bind:value={row.lastName} placeholder="Last name" />
												{#if tenantRowErrors[i]?.lastName}<p class="mt-1 text-[11px] text-destructive">{tenantRowErrors[i].lastName}</p>{/if}
											</div>
											<div>
												<span class="mb-1 block text-[11px] text-muted-foreground">Email (optional)</span>
												<Input type="email" autocomplete="email" data-testid="onboarding-tenant-email-{i}" bind:value={row.email} placeholder="tenant@example.com" />
												{#if tenantRowErrors[i]?.email}<p class="mt-1 text-[11px] text-destructive">{tenantRowErrors[i].email}</p>{/if}
											</div>
											<div>
												<span class="mb-1 block text-[11px] text-muted-foreground">Phone (optional)</span>
												<div class="flex items-center gap-1">
													<Input type="tel" autocomplete="tel" inputmode="tel" mask="phone" data-testid="onboarding-tenant-phone-{i}" bind:value={row.phone} placeholder="(555) 555-5555" />
													{#if tenantRows.length > 1}
														<button type="button" class="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-destructive/10 hover:text-destructive" aria-label="Remove tenant" data-testid="onboarding-tenant-remove-{i}" onclick={() => removeTenantRow(i)}>
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
						</WizardStepScaffold>

					<!-- ============ Import a lease (lease-first: scan → builds the whole chain) ============ -->
					{:else if currentStep.key === 'import'}
						<WizardStepScaffold step={currentStep}>
							<div data-coach="onboarding-import">
								<LeaseFirstImport {portfolioId} oncomplete={handleLeaseImportComplete} />
							</div>
						</WizardStepScaffold>

					<!-- ============ Lease ============ -->
					{:else if currentStep.key === 'lease'}
						<WizardStepScaffold step={currentStep}>
							{#snippet photoPrefill()}
								{#if !leaseBlocked}
									<LeasePhotoPrefill onapply={applyLeasePrefill} />
								{/if}
							{/snippet}

							<div class="mb-4 flex flex-wrap items-center justify-between gap-2 rounded-md border border-border bg-muted/40 px-3 py-2.5" data-testid="onboarding-lease-bulk-import">
								<span class="text-sm text-foreground">Have many leases on paper? Import them all at once instead.</span>
								<Button variant="outline" size="sm" class="gap-1" href="/scan/batch" data-testid="onboarding-import-leases">
									<FileText class="h-4 w-4" />
									Import leases
								</Button>
							</div>
							{#if leaseBlocked}
								<div class="rounded-md border border-warning/40 bg-warning/10 px-3 py-3 text-sm text-foreground" data-testid="onboarding-lease-blocked">
									You'll need at least one property with a unit and one tenant before you can create a
									lease. You can skip this and add it later from the Leases page.
								</div>
							{:else}
								<div class="grid gap-4 sm:grid-cols-2">
									<div>
										<RemoteRecordSelect
											queryKey={['onboarding-lease-tenants', portfolioId]}
											label="Tenant"
											bind:value={leaseForm.tenantId}
											selectedLabel={selectedLeaseTenantLabel}
											placeholder="Select tenant"
											searchPlaceholder="Search tenants…"
											required
											testid="onboarding-lease-tenant"
											loadPage={loadTenantOptions}
											onValueChange={(_value, option) => (selectedLeaseTenantLabel = option?.label ?? '')}
										/>
										{#if leaseErrors.tenantId}<p class="mt-1 text-xs text-destructive">{leaseErrors.tenantId}</p>{/if}
									</div>
									<div>
										<RemoteRecordSelect
											queryKey={['onboarding-lease-properties', portfolioId]}
											label="Property"
											bind:value={leaseForm.propertyId}
											selectedLabel={selectedLeasePropertyLabel}
											placeholder="Select property"
											searchPlaceholder="Search properties…"
											required
											testid="onboarding-lease-property"
											loadPage={loadPropertyOptions}
											onValueChange={(_value, option) => {
												leaseForm.unitId = '';
												selectedLeaseUnitLabel = '';
												selectedLeasePropertyLabel = option?.label ?? '';
											}}
										/>
									</div>
									<div>
										<RemoteRecordSelect
											queryKey={['onboarding-lease-units', portfolioId, leaseForm.propertyId]}
											label="Unit"
											bind:value={leaseForm.unitId}
											selectedLabel={selectedLeaseUnitLabel}
											placeholder={leaseForm.propertyId ? 'Select unit' : 'Pick a property first'}
											searchPlaceholder="Search units…"
											disabled={!leaseForm.propertyId}
											required
											testid="onboarding-lease-unit"
											loadPage={loadLeaseUnitOptions}
											onValueChange={(_value, option) => (selectedLeaseUnitLabel = option?.label ?? '')}
										/>
										{#if leaseErrors.unitId}<p class="mt-1 text-xs text-destructive">{leaseErrors.unitId}</p>{/if}
									</div>
									<div>
										<label for="ob-lease-rent" class="mb-1 block text-xs font-medium text-muted-foreground">Monthly rent</label>
										<Input id="ob-lease-rent" type="text" inputmode="decimal" mask="currency" data-testid="onboarding-lease-rent" bind:value={leaseForm.monthlyRent} placeholder="1500" />
										{#if leaseErrors.monthlyRent}<p class="mt-1 text-xs text-destructive">{leaseErrors.monthlyRent}</p>{/if}
									</div>
									<div>
										<label for="ob-lease-start" class="mb-1 block text-xs font-medium text-muted-foreground">Start date</label>
										<DatePicker
											id="ob-lease-start"
											testid="onboarding-lease-start"
											bind:value={leaseForm.startDate}
											onchange={handleLeaseStartDateChange}
											placeholder="Start date"
										/>
										{#if leaseErrors.startDate}<p class="mt-1 text-xs text-destructive">{leaseErrors.startDate}</p>{/if}
									</div>
									<div>
										<label for="ob-lease-end" class="mb-1 block text-xs font-medium text-muted-foreground">End date</label>
										<DatePicker
											id="ob-lease-end"
											testid="onboarding-lease-end"
											bind:value={leaseForm.endDate}
											onchange={handleLeaseEndDateChange}
											showToday={false}
											placeholder="End date"
										/>
										{#if leaseErrors.endDate}<p class="mt-1 text-xs text-destructive">{leaseErrors.endDate}</p>{/if}
									</div>
									<div>
										<label for="ob-lease-deposit" class="mb-1 block text-xs font-medium text-muted-foreground">Security deposit</label>
										<Input id="ob-lease-deposit" type="text" inputmode="decimal" mask="currency" data-testid="onboarding-lease-deposit" bind:value={leaseForm.securityDeposit} placeholder="1500" />
										{#if leaseErrors.securityDeposit}<p class="mt-1 text-xs text-destructive">{leaseErrors.securityDeposit}</p>{/if}
									</div>
									<div>
										<label for="ob-lease-dueday" class="mb-1 flex items-center gap-1 text-xs font-medium text-muted-foreground">Rent due day<HelpPopover title="Rent due day" summary="The day of the month rent is expected — e.g. 1 means the 1st of each month. The system posts rent charges and calculates late fees based on this date." testid="help-rent-due-day" /></label>
										<Input id="ob-lease-dueday" type="text" inputmode="numeric" maxlength={2} mask="integer" data-testid="onboarding-lease-dueday" bind:value={leaseForm.rentDueDay} placeholder="1" />
										{#if leaseErrors.rentDueDay}<p class="mt-1 text-xs text-destructive">{leaseErrors.rentDueDay}</p>{/if}
									</div>
								</div>
								{#if leasePrefillDraftId != null}
									<div class="mt-4 rounded-md border border-border bg-muted/20 p-3">
										<LeaseScanSignatureChoice
											bind:reviewDisposition={leaseReviewDisposition}
											bind:documentTemplateId={leaseDocumentTemplateId}
											propertyId={Number(leaseForm.propertyId) || 0}
											disabled={saveLeaseMutation.isPending}
										/>
									</div>
								{/if}
							{/if}
						</WizardStepScaffold>

					<!-- ============ My alerts ============ -->
					{:else if currentStep.key === 'notifications'}
						<WizardStepScaffold step={currentStep}>
							{#if myAlertsQuery.data}
								<p class="mb-3 text-sm text-muted-foreground">
									For {myAlertsQuery.data.displayName}: {myAlertsQuery.data.email || 'no email'} · {myAlertsQuery.data.phoneNumber || 'no phone'}
								</p>
							{/if}
							<div class="grid gap-3 sm:grid-cols-2" data-coach="onboarding-notifications">
								<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3"><input type="checkbox" bind:checked={alertForm.enableInApp} /> <span>In-app bell and inbox</span></label>
								<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3"><input type="checkbox" bind:checked={alertForm.enableMobilePush} /> <span>Mobile push</span></label>
								<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3"><input type="checkbox" bind:checked={alertForm.enableEmail} /> <span>Email</span></label>
								<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3"><input type="checkbox" bind:checked={alertForm.enableSms} /> <span>SMS</span></label>
							</div>
							<p class="mt-3 text-xs text-muted-foreground">These choices are only for you. Team alerts and tenant notices are separate.</p>
						</WizardStepScaffold>
					{/if}
				</Card.Content>
			</Card.Root>

			<!-- Footer nav -->
			<div class="mt-4 flex items-center justify-between gap-2">
				<Button variant="ghost" class="gap-1" data-testid="onboarding-back" disabled={(stepIndex === 0 && !(currentStep.key === 'property' && propertySub !== 'address')) || anyPending} onclick={back}>
					<ArrowLeft class="h-4 w-4" />
					Back
				</Button>
				<div class="flex items-center gap-2">
					<!-- A2: on core steps, Skip is a quiet text link ("I'll add this later"), not a button that
					     competes with the primary "Save & continue". Optional steps keep a plain "Skip". -->
					<Button
						variant={currentStep.core ? 'ghost' : 'outline'}
						class={currentStep.core ? 'text-muted-foreground underline-offset-4 hover:bg-transparent hover:text-foreground hover:underline' : undefined}
						data-testid="onboarding-skip"
						disabled={anyPending}
						onclick={skip}
					>
						{currentStep.core ? "I'll add this later" : 'Skip'}
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
						{#if propertySub === 'address'}
							<Button class="gap-1" data-testid="onboarding-property-next-sub" disabled={anyPending} onclick={() => { if (propertyAddressValid()) { seedUnitsForStructure(); propertySub = 'units'; } }}>
								Next <ArrowRight class="h-4 w-4" />
							</Button>
						{:else}
							<Button class="gap-1" data-testid="onboarding-next" disabled={anyPending} onclick={submitProperty}>
								{savePropertyMutation.isPending ? 'Saving…' : 'Save & continue'}
								<ArrowRight class="h-4 w-4" />
							</Button>
						{/if}
					{:else if currentStep.key === 'tenants'}
						<Button class="gap-1" data-testid="onboarding-next" disabled={anyPending} onclick={submitTenants}>
							{saveTenantsMutation.isPending ? 'Saving…' : 'Save & continue'}
							<ArrowRight class="h-4 w-4" />
						</Button>
					{:else if currentStep.key === 'lease'}
						{#if leaseBlocked}
							<Button class="gap-1" data-testid="onboarding-finish" onclick={() => next()}>
								<CheckCircle2 class="h-4 w-4" />
								Finish
							</Button>
						{:else}
							<Button class="gap-1" data-testid="onboarding-finish" disabled={anyPending || leaseSignatureChoiceInvalid} onclick={submitLease}>
								{saveLeaseMutation.isPending ? 'Creating…' : leasePrefillDraftId != null ? 'Import agreement & finish' : 'Create lease & finish'}
								<CheckCircle2 class="h-4 w-4" />
							</Button>
						{/if}
					{:else if currentStep.key === 'notifications'}
						<Button class="gap-1" data-testid="onboarding-next" disabled={anyPending || myAlertsQuery.isLoading} onclick={() => saveMyAlertsMutation.mutate()}>
							{saveMyAlertsMutation.isPending ? 'Saving…' : 'Save & continue'}
							<ArrowRight class="h-4 w-4" />
						</Button>
					{/if}
				</div>
			</div>
		{/if}
	</div>
</div>

<ConfirmDialog
	open={ownerDeleteTarget !== null}
	title="Delete owner"
	message={ownerDeleteTarget
		? ownerDeleteAssignedCount > 0
			? `"${ownerOptionLabel(ownerDeleteTarget)}" is assigned to ${ownerDeleteAssignedCount} propert${ownerDeleteAssignedCount === 1 ? 'y' : 'ies'}. Reassign or clear those properties before deleting this owner.`
			: `Delete "${ownerOptionLabel(ownerDeleteTarget)}"? This owner is not assigned to any properties.`
		: ''}
	confirmLabel={ownerDeleteAssignedCount > 0 ? 'Close' : 'Delete owner'}
	busy={deleteOwnerMutation.isPending}
	testid="onboarding-owner-delete-confirm"
	onconfirm={() => {
		if (!ownerDeleteTarget || ownerDeleteAssignedCount > 0) {
			ownerDeleteTarget = null;
			return;
		}
		deleteOwnerMutation.mutate({ id: ownerDeleteTarget.id });
	}}
	oncancel={() => (ownerDeleteTarget = null)}
/>

<ConfirmDialog
	open={propertyDeleteTarget !== null}
	title="Delete property"
	message={propertyDeleteTarget
		? `Delete "${propertyOptionLabel(propertyDeleteTarget)}"? Only draft properties with no real units or history can be deleted. Empty single-family properties delete their generated unit too. Properties with leases, applications, work orders, expenses, loans, inspections, appointments, or documents stay preserved.`
		: ''}
	confirmLabel="Delete property"
	busy={deletePropertyMutation.isPending}
	testid="onboarding-property-delete-confirm"
	onconfirm={() => propertyDeleteTarget && deletePropertyMutation.mutate(propertyDeleteTarget.id)}
	oncancel={() => (propertyDeleteTarget = null)}
/>

<ConfirmDialog
	open={unitDeleteTarget !== null}
	title="Delete unit"
	message={unitDeleteTarget
		? `Delete Unit ${unitDeleteTarget.unitNumber}? Only draft units with no leases, applications, work orders, expenses, inspections, appointments, or documents can be deleted. Historical units stay preserved.`
		: ''}
	confirmLabel="Delete unit"
	busy={deleteUnitMutation.isPending}
	testid="onboarding-unit-delete-confirm"
	onconfirm={() => unitDeleteTarget && deleteUnitMutation.mutate(unitDeleteTarget.id)}
	oncancel={() => (unitDeleteTarget = null)}
/>
