<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { browser } from '$app/environment';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { owners } from '$lib/api/endpoints/owners';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { leases } from '$lib/api/endpoints/leases';
	import { scan } from '$lib/api/scan';
	import { notifications } from '$lib/api/endpoints/notifications';
	import type { Owner, Property, Unit, Tenant, OwnerEntityType } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { getCurrentUser } from '$lib/stores/auth.svelte';
	import {
		settingsSchema,
		ownerSchema,
		propertySchema,
		unitSchema,
		tenantSchema,
		leaseSchema,
		leaseRentTrackingErrors,
		parseForm,
	} from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatPhoneInput } from '$lib/utils/phone';
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
	import {
		WIZARD_STEPS,
		CORE_WIZARD_STEPS,
		ONBOARDING_SETUP_SHORTCUTS,
		wizardStep,
		type WizardStepKey,
		type WizardStepMeta,
	} from '$lib/onboarding/wizard-steps';
	import {
		resolveInitialOnboardingState,
		shouldFinishAfterOptionalStep,
	} from '$lib/onboarding/onboarding-flow-state';
	import {
		buildOnboardingLeaseScanOverrides,
		type OnboardingLeaseConfirmInput,
	} from '$lib/onboarding/lease-scan-confirm';
	import { ownerEntityIdForOnboarding } from '$lib/onboarding/owner-selection';
	import { buildOnboardingPropertyPayload } from '$lib/onboarding/property-payload';
	import { formatPropertyType, propertyTypeOptions } from '$lib/properties/property-labels';
	import {
		Check,
		ArrowLeft,
		ArrowRight,
		Plus,
		Trash2,
		PartyPopper,
		Sparkles,
		ScanLine,
		FileSpreadsheet,
		FileText,
		CheckCircle2,
		Building,
		UserCircle2,
		Home,
		Users,
		Bell,
		MessageSquare,
		ListChecks,
	} from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	// ---------------------------------------------------------------------------
	// Step model. The ordered flow is the core entity steps (portfolio → lease)
	// followed by the optional provider-setup steps (notifications, texting). The
	// step metadata (titles, explanations, where-to-find, docs links) lives in the
	// shared registry so Settings can deep-link into the exact same steps.
	// ---------------------------------------------------------------------------
	const STEP_ICONS = { Building, UserCircle2, Home, Users, FileText, Bell, MessageSquare } as const;
	const STEPS = WIZARD_STEPS;

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
	let portfolioSaved = $state(false);
	let notificationsSaved = $state(false);
	let textingSaved = $state(false);
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
	const notificationEmailQuery = createQuery(() => ({
		queryKey: ['notification-email', portfolioId],
		enabled: portfolioId > 0,
		queryFn: () => notifications.getNotificationEmail(),
	}));
	const notificationSettingsQuery = createQuery(() => ({
		queryKey: ['notification-settings'],
		enabled: portfolioId > 0,
		queryFn: () => notifications.getSettings(),
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
	const hasNotificationEmail = $derived(!!notificationEmailQuery.data?.email);
	const hasTexting = $derived(
		notificationSettingsQuery.data?.smsCredentialASet === true ||
			notificationSettingsQuery.data?.smsCredentialBSet === true
	);

	// A step counts as "done" if the wizard handled it OR data already exists.
	const stepDone = $derived<Record<WizardStepKey, boolean>>({
		portfolio: portfolioSaved || !!portfolioQuery.data?.name,
		owner: !!createdOwner || hasExistingOwners,
		property: !!createdProperty || hasExistingProperties,
		tenants: createdTenants.length > 0 || hasExistingTenants,
		lease: createdLease || hasExistingLeases,
		notifications: notificationsSaved || hasNotificationEmail,
		texting: textingSaved || hasTexting,
	});

	// A1: the real "set-up spine" — a property, a tenant, and a lease all exist. The wizard lets a user
	// Skip straight through, so we only show the "You're all set!" celebration when this is truly true;
	// otherwise we tell the truth ("You can finish anytime") and point back to what's left.
	const coreSpineComplete = $derived(stepDone.property && stepDone.tenants && stepDone.lease);

	// ---------------------------------------------------------------------------
	// Initial positioning. Priority:
	//   1. ?step=<key> in the URL (deep-link from Settings / dashboard) — honored verbatim.
	//   2. otherwise resume the persisted step if it isn't already done.
	//   3. otherwise jump to the first incomplete CORE step.
	// Runs once after detection settles so manual back/next work afterward.
	// ---------------------------------------------------------------------------
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
		if (sandboxQuery.data?.isSandbox === true) return;
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
	// Step: Property + Units. Split into sub-steps so the user sees 1–2 fields at a
	// time instead of one dense form: address → details → units.
	// ---------------------------------------------------------------------------
	const PROPERTY_SUBSTEPS = ['address', 'details', 'units'] as const;
	type PropertySubstep = (typeof PROPERTY_SUBSTEPS)[number];
	let propertySub = $state<PropertySubstep>('address');
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
		mutationFn: async (vars: { property: Record<string, unknown>; units: Record<string, unknown>[] }) => {
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
			propertySub = 'address';
			next();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// Validate just the address fields before advancing the property sub-step.
	function propertyAddressValid(): boolean {
		const errs: Record<string, string> = {};
		if (!propertyForm.name.trim()) errs.name = 'Name is required';
		if (!propertyForm.addressLine1.trim()) errs.addressLine1 = 'Address is required';
		if (!propertyForm.city.trim()) errs.city = 'City is required';
		if (!propertyForm.state.trim()) errs.state = 'State is required';
		if (!propertyForm.postalCode.trim()) errs.postalCode = 'ZIP is required';
		propertyErrors = errs;
		return Object.keys(errs).length === 0;
	}

	function submitProperty() {
		const propResult = parseForm(
			propertySchema,
			buildOnboardingPropertyPayload({
				propertyForm,
				createdOwner,
				existingOwners: ownersQuery.data ?? []
			})
		);
		if (propResult.errors) {
			propertyErrors = propResult.errors;
			propertySub = 'address';
			return;
		}
		propertyErrors = {};

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

		savePropertyMutation.mutate({ property: { portfolioId, ...propResult.data }, units: validUnits });
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
	const isoDate = (d: Date) => d.toISOString().slice(0, 10);
	const rentTrackingStartOptions = [
		{ value: 'ForwardOnly', label: 'Start from today' },
		{ value: 'BackfillFromLeaseStart', label: 'Backfill from lease start' },
		{ value: 'CustomCutoffDate', label: 'Use cutoff date' }
	] as const;
	const rentTrackingStartLabel = (value: string) =>
		rentTrackingStartOptions.find((option) => option.value === value)?.label ?? 'Select start';

	let leaseForm = $state({
		tenantId: '',
		propertyId: '',
		unitId: '',
		startDate: isoDate(today),
		endDate: isoDate(oneYear),
		monthlyRent: '',
		securityDeposit: '',
		lateFeeAmount: '0',
		leaseNumber: '',
		rentDueDay: '1',
		rentTrackingStartMode: 'ForwardOnly',
		rentTrackingStartDate: '',
	});
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

	const leaseProperties = $derived(propertiesQuery.data ?? []);
	const leaseTenants = $derived(tenantsQuery.data ?? []);

	const leaseUnitsQuery = createQuery(() => ({
		queryKey: ['onboarding-units', leaseForm.propertyId],
		enabled: !!leaseForm.propertyId,
		queryFn: () => properties.listUnits(Number(leaseForm.propertyId)),
	}));

	$effect(() => {
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
		const list = leaseUnitsQuery.data ?? [];
		if (!leaseForm.unitId && list.length > 0) {
			const preferred = createdUnits.find((u) => list.some((l) => l.id === u.id)) ?? list[0];
			leaseForm.unitId = String(preferred.id);
			if (!leaseForm.monthlyRent && preferred.marketRent != null) {
				leaseForm.monthlyRent = String(preferred.marketRent);
			}
		}
	});
	$effect(() => {
		if (leaseForm.rentTrackingStartMode !== 'CustomCutoffDate' && leaseForm.rentTrackingStartDate) {
			leaseForm.rentTrackingStartDate = '';
		}
	});

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
		if (values.startDate) leaseForm.startDate = values.startDate;
		if (values.endDate) leaseForm.endDate = values.endDate;
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
			if (prefillDraftId != null) {
				return scan.confirm(prefillDraftId, buildOnboardingLeaseScanOverrides(data));
			}

			return leases.create(data as unknown as Record<string, unknown>);
		},
		onSuccess: () => {
			createdLease = true;
			leasePrefillDraftId = null;
			showSuccess('Lease created.');
			queryClient.invalidateQueries({ queryKey: ['leases', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['scans'] });
			next();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitLease() {
		const unit = (leaseUnitsQuery.data ?? []).find((u) => String(u.id) === leaseForm.unitId);
		const stamp = leaseForm.startDate.replace(/-/g, '');
		const leaseNumber = leaseForm.leaseNumber.trim() || `L-${unit?.unitNumber ?? leaseForm.unitId}-${stamp}`;
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
			rentTrackingStartMode: leaseForm.rentTrackingStartMode,
			rentTrackingStartDate: leaseForm.rentTrackingStartDate,
			status: 'Active',
			notes: '',
		});
		const rentTrackingErrors = leaseRentTrackingErrors({ ...leaseForm, status: 'Active' });
		if (result.errors || Object.keys(rentTrackingErrors).length > 0) {
			leaseErrors = { ...(result.errors ?? {}), ...rentTrackingErrors };
			return;
		}
		leaseErrors = {};
		saveLeaseMutation.mutate({
			data: { portfolioId, ...result.data },
			prefillDraftId: leasePrefillDraftId
		});
	}

	// ---------------------------------------------------------------------------
	// Step: Notifications (email alerts) — writes the same endpoint as Settings.
	// ---------------------------------------------------------------------------
	let notificationEmail = $state('');
	let notificationEmailPrefilled = false;
	$effect(() => {
		if (notificationEmailQuery.data && !notificationEmailPrefilled) {
			notificationEmailPrefilled = true;
			notificationEmail = notificationEmailQuery.data.email ?? '';
		}
	});
	const saveNotificationEmailMutation = createMutation(() => ({
		mutationFn: () => notifications.setNotificationEmail(notificationEmail.trim() || null),
		onSuccess: () => {
			notificationsSaved = true;
			showSuccess('Alert email saved.');
			queryClient.invalidateQueries({ queryKey: ['notification-email', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['portfolio', portfolioId] });
			next();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ---------------------------------------------------------------------------
	// Step: Texting (SignalWire) — writes the same endpoint as Settings. Only the
	// provider connection fields; keeps every other notification setting intact by
	// round-tripping the current settings payload.
	// ---------------------------------------------------------------------------
	let textingForm = $state({ projectId: '', spaceUrl: '', fromNumber: '' });
	let textingToken = $state('');
	let textingPrefilled = false;
	$effect(() => {
		const d = notificationSettingsQuery.data;
		if (d && !textingPrefilled) {
			textingPrefilled = true;
			// Credentials are write-only on the wire (only *Set booleans come back),
			// so the from-number is the only prefillable connection field.
			textingForm = {
				projectId: '',
				spaceUrl: '',
				fromNumber: d.smsFromNumber ?? '',
			};
		}
	});
	const saveTextingMutation = createMutation(() => ({
		mutationFn: () => {
			const d = notificationSettingsQuery.data;
			if (!d) throw new Error('Settings not loaded yet.');
			return notifications.setSettings({
				enableRentCharges: d.enableRentCharges,
				enableLateFees: d.enableLateFees,
				enableLeaseExpiryReminders: d.enableLeaseExpiryReminders,
				notifyTenants: d.notifyTenants,
				rentChargeLeadDays: d.rentChargeLeadDays,
				lateFeeGraceDays: d.lateFeeGraceDays,
				leaseExpiryReminderDays: d.leaseExpiryReminderDays,
				enableDailyBriefingMessages: d.enableDailyBriefingMessages,
				dailyBriefingSendHourLocal: d.dailyBriefingSendHourLocal,
				dailyBriefingIncludeEmpty: d.dailyBriefingIncludeEmpty,
				dailyBriefingSmsRecipients: d.dailyBriefingSmsRecipients,
				dailyBriefingEmailRecipients: d.dailyBriefingEmailRecipients,
				// Wizard's texting step is SignalWire-guided; slots per SmsProviderMeta:
				// A = Project ID, B = API Token, C = Space URL. undefined = keep saved secret.
				smsProvider: 'SignalWire',
				smsFromNumber: textingForm.fromNumber.trim() || null,
				smsCredentialA: textingForm.projectId.trim() || undefined,
				smsCredentialB: textingToken.length > 0 ? textingToken : undefined,
				smsCredentialC: textingForm.spaceUrl.trim() || undefined,
				autoSendRentReminder: d.autoSendRentReminder,
				autoSendLateRent: d.autoSendLateRent,
				leaseEndAutoAction: d.leaseEndAutoAction,
				channelPreferences: d.channelPreferences ?? [],
			});
		},
		onSuccess: () => {
			textingSaved = true;
			textingToken = '';
			showSuccess('Texting connected.');
			queryClient.invalidateQueries({ queryKey: ['notification-settings'] });
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

	function next() {
		// Deep-linked from a Settings section → return there once the step is handled.
		if (fromParam === 'settings' && currentStep.settingsAnchor) {
			goto(`/settings#${currentStep.settingsAnchor}`);
			return;
		}
		// The last CORE step (lease) is the natural "done" point — show the celebration and offer the
		// optional provider steps from there rather than forcing the user through them.
		if (currentStep.key === 'lease') {
			finishFlow();
			return;
		}
		if (shouldFinishAfterOptionalStep({
			currentStepKey: currentStep.key,
			returnToFinishAfterOptional,
		})) {
			finishFlow();
			return;
		}
		if (stepIndex < STEPS.length - 1) {
			stepIndex += 1;
		} else {
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
		}
	}
	function skip() {
		next();
	}
	function goToStep(key: WizardStepKey) {
		stepIndex = STEPS.findIndex((s) => s.key === key);
	}
	function openOptionalStepFromFinished(key: WizardStepKey) {
		returnToFinishAfterOptional = true;
		finished = false;
		goToStep(key);
	}

	const anyPending = $derived(
		savePortfolioMutation.isPending ||
			saveOwnerMutation.isPending ||
			savePropertyMutation.isPending ||
			saveTenantsMutation.isPending ||
			saveLeaseMutation.isPending ||
			saveNotificationEmailMutation.isPending ||
			saveTextingMutation.isPending
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

	// Which steps show in the progress rail. The optional provider steps appear after the core ones
	// but are visually marked as optional.
	const coreCount = $derived(CORE_WIZARD_STEPS.length);
	const leaseBlocked = $derived(leaseProperties.length === 0 || leaseTenants.length === 0);
</script>

<svelte:head>
	<title>Set up your portfolio - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto bg-muted/30 p-6 pb-20" data-testid="onboarding-page">
	<div class="mx-auto max-w-2xl">
		{#if finished}
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
						{#if !stepDone.notifications}
							<Button variant="outline" class="gap-2" data-testid="onboarding-setup-notifications" onclick={() => openOptionalStepFromFinished('notifications')}>
								<Bell class="h-4 w-4" />
								Set up email alerts
							</Button>
						{/if}
						{#if !stepDone.texting}
							<Button variant="outline" class="gap-2" data-testid="onboarding-setup-texting" onclick={() => openOptionalStepFromFinished('texting')}>
								<MessageSquare class="h-4 w-4" />
								Turn on texting
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

			<!-- Scan/import shortcuts -->
			<div
				class="mb-4 grid gap-3 rounded-md border border-border bg-muted/40 p-3 sm:grid-cols-2"
				data-testid="onboarding-setup-shortcuts"
			>
				{#each ONBOARDING_SETUP_SHORTCUTS as shortcut (shortcut.key)}
					<div class="flex items-center justify-between gap-3">
						<div class="min-w-0">
							<p class="text-sm font-semibold text-foreground">{shortcut.label}</p>
							<p class="text-xs leading-relaxed text-muted-foreground">{shortcut.description}</p>
						</div>
						<Button
							variant={shortcut.primary ? 'default' : 'outline'}
							size="sm"
							class="shrink-0 gap-1.5"
							href={shortcut.href}
							data-testid={`onboarding-${shortcut.key}`}
						>
							{#if shortcut.key === 'scan-new-rental'}
								<ScanLine class="h-4 w-4" />
							{:else}
								<FileSpreadsheet class="h-4 w-4" />
							{/if}
							{shortcut.label}
						</Button>
					</div>
				{/each}
			</div>

			<!-- Step indicator (core steps + optional add-ons) -->
			<div class="mb-4" data-testid="onboarding-steps">
				<Progress value={Math.min(stepIndex + 1, coreCount)} max={coreCount} class="mb-3" />
				<div class="flex items-center justify-between gap-1">
					{#each STEPS as step (step.key)}
						{@const active = step.key === currentStep.key}
						{@const done = stepDone[step.key]}
						{@const StepIcon = STEP_ICONS[step.icon]}
						<button
							type="button"
							class="flex flex-1 flex-col items-center gap-1 text-center"
							data-testid="onboarding-step-tab-{step.key}"
							onclick={() => goToStep(step.key)}
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
									<StepIcon class="h-4 w-4" />
								{/if}
							</span>
							<span class="text-[11px] {active ? 'font-medium text-foreground' : 'text-muted-foreground'}">
								{step.label}{#if !step.core}<span class="block text-[9px] text-muted-foreground/70">optional</span>{/if}
							</span>
						</button>
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
									<Input id="ob-portfolio-name" data-testid="onboarding-portfolio-name" bind:value={portfolioForm.name} placeholder="e.g. Smith Family Rentals" />
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
									You already have {ownersQuery.data?.length} owner{(ownersQuery.data?.length ?? 0) === 1 ? '' : 's'} on file. You can add another or skip ahead.
								</div>
							{:else if ownerPrefilled}
								<div class="mb-4 rounded-md border border-primary/40 bg-primary/10 px-3 py-2 text-sm text-foreground" data-testid="onboarding-owner-prefilled">
									We filled this in from your account — just confirm it's right. Own through an LLC or trust? Change the name and type below.
								</div>
							{/if}
							<div class="grid gap-4 sm:grid-cols-2">
								<div class="sm:col-span-2">
									<label for="ob-owner-name" class="mb-1 block text-xs font-medium text-muted-foreground">Owner name</label>
									<Input id="ob-owner-name" data-testid="onboarding-owner-name" bind:value={ownerForm.name} placeholder="e.g. John Smith or Smith Holdings LLC" />
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
									<Input id="ob-owner-email" data-testid="onboarding-owner-email" bind:value={ownerForm.email} placeholder="owner@example.com" />
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
									You already have {propertiesQuery.data?.length} propert{(propertiesQuery.data?.length ?? 0) === 1 ? 'y' : 'ies'}. Add another or skip ahead.
								</div>
							{/if}

							{#if propertySub === 'address'}
								<div class="grid gap-4" data-testid="onboarding-property-sub-address">
									<div>
										<label for="ob-prop-name" class="mb-1 block text-xs font-medium text-muted-foreground">Property name</label>
										<Input id="ob-prop-name" data-testid="onboarding-property-name" bind:value={propertyForm.name} placeholder="e.g. 123 Main St Duplex" />
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
											<Input id="ob-prop-zip" data-testid="onboarding-property-zip" bind:value={propertyForm.postalCode} placeholder="ZIP" />
											{#if propertyErrors.postalCode}<p class="mt-1 text-xs text-destructive">{propertyErrors.postalCode}</p>{/if}
										</div>
									</div>
								</div>
							{:else if propertySub === 'details'}
								<div class="grid gap-4" data-testid="onboarding-property-sub-details">
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
									<h3 class="mb-1 text-sm font-semibold">Units</h3>
									<p class="mb-3 text-xs text-muted-foreground">A house is one unit; a duplex is two. Add a row per unit — or leave blank and add them later.</p>
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
														<Input type="number" min="0" step="1" data-testid="onboarding-unit-beds-{i}" bind:value={row.bedrooms} placeholder="2" />
														{#if unitRowErrors[i]?.bedrooms}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].bedrooms}</p>{/if}
													</div>
													<div>
														<span class="mb-1 block text-[11px] text-muted-foreground">Baths</span>
														<Input type="number" min="0" step="0.5" data-testid="onboarding-unit-baths-{i}" bind:value={row.bathrooms} placeholder="1" />
														{#if unitRowErrors[i]?.bathrooms}<p class="mt-1 text-[11px] text-destructive">{unitRowErrors[i].bathrooms}</p>{/if}
													</div>
													<div>
														<span class="mb-1 block text-[11px] text-muted-foreground">Market rent</span>
														<div class="flex items-center gap-1">
															<Input type="number" min="0" step="0.01" data-testid="onboarding-unit-rent-{i}" bind:value={row.marketRent} placeholder="1500" />
															{#if unitRows.length > 1}
																<button type="button" class="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-destructive/10 hover:text-destructive" aria-label="Remove unit" data-testid="onboarding-unit-remove-{i}" onclick={() => removeUnitRow(i)}>
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
							{/if}
						</WizardStepScaffold>

					<!-- ============ Tenants ============ -->
					{:else if currentStep.key === 'tenants'}
						<WizardStepScaffold step={currentStep}>
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
													<Input data-testid="onboarding-tenant-phone-{i}" bind:value={row.phone} placeholder="(555) 555-5555" oninput={(e) => { row.phone = formatPhoneInput((e.target as HTMLInputElement).value); }} />
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
										<span class="mb-1 block text-xs font-medium text-muted-foreground">Tenant</span>
										<Select.Root type="single" bind:value={leaseForm.tenantId}>
											<Select.Trigger class="w-full" data-testid="onboarding-lease-tenant">{leaseTenantLabel}</Select.Trigger>
											<Select.Content>
												{#each leaseTenants as t}
													<Select.Item value={String(t.id)} label={t.fullName || `${t.firstName} ${t.lastName}`}>{t.fullName || `${t.firstName} ${t.lastName}`}</Select.Item>
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
											<Select.Trigger class="w-full" data-testid="onboarding-lease-unit" disabled={!leaseForm.propertyId}>{leaseUnitLabel}</Select.Trigger>
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
										<label for="ob-lease-dueday" class="mb-1 flex items-center gap-1 text-xs font-medium text-muted-foreground">Rent due day<HelpPopover title="Rent due day" summary="The day of the month rent is expected — e.g. 1 means the 1st of each month. The system posts rent charges and calculates late fees based on this date." testid="help-rent-due-day" /></label>
										<Input id="ob-lease-dueday" data-testid="onboarding-lease-dueday" bind:value={leaseForm.rentDueDay} placeholder="1" />
										{#if leaseErrors.rentDueDay}<p class="mt-1 text-xs text-destructive">{leaseErrors.rentDueDay}</p>{/if}
									</div>
									<div class={leaseForm.rentTrackingStartMode === 'CustomCutoffDate' ? '' : 'sm:col-span-2'}>
										<span class="mb-1 flex items-center gap-1 text-xs font-medium text-muted-foreground">Rent tracking start<HelpPopover title="Rent tracking start" summary="Controls when the system starts generating rent records for this lease." detail="Start from today: only future months are tracked (good for new leases). Backfill from lease start: creates past records back to the lease start date (use when catching up). Use cutoff date: you choose a specific date to start from, useful if you've already been tracking rent elsewhere and want to pick up mid-lease." testid="help-rent-tracking-start" /></span>
										<Select.Root type="single" bind:value={leaseForm.rentTrackingStartMode}>
											<Select.Trigger class="w-full" data-testid="onboarding-lease-rent-tracking-mode">{rentTrackingStartLabel(leaseForm.rentTrackingStartMode)}</Select.Trigger>
											<Select.Content>
												{#each rentTrackingStartOptions as option}
													<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
												{/each}
											</Select.Content>
										</Select.Root>
									</div>
									{#if leaseForm.rentTrackingStartMode === 'CustomCutoffDate'}
										<div>
											<label for="ob-lease-rent-tracking-date" class="mb-1 block text-xs font-medium text-muted-foreground">Cutoff date</label>
											<DatePicker id="ob-lease-rent-tracking-date" testid="onboarding-lease-rent-tracking-date" bind:value={leaseForm.rentTrackingStartDate} placeholder="Cutoff date" min={leaseForm.startDate || undefined} />
											{#if leaseErrors.rentTrackingStartDate}<p class="mt-1 text-xs text-destructive">{leaseErrors.rentTrackingStartDate}</p>{/if}
										</div>
									{/if}
								</div>
							{/if}
						</WizardStepScaffold>

					<!-- ============ Notifications (email alerts) ============ -->
					{:else if currentStep.key === 'notifications'}
						<WizardStepScaffold step={currentStep}>
							<div>
								<label for="ob-notif-email" class="mb-1 block text-xs font-medium text-muted-foreground">Alert email <span class="font-normal">(optional)</span></label>
								<Input id="ob-notif-email" type="email" data-testid="onboarding-notification-email" bind:value={notificationEmail} placeholder="your-email@example.com" />
								<p class="mt-1 text-xs text-muted-foreground">Leave blank to use your login email.</p>
							</div>
						</WizardStepScaffold>

					<!-- ============ Texting (SignalWire) ============ -->
					{:else if currentStep.key === 'texting'}
						<WizardStepScaffold step={currentStep}>
							{#if hasTexting}
								<div class="mb-4 rounded-md border border-success/40 bg-success/10 px-3 py-2 text-sm text-foreground" data-testid="onboarding-texting-existing">
									Texting is already connected. You can update the details below or skip.
								</div>
							{/if}
							<div class="grid gap-4 sm:grid-cols-2">
								<div>
									<label for="ob-sw-project" class="mb-1 block text-xs font-medium text-muted-foreground">Project ID</label>
									<Input id="ob-sw-project" autocomplete="off" data-testid="onboarding-texting-project" bind:value={textingForm.projectId} />
								</div>
								<div>
									<label for="ob-sw-space" class="mb-1 block text-xs font-medium text-muted-foreground">Space URL</label>
									<Input id="ob-sw-space" autocomplete="off" data-testid="onboarding-texting-space" bind:value={textingForm.spaceUrl} placeholder="your-space.signalwire.com" />
								</div>
								<div>
									<label for="ob-sw-from" class="mb-1 block text-xs font-medium text-muted-foreground">From number</label>
									<Input id="ob-sw-from" autocomplete="off" data-testid="onboarding-texting-from" bind:value={textingForm.fromNumber} placeholder="+13302933081" />
								</div>
								<div>
									<label for="ob-sw-token" class="mb-1 block text-xs font-medium text-muted-foreground">
										API Token {notificationSettingsQuery.data?.smsCredentialBSet ? '(saved)' : ''}
									</label>
									<Input id="ob-sw-token" type="password" autocomplete="new-password" data-testid="onboarding-texting-token" bind:value={textingToken} placeholder={notificationSettingsQuery.data?.smsCredentialBSet ? 'Leave blank to keep saved token' : 'Paste API token'} />
								</div>
							</div>
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
							<Button class="gap-1" data-testid="onboarding-property-next-sub" disabled={anyPending} onclick={() => { if (propertyAddressValid()) propertySub = 'details'; }}>
								Next <ArrowRight class="h-4 w-4" />
							</Button>
						{:else if propertySub === 'details'}
							<Button class="gap-1" data-testid="onboarding-property-next-sub" disabled={anyPending} onclick={() => (propertySub = 'units')}>
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
							<Button class="gap-1" data-testid="onboarding-finish" disabled={anyPending} onclick={submitLease}>
								{saveLeaseMutation.isPending ? 'Creating…' : 'Create lease & finish'}
								<CheckCircle2 class="h-4 w-4" />
							</Button>
						{/if}
					{:else if currentStep.key === 'notifications'}
						<Button class="gap-1" data-testid="onboarding-next" disabled={anyPending} onclick={() => saveNotificationEmailMutation.mutate()}>
							{saveNotificationEmailMutation.isPending ? 'Saving…' : 'Save & continue'}
							<ArrowRight class="h-4 w-4" />
						</Button>
					{:else if currentStep.key === 'texting'}
						<Button class="gap-1" data-testid="onboarding-next" disabled={anyPending || notificationSettingsQuery.isLoading} onclick={() => saveTextingMutation.mutate()}>
							{saveTextingMutation.isPending ? 'Saving…' : 'Save & continue'}
							<ArrowRight class="h-4 w-4" />
						</Button>
					{/if}
				</div>
			</div>
		{/if}
	</div>
</div>
