<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { toast } from 'svelte-sonner';
	import { scan } from '$lib/api/scan';
	import { properties } from '$lib/api/endpoints/properties';
	import { units } from '$lib/api/endpoints/units';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import { Loader2 } from '@lucide/svelte';
	import FileDrop from '$lib/components/FileDrop.svelte';
	import PropertyFields from '$lib/components/forms/PropertyFields.svelte';
	import UnitFields from '$lib/components/forms/UnitFields.svelte';
	import TenantFields from '$lib/components/forms/TenantFields.svelte';
	import LeaseTermFields from '$lib/components/forms/LeaseTermFields.svelte';
	import LeaseScanSignatureChoice, { type LeaseScanReviewDisposition } from './LeaseScanSignatureChoice.svelte';
	import { propertySchema, unitSchema, tenantSchema, leaseSchema, parseForm } from '$lib/schemas';
	import { toLeasePrefill, type PrefillConfidence } from '$lib/scan/lease-prefill';
	import { createNewRentalPropertyForm, findNewRentalExistingUnitId, formatNewRentalStepLabel, formatNewRentalStepPosition, newRentalDraftUrl, seedNewRentalLateFeeAmount, type NewRentalPhase } from '$lib/scan/new-rental-state';
	import { prepareNewRentalPhotoUpload } from '$lib/scan/new-rental-upload';
	import { showError, apiErrorMessage } from '$lib/utils/toast';
	import { defaultLeaseNumber } from '$lib/leases/lease-number';

	interface Props {
		/** Portfolio the new rental belongs to. */
		portfolioId: number;
		/** Resume an in-progress draft (the page passes the URL draftId; the wizard passes null to start at capture). */
		initialDraftId?: number | null;
		/** Keep the standalone scan page reloadable/bookmarkable while avoiding URL churn in embedded onboarding. */
		syncDraftToUrl?: boolean;
		/** Called on a SUCCESSFUL scan.confirm. The parent decides where to navigate. */
		oncomplete: (result: { leaseManagementId?: number | null; agreementId?: number | null; propertyId?: number | null; unitId?: number | null; tenantId?: number | null }) => void;
		/** Called when an embedded parent wants to close the capture flow without saving. */
		oncancel?: () => void;
	}

	let { portfolioId, initialDraftId = null, syncDraftToUrl = false, oncomplete, oncancel }: Props = $props();

	// ----- phase: capture -> processing -> steps -> review -> done -----
	// `initialDraftId` is an initial seed only (resume an in-progress draft); later prop
	// changes are intentionally ignored — the flow drives its own phase/draftId from here on.
	// svelte-ignore state_referenced_locally
	let phase = $state<NewRentalPhase>(initialDraftId == null ? 'capture' : 'processing');
	// svelte-ignore state_referenced_locally
	let draftId = $state<number | null>(initialDraftId);
	let confidence = $state<PrefillConfidence>({});

	// Step machine. 0=Property 1=Unit 2=Tenant 3=Lease 4=Review.
	let step = $state(0);
	const STEP_LABELS = ['Property', 'Unit', 'Tenant', 'Lease'];
	const TOTAL = STEP_LABELS.length;

	// ----- the four step forms (string-bound, schema-validated) -----
	let propertyForm = $state(createNewRentalPropertyForm());
	let unitForm = $state({ unitNumber: '', floorPlan: '', bedrooms: '', bathrooms: '', squareFeet: '', marketRent: '', notes: '' });
	let tenantForm = $state({ firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' });
	let leaseForm = $state({
		leaseNumber: defaultLeaseNumber(),
		startDate: '',
		endDate: '',
		monthlyRent: '',
		securityDeposit: '',
		lateFeeAmount: '',
		rentDueDay: '1',
		status: 'Active',
		notes: ''
	});

	let propertyErrors = $state<Record<string, string>>({});
	let unitErrors = $state<Record<string, string>>({});
	let tenantErrors = $state<Record<string, string>>({});
	let leaseErrors = $state<Record<string, string>>({});
	let reviewDisposition = $state<LeaseScanReviewDisposition | ''>('');
	let documentTemplateId = $state('');
	let possessionGivenOn = $state('');
	let rentTrackingStartMode = $state<
		'ForwardOnly' | 'BackfillFromLeaseStart' | 'CustomCutoffDate'
	>('ForwardOnly');
	let rentTrackingStartOn = $state('');

	// Which step-form fields were auto-filled (drives the "from your lease" badge).
	let autoFilled = $state<Set<string>>(new Set());

	// ----- Property duplicate-guard: link an existing in-portfolio property, or create new -----
	const CREATE = '__create__';
	let propertyChoice = $state<string>(CREATE); // a real id string => link; CREATE => create-new
	let unitChoice = $state<string>(CREATE);      // existing unit id, or CREATE
	let tenantChoice = $state<string>(CREATE);    // existing tenant id, or CREATE
	let selectedPropertyLabel = $state('');
	let selectedUnitLabel = $state('');
	let selectedTenantLabel = $state('');
	const isCreatingProperty = $derived(propertyChoice === CREATE);
	const isCreatingTenant = $derived(tenantChoice === CREATE);

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId, 'new-rental-candidate', propertyForm.name, propertyForm.addressLine1],
		queryFn: () => properties.listPage(portfolioId, { search: propertyForm.name || propertyForm.addressLine1, skip: 0, take: 20, sort: 'name' }),
		enabled: phase === 'steps'
	}));
	const unitsQuery = createQuery(() => ({
		queryKey: ['units-for-new-rental', propertyChoice, unitForm.unitNumber],
		queryFn: () => units.listWithHealthPage({ propertyId: Number(propertyChoice), search: unitForm.unitNumber, skip: 0, take: 20, sort: 'unitNumber' }),
		enabled: phase === 'steps' && propertyChoice !== CREATE && !!propertyChoice
	}));
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId, 'new-rental-candidate', tenantForm.firstName, tenantForm.lastName],
		queryFn: () => tenants.listPage(portfolioId, { search: `${tenantForm.firstName} ${tenantForm.lastName}`.trim(), skip: 0, take: 20, sort: 'lastName' }),
		enabled: phase === 'steps'
	}));

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

	async function loadUnitOptions(params: { search?: string; skip: number; take: number }) {
		const result = await units.listWithHealthPage({
			...params,
			propertyId: Number(propertyChoice),
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

	function resetReviewStateForDraft() {
		confidence = {};
		step = 0;
		propertyForm = createNewRentalPropertyForm();
		unitForm = { unitNumber: '', floorPlan: '', bedrooms: '', bathrooms: '', squareFeet: '', marketRent: '', notes: '' };
		tenantForm = { firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' };
		leaseForm = {
			leaseNumber: defaultLeaseNumber(),
			startDate: '',
			endDate: '',
			monthlyRent: '',
			securityDeposit: '',
			lateFeeAmount: '',
			rentDueDay: '1',
			status: 'Active',
			notes: ''
		};
		propertyErrors = {};
		unitErrors = {};
		tenantErrors = {};
		leaseErrors = {};
		reviewDisposition = '';
		documentTemplateId = '';
		possessionGivenOn = '';
		rentTrackingStartMode = 'ForwardOnly';
		rentTrackingStartOn = '';
		autoFilled = new Set();
		propertyChoice = CREATE;
		unitChoice = CREATE;
		tenantChoice = CREATE;
		selectedPropertyLabel = '';
		selectedUnitLabel = '';
		selectedTenantLabel = '';
		seeded = false;
		unitChoiceSeeded = false;
	}

	// ----- upload: one PDF passes straight through; many photos are stitched first -----
	const uploadOne = createMutation(() => ({
		mutationFn: (file: File) => scan.upload(file, 'LeaseAgreement'),
		onSuccess: (res) => {
			resetReviewStateForDraft();
			draftId = res.draftId;
			phase = 'processing';
			if (syncDraftToUrl) {
				void goto(newRentalDraftUrl(res.draftId), { replaceState: true, noScroll: true, keepFocus: true });
			}
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not read that file. Try a clearer photo or the PDF.'))
	}));

	async function onPdf(file: File) {
		uploadOne.mutate(file);
	}
	async function onPhotos(files: File[]) {
		try {
			uploadOne.mutate(await prepareNewRentalPhotoUpload(files));
		} catch {
			showError('Could not combine those photos. Try fewer, clearer shots or a PDF.');
		}
	}

	// poll the draft until Reviewing, then seed the forms
	const draftQuery = createQuery(() => ({
		queryKey: ['new-rental-draft', draftId],
		enabled: draftId != null && phase === 'processing',
		queryFn: () => scan.get(draftId as number),
		refetchInterval: (q) => {
			const s = q.state.data?.status;
			return s === 'Pending' || s === 'Processing' ? 1500 : false;
		}
	}));

	let seeded = $state(false);
	let unitChoiceSeeded = $state(false);
	$effect(() => {
		const d = draftQuery.data;
		if (!d || phase !== 'processing') return;
		if (d.status === 'Failed' || d.status === 'Rejected') {
			showError('We could not read that document. Try a clearer photo or the PDF.');
			phase = 'capture';
			draftId = null;
			if (syncDraftToUrl) {
				void goto('/scan/new-rental', { replaceState: true, noScroll: true, keepFocus: true });
			}
			return;
		}
		if (d.status !== 'Reviewing' || seeded) return;
		const { values, confidence: conf } = toLeasePrefill(d.fields);
		confidence = conf;
		// seed property
		propertyForm.name = values.propertyName;
		propertyForm.addressLine1 = values.propertyAddress;
		propertyForm.city = values.propertyCity;
		propertyForm.state = values.propertyState;
		propertyForm.postalCode = values.propertyPostalCode;
		// seed unit
		unitForm.unitNumber = values.unitNumber || '1';
		unitForm.bedrooms = values.unitBedrooms;
		unitForm.bathrooms = values.unitBathrooms;
		unitForm.marketRent = values.monthlyRent || '0';
		unitForm.squareFeet = values.unitSquareFeet;
		// seed tenant (split on last space)
		if (values.tenantName) {
			const parts = values.tenantName.trim().split(/\s+/);
			tenantForm.firstName = parts.length > 1 ? parts.slice(0, -1).join(' ') : parts[0];
			tenantForm.lastName = parts.length > 1 ? parts[parts.length - 1] : '';
		}
		tenantForm.email = values.tenantEmail;
		tenantForm.phone = values.tenantPhone;
		tenantForm.emergencyContact = values.tenantEmergencyContact;
		// seed lease terms
		leaseForm.leaseNumber = values.leaseNumber || defaultLeaseNumber(new Date(values.startDate || Date.now()));
		leaseForm.startDate = values.startDate;
		leaseForm.endDate = values.endDate;
		possessionGivenOn = values.possessionGivenOn;
		leaseForm.monthlyRent = values.monthlyRent;
		leaseForm.securityDeposit = values.securityDeposit;
		leaseForm.lateFeeAmount = seedNewRentalLateFeeAmount(values.lateFee);
		leaseForm.rentDueDay = values.rentDueDay || '1';
		// auto-fill badge set
		const af = new Set<string>();
		if (values.propertyName) af.add('name');
		if (values.propertyAddress) af.add('addressLine1');
		if (values.propertyCity) af.add('city');
		if (values.propertyState) af.add('state');
		if (values.propertyPostalCode) af.add('postalCode');
		if (values.unitNumber) af.add('unitNumber');
		if (values.unitBedrooms) af.add('bedrooms');
		if (values.unitBathrooms) af.add('bathrooms');
		if (values.tenantName) { af.add('firstName'); af.add('lastName'); }
		if (values.tenantEmail) af.add('email');
		if (values.tenantPhone) af.add('phone');
		if (values.tenantEmergencyContact) af.add('emergencyContact');
		if (values.leaseNumber) af.add('leaseNumber');
		if (values.startDate) af.add('startDate');
		if (values.endDate) af.add('endDate');
		if (values.monthlyRent) af.add('monthlyRent');
		if (values.securityDeposit) af.add('securityDeposit');
		if (values.lateFee) af.add('lateFeeAmount');
		if (values.rentDueDay) af.add('rentDueDay');
		autoFilled = af;
		// duplicate-guard default from the proposal
		const prop = d.leaseProposal?.property;
		if (prop?.action === 'link' && prop.existingId != null) propertyChoice = String(prop.existingId);
		else propertyChoice = CREATE;
		unitChoice = CREATE;
		tenantChoice = CREATE;
		unitChoiceSeeded = false;
		seeded = true;
		phase = 'steps';
		step = 0;
	});
	$effect(() => {
		if (phase !== 'steps' || isCreatingProperty || unitChoiceSeeded || unitChoice !== CREATE) return;
		const unitId = findNewRentalExistingUnitId(
			unitForm.unitNumber,
			draftQuery.data?.leaseProposal?.unit,
			unitsQuery.data?.items ?? []
		);
		if (!unitId) return;
		unitChoice = unitId;
		selectedUnitLabel = `Unit ${unitsQuery.data?.items.find((unit) => String(unit.id) === unitId)?.unitNumber ?? unitId}`;
		unitChoiceSeeded = true;
	});

	// ----- step navigation with per-step validation (AC-1) -----
	function validateStep(i: number): boolean {
		if (i === 0) {
			if (isCreatingProperty) {
				const r = parseForm(propertySchema, propertyForm);
				propertyErrors = r.errors ?? {};
				return !r.errors;
			}
			propertyErrors = {};
			return !!propertyChoice; // linking an existing property: no field validation
		}
		if (i === 1) {
			if (!isCreatingProperty && unitChoice !== CREATE) { unitErrors = {}; return !!unitChoice; }
			const r = parseForm(unitSchema, unitForm);
			unitErrors = r.errors ?? {};
			return !r.errors;
		}
		if (i === 2) {
			if (tenantChoice !== CREATE) { tenantErrors = {}; return !!tenantChoice; }
			const r = parseForm(tenantSchema, tenantForm);
			tenantErrors = r.errors ?? {};
			return !r.errors;
		}
		if (i === 3) {
			// validate lease terms minus the picker ids (resolved from prior steps at confirm)
			const probe = { ...leaseForm, propertyId: '1', unitId: '1', tenantId: '1', moveInDate: '' };
			const r = parseForm(leaseSchema, probe);
			// drop id errors (they aren't user-entered here)
			const e = { ...(r.errors ?? {}) };
			delete e.propertyId; delete e.unitId; delete e.tenantId;
			if (rentTrackingStartMode === 'CustomCutoffDate' && !rentTrackingStartOn) {
				e.rentTrackingStartOn = 'Choose the custom rent tracking start date.';
			} else if (
				rentTrackingStartMode === 'CustomCutoffDate' &&
				leaseForm.startDate &&
				rentTrackingStartOn < leaseForm.startDate
			) {
				e.rentTrackingStartOn = 'Rent tracking cannot start before the agreement.';
			}
			leaseErrors = e;
			return Object.keys(leaseErrors).length === 0;
		}
		return true;
	}

	function next() {
		if (!validateStep(step)) return;
		step = Math.min(step + 1, TOTAL); // TOTAL == review index
	}
	function back() { step = Math.max(step - 1, 0); }

	const selectedExistingPropertyLabel = $derived.by(() => {
		if (propertyChoice === CREATE) return 'Create new from the lease';
		return selectedPropertyLabel || (propertiesQuery.data?.items.find((p) => String(p.id) === propertyChoice)?.name ?? 'Select a property');
	});
	const selectedExistingUnitLabel = $derived.by(() => {
		if (unitChoice === CREATE) return 'Create new from the lease';
		const unit = unitsQuery.data?.items.find((candidate) => String(candidate.id) === unitChoice);
		return selectedUnitLabel || (unit ? `Unit ${unit.unitNumber}` : 'Select a unit');
	});
	const selectedExistingTenantLabel = $derived.by(() => {
		if (tenantChoice === CREATE) return 'Create new from the lease';
		const tenant = tenantsQuery.data?.items.find((candidate) => String(candidate.id) === tenantChoice);
		return selectedTenantLabel || (tenant ? tenant.fullName || `${tenant.firstName} ${tenant.lastName}`.trim() : 'Select a tenant');
	});

	function propertyReviewAddress(): string {
		return [propertyForm.addressLine1, propertyForm.city, propertyForm.state]
			.reduce<string[]>((parts, value) => {
				if (value) parts.push(value);
				return parts;
			}, [])
			.join(', ');
	}

	// ----- confirm: emit Contract-3 override JSON, ONE ConfirmAsLeaseAsync -----
	function buildOverrides(): string {
		const o: Record<string, unknown> = {};
		if (isCreatingProperty) {
			o.propertyId = null;
			o.rentalStructure = propertyForm.rentalStructure;
			if (propertyForm.type) o.propertyType = propertyForm.type;
			if (propertyForm.name.trim()) o.propertyName = propertyForm.name.trim();
			if (propertyForm.addressLine1.trim()) o.propertyAddress = propertyForm.addressLine1.trim();
			if (propertyForm.city.trim()) o.propertyCity = propertyForm.city.trim();
			if (propertyForm.state.trim()) o.propertyState = propertyForm.state.trim();
			if (propertyForm.postalCode.trim()) o.propertyPostalCode = propertyForm.postalCode.trim();
		} else {
			o.propertyId = Number(propertyChoice);
		}
		if (!isCreatingProperty && unitChoice !== CREATE) {
			o.unitId = Number(unitChoice);
		} else {
			o.unitId = null;
			if (unitForm.unitNumber.trim()) o.unitNumber = unitForm.unitNumber.trim();
			if (unitForm.bedrooms.trim()) o.unitBedrooms = Number(unitForm.bedrooms);
			if (unitForm.bathrooms.trim()) o.unitBathrooms = Number(unitForm.bathrooms);
			if (unitForm.squareFeet.trim()) o.unitSquareFeet = Number(unitForm.squareFeet);
		}
		// tenant: link an existing tenant (trusted id), or create/match by name from the tenant step
		if (tenantChoice !== CREATE) {
			o.tenantId = Number(tenantChoice);
		} else {
			const fullName = `${tenantForm.firstName} ${tenantForm.lastName}`.trim();
			if (fullName) o.tenantName = fullName;
			if (tenantForm.email.trim()) o.tenantEmail = tenantForm.email.trim();
			if (tenantForm.phone.trim()) o.tenantPhone = tenantForm.phone.trim();
			if (tenantForm.emergencyContact.trim()) o.tenantEmergencyContact = tenantForm.emergencyContact.trim();
		}
		// lease terms
		o.leaseNumber =
			leaseForm.leaseNumber.trim() || defaultLeaseNumber(new Date(leaseForm.startDate || Date.now()));
		o.startDate = leaseForm.startDate;
		o.endDate = leaseForm.endDate;
		if (leaseForm.monthlyRent.trim()) o.monthlyRent = Number(leaseForm.monthlyRent);
		if (leaseForm.securityDeposit.trim()) o.securityDeposit = Number(leaseForm.securityDeposit);
		if (leaseForm.lateFeeAmount.trim()) o.lateFee = Number(leaseForm.lateFeeAmount);
		if (leaseForm.rentDueDay.trim()) o.rentDueDay = Number(leaseForm.rentDueDay);
		o.rentTrackingStartMode = rentTrackingStartMode;
		if (rentTrackingStartMode === 'CustomCutoffDate') {
			o.rentTrackingStartOn = rentTrackingStartOn;
		}
		o.reviewDisposition = reviewDisposition;
		if (reviewDisposition === 'AlreadyFullySigned' && possessionGivenOn) {
			o.possessionGivenAtUtc = possessionGivenOn;
		}
		if (reviewDisposition === 'NeedsSignatures' && documentTemplateId) {
			o.documentTemplateId = Number(documentTemplateId);
		}
		return JSON.stringify(o);
	}

	const signatureChoiceInvalid = $derived(!reviewDisposition);

	const confirmMutation = createMutation(() => ({
		mutationFn: () => scan.confirm(draftId as number, buildOverrides()),
		onSuccess: (res) => {
			toast.success('Your rental is set up.');
			phase = 'done';
			// A linked existing unit has an id in scope; a freshly created unit (unitChoice === CREATE)
			// does not, so we hand the parent null and it falls back to /leases/{id}.
			const unitId = unitChoice !== CREATE ? Number(unitChoice) : null;
			oncomplete({ leaseManagementId: res.leaseManagementId, agreementId: res.agreementId, propertyId: null, unitId, tenantId: null });
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not create the rental. Check the details and try again.'))
	}));
</script>

{#if phase === 'capture'}
	<div class="space-y-4" data-testid="new-rental-capture">
		<div class="flex items-start justify-between gap-3">
			<div>
				<h1 class="text-lg font-semibold text-foreground">New rental from your lease</h1>
				<p class="text-sm text-muted-foreground">Snap the lease (multiple photos are fine) or upload the PDF. We'll read it and pre-fill the property, unit, tenant, and lease for you to review.</p>
			</div>
			{#if oncancel}
				<Button type="button" variant="ghost" size="sm" onclick={oncancel}>Cancel</Button>
			{/if}
		</div>
		<div>
			<p class="mb-1 text-xs font-medium text-muted-foreground">Photos of the lease (one or many)</p>
			<FileDrop
				multiple
				title="Drop lease photos here"
				helperText="or click to browse — PDF, JPG, PNG, HEIC accepted"
				onselectedmany={onPhotos}
			/>
		</div>
		<div>
			<p class="mb-1 text-xs font-medium text-muted-foreground">…or a single PDF</p>
			<FileDrop
				title="Drop the lease PDF here"
				helperText="or click to browse — PDF, JPG, PNG, HEIC accepted"
				onselected={onPdf}
			/>
		</div>
	</div>
{:else if phase === 'processing'}
	<div class="flex items-center gap-2 py-10 text-sm text-muted-foreground" data-testid="new-rental-processing">
		<Loader2 class="h-4 w-4 animate-spin" /> Reading your lease… this takes a few seconds.
	</div>
{:else if phase === 'steps'}
	<!-- AC-1: visible step progress -->
	<div data-testid="new-rental-stepper">
		<div class="mb-1 flex items-center justify-between text-xs text-muted-foreground">
			<span data-testid="new-rental-step-label">
				{formatNewRentalStepLabel(step, STEP_LABELS)}
			</span>
			<span>{formatNewRentalStepPosition(step, STEP_LABELS)}</span>
		</div>
		<div class="h-1.5 w-full overflow-hidden rounded-full bg-muted">
			<div class="h-full bg-primary transition-all" style={`width:${((Math.min(step, TOTAL) + 1) / (TOTAL + 1)) * 100}%`}></div>
		</div>
	</div>

	{#if step === 0}
		<div class="space-y-3" data-testid="new-rental-step-property">
			<h2 class="text-base font-semibold text-foreground">Property</h2>
			<!-- AC-4 duplicate-guard at the Property step -->
			<div>
				<RemoteRecordSelect
					queryKey={['new-rental-properties', portfolioId]}
					label="Is this one of your existing properties?"
					value={propertyChoice === CREATE ? '' : propertyChoice}
					selectedLabel={selectedExistingPropertyLabel}
					placeholder="Create new from the lease"
					clearLabel="Create new from the lease"
					searchPlaceholder="Search properties…"
					testid="new-rental-property-choice"
					loadPage={loadPropertyOptions}
					onValueChange={(value, option) => {
						propertyChoice = value || CREATE;
						selectedPropertyLabel = option?.label ?? '';
						unitChoice = CREATE;
						selectedUnitLabel = '';
					}}
				/>
				{#if (propertiesQuery.data?.totalCount ?? 0) > 0 && isCreatingProperty}
					<p class="mt-1 text-xs text-[var(--warning)]" data-testid="new-rental-dupe-hint">If this lease is for a property you already have, pick it above to avoid a duplicate.</p>
				{/if}
			</div>
			{#if isCreatingProperty}
				<PropertyFields bind:form={propertyForm} errors={propertyErrors} {autoFilled} {confidence} testidPrefix="new-rental-property" />
			{/if}
		</div>
	{:else if step === 1}
		<div class="space-y-3" data-testid="new-rental-step-unit">
			<h2 class="text-base font-semibold text-foreground">Unit</h2>
			{#if !isCreatingProperty}
				<div>
					<RemoteRecordSelect
						queryKey={['new-rental-units', portfolioId, propertyChoice]}
						label="Pick an existing unit, or create one."
						value={unitChoice === CREATE ? '' : unitChoice}
						selectedLabel={selectedExistingUnitLabel}
						placeholder="Create new from the lease"
						clearLabel="Create new from the lease"
						searchPlaceholder="Search units…"
						testid="new-rental-unit-choice"
						loadPage={loadUnitOptions}
						onValueChange={(value, option) => {
							unitChoice = value || CREATE;
							selectedUnitLabel = option?.label ?? '';
						}}
					/>
				</div>
			{/if}
			{#if isCreatingProperty || unitChoice === CREATE}
				<UnitFields bind:form={unitForm} errors={unitErrors} {autoFilled} {confidence} testidPrefix="new-rental-unit" />
			{/if}
		</div>
	{:else if step === 2}
		<div class="space-y-3" data-testid="new-rental-step-tenant">
			<h2 class="text-base font-semibold text-foreground">Tenant</h2>
			<div>
				<RemoteRecordSelect
					queryKey={['new-rental-tenants', portfolioId]}
					label="Is this one of your existing tenants?"
					value={tenantChoice === CREATE ? '' : tenantChoice}
					selectedLabel={selectedExistingTenantLabel}
					placeholder="Create new from the lease"
					clearLabel="Create new from the lease"
					searchPlaceholder="Search tenants…"
					testid="new-rental-tenant-choice"
					loadPage={loadTenantOptions}
					onValueChange={(value, option) => {
						tenantChoice = value || CREATE;
						selectedTenantLabel = option?.label ?? '';
					}}
				/>
				{#if (tenantsQuery.data?.totalCount ?? 0) > 0 && isCreatingTenant}
					<p class="mt-1 text-xs text-[var(--warning)]" data-testid="new-rental-tenant-dupe-hint">If this lease is for someone you already have, pick them above to avoid a duplicate.</p>
				{/if}
			</div>
			{#if isCreatingTenant}
				<TenantFields bind:form={tenantForm} errors={tenantErrors} {autoFilled} testidPrefix="new-rental-tenant" />
			{/if}
		</div>
	{:else if step === 3}
		<div class="space-y-3" data-testid="new-rental-step-lease">
			<h2 class="text-base font-semibold text-foreground">Lease</h2>
			<LeaseTermFields bind:form={leaseForm} errors={leaseErrors} {autoFilled} {confidence} testidPrefix="new-rental-lease" />
			<div class="grid gap-3 rounded-md border border-border bg-muted/20 p-3 sm:grid-cols-2">
				<div class={rentTrackingStartMode === 'CustomCutoffDate' ? '' : 'sm:col-span-2'}>
					<label class="mb-1 block text-xs font-semibold text-foreground" for="new-rental-rent-tracking-mode">
						Begin rent charges
					</label>
					<Select.Root
						type="single"
						bind:value={rentTrackingStartMode}
						onValueChange={() => {
							if (rentTrackingStartMode !== 'CustomCutoffDate') rentTrackingStartOn = '';
						}}
					>
						<Select.Trigger
							id="new-rental-rent-tracking-mode"
							data-testid="new-rental-rent-tracking-mode"
							class="w-full"
						>
							{rentTrackingStartMode === 'ForwardOnly'
								? 'Start from the current date'
								: rentTrackingStartMode === 'BackfillFromLeaseStart'
									? 'Backfill from the lease start'
									: 'Start from a custom date'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="ForwardOnly" label="Start from the current date">Start from the current date</Select.Item>
							<Select.Item value="BackfillFromLeaseStart" label="Backfill from the lease start">Backfill from the lease start</Select.Item>
							<Select.Item value="CustomCutoffDate" label="Start from a custom date">Start from a custom date</Select.Item>
						</Select.Content>
					</Select.Root>
					<p class="mt-1 text-xs text-muted-foreground">
						This decides whether older rent periods are posted when the signed lease is imported.
					</p>
				</div>
				{#if rentTrackingStartMode === 'CustomCutoffDate'}
					<div>
						<label class="mb-1 block text-xs font-semibold text-foreground" for="new-rental-rent-tracking-date">
							Custom start date
						</label>
						<DatePicker
							id="new-rental-rent-tracking-date"
							bind:value={rentTrackingStartOn}
							min={leaseForm.startDate || undefined}
							testid="new-rental-rent-tracking-date"
						/>
						{#if leaseErrors.rentTrackingStartOn}
							<p class="mt-1 text-xs text-destructive">{leaseErrors.rentTrackingStartOn}</p>
						{/if}
					</div>
				{/if}
			</div>
		</div>
	{:else}
		<!-- AC-4: final review before save -->
		<div class="space-y-3" data-testid="new-rental-review">
			<h2 class="text-base font-semibold text-foreground">Here's what I'll add</h2>
			<ul class="space-y-2 rounded-md border border-border bg-muted/30 p-3 text-sm">
				<li data-testid="review-property"><span class="font-medium">Property:</span>
					{#if isCreatingProperty}{propertyForm.name || propertyForm.addressLine1} — {propertyReviewAddress()} <span class="text-xs text-[var(--success)]">(new)</span>
					{:else}{selectedExistingPropertyLabel} <span class="text-xs text-muted-foreground">(existing)</span>{/if}
				</li>
				<li data-testid="review-unit"><span class="font-medium">Unit:</span>
					{#if !isCreatingProperty && unitChoice !== CREATE}{selectedExistingUnitLabel} <span class="text-xs text-muted-foreground">(existing)</span>
					{:else}Unit {unitForm.unitNumber} <span class="text-xs text-[var(--success)]">(new)</span>{/if}
				</li>
				<li data-testid="review-tenant"><span class="font-medium">Tenant:</span>
					{#if tenantChoice !== CREATE}{selectedExistingTenantLabel} <span class="text-xs text-muted-foreground">(existing)</span>
					{:else}{`${tenantForm.firstName} ${tenantForm.lastName}`.trim() || '—'} <span class="text-xs text-[var(--success)]">(new)</span>{/if}
				</li>
				<li data-testid="review-lease"><span class="font-medium">Lease:</span> ${leaseForm.monthlyRent}/mo, {leaseForm.startDate} – {leaseForm.endDate}</li>
			</ul>
			<LeaseScanSignatureChoice
				bind:reviewDisposition
				bind:documentTemplateId
				propertyId={isCreatingProperty ? 0 : Number(propertyChoice)}
				disabled={confirmMutation.isPending}
			/>
			{#if reviewDisposition === 'AlreadyFullySigned'}
				<div class="rounded-md border border-border bg-muted/20 p-3">
					<label class="mb-1 block text-xs font-semibold text-foreground" for="new-rental-possession-given-date">
						Possession given
					</label>
					<DatePicker
						id="new-rental-possession-given-date"
						bind:value={possessionGivenOn}
						min={leaseForm.startDate || undefined}
						testid="new-rental-possession-given-date"
					/>
					<p class="mt-1 text-xs text-muted-foreground">
						Required when the signed lease term has started and the tenant already received possession.
					</p>
				</div>
			{/if}
			<p class="text-xs text-muted-foreground">Nothing is saved until you tap Confirm. We'll create everything in one step.</p>
		</div>
	{/if}

	<div class="flex items-center justify-between pt-2">
		<Button variant="ghost" onclick={back} disabled={step === 0} data-testid="new-rental-back">Back</Button>
		{#if step < TOTAL}
			<Button onclick={next} data-testid="new-rental-next">Next</Button>
		{:else}
			<Button onclick={() => confirmMutation.mutate()} disabled={confirmMutation.isPending || signatureChoiceInvalid} data-testid="new-rental-confirm">
				{confirmMutation.isPending ? 'Creating…' : 'Confirm & create'}
			</Button>
		{/if}
	</div>
{/if}
