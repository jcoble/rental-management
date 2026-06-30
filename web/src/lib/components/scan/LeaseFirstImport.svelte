<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { toast } from 'svelte-sonner';
	import { scan } from '$lib/api/scan';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { Button } from '$lib/components/ui/button';
	import * as Select from '$lib/components/ui/select';
	import { Loader2 } from '@lucide/svelte';
	import FileDrop from '$lib/components/FileDrop.svelte';
	import PropertyFields from '$lib/components/forms/PropertyFields.svelte';
	import UnitFields from '$lib/components/forms/UnitFields.svelte';
	import TenantFields from '$lib/components/forms/TenantFields.svelte';
	import LeaseTermFields from '$lib/components/forms/LeaseTermFields.svelte';
	import { propertySchema, unitSchema, tenantSchema, leaseSchema, leaseRentTrackingErrors, parseForm } from '$lib/schemas';
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
		oncomplete: (result: { leaseId?: number | null; propertyId?: number | null; unitId?: number | null; tenantId?: number | null }) => void;
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
	let unitForm = $state({ unitNumber: '', bedrooms: '', bathrooms: '', marketRent: '' });
	let tenantForm = $state({ firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' });
	let leaseForm = $state({
		leaseNumber: defaultLeaseNumber(),
		startDate: '',
		endDate: '',
		monthlyRent: '',
		securityDeposit: '',
		lateFeeAmount: '',
		rentDueDay: '1',
		rentTrackingStartMode: 'ForwardOnly',
		rentTrackingStartDate: '',
		openingBalanceAmount: '',
		openingBalanceAsOfDate: '',
		openingBalanceNote: '',
		status: 'Active',
		notes: ''
	});

	let propertyErrors = $state<Record<string, string>>({});
	let unitErrors = $state<Record<string, string>>({});
	let tenantErrors = $state<Record<string, string>>({});
	let leaseErrors = $state<Record<string, string>>({});

	// Which step-form fields were auto-filled (drives the "from your lease" badge).
	let autoFilled = $state<Set<string>>(new Set());

	// ----- Property duplicate-guard: link an existing in-portfolio property, or create new -----
	const CREATE = '__create__';
	let propertyChoice = $state<string>(CREATE); // a real id string => link; CREATE => create-new
	let unitChoice = $state<string>(CREATE);      // existing unit id, or CREATE
	let tenantChoice = $state<string>(CREATE);    // existing tenant id, or CREATE
	const isCreatingProperty = $derived(propertyChoice === CREATE);
	const isCreatingTenant = $derived(tenantChoice === CREATE);

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
		enabled: phase === 'steps'
	}));
	const unitsQuery = createQuery(() => ({
		queryKey: ['units-for-new-rental', propertyChoice],
		queryFn: () => properties.listUnits(Number(propertyChoice)),
		enabled: phase === 'steps' && propertyChoice !== CREATE && !!propertyChoice
	}));
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId, { take: 200 }),
		enabled: phase === 'steps'
	}));

	function resetReviewStateForDraft() {
		confidence = {};
		step = 0;
		propertyForm = createNewRentalPropertyForm();
		unitForm = { unitNumber: '', bedrooms: '', bathrooms: '', marketRent: '' };
		tenantForm = { firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' };
		leaseForm = {
			leaseNumber: defaultLeaseNumber(),
			startDate: '',
			endDate: '',
			monthlyRent: '',
			securityDeposit: '',
			lateFeeAmount: '',
			rentDueDay: '1',
			rentTrackingStartMode: 'ForwardOnly',
			rentTrackingStartDate: '',
			openingBalanceAmount: '',
			openingBalanceAsOfDate: '',
			openingBalanceNote: '',
			status: 'Active',
			notes: ''
		};
		propertyErrors = {};
		unitErrors = {};
		tenantErrors = {};
		leaseErrors = {};
		autoFilled = new Set();
		propertyChoice = CREATE;
		unitChoice = CREATE;
		tenantChoice = CREATE;
		seeded = false;
		unitChoiceSeeded = false;
	}

	// ----- upload: one PDF passes straight through; many photos are stitched first -----
	const uploadOne = createMutation(() => ({
		mutationFn: (file: File) => scan.upload(file, 'Lease'),
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
		if (leaseForm.rentTrackingStartMode !== 'CustomCutoffDate' && leaseForm.rentTrackingStartDate) {
			leaseForm.rentTrackingStartDate = '';
		}
	});
	$effect(() => {
		if (leaseForm.rentTrackingStartMode !== 'OpeningBalanceOnly') {
			if (leaseForm.openingBalanceAmount) leaseForm.openingBalanceAmount = '';
			if (leaseForm.openingBalanceAsOfDate) leaseForm.openingBalanceAsOfDate = '';
			if (leaseForm.openingBalanceNote) leaseForm.openingBalanceNote = '';
		}
	});

	$effect(() => {
		if (phase !== 'steps' || isCreatingProperty || unitChoiceSeeded || unitChoice !== CREATE) return;
		const unitId = findNewRentalExistingUnitId(
			unitForm.unitNumber,
			draftQuery.data?.leaseProposal?.unit,
			unitsQuery.data ?? []
		);
		if (!unitId) return;
		unitChoice = unitId;
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
			leaseErrors = { ...e, ...leaseRentTrackingErrors(probe) };
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
		return propertiesQuery.data?.find((p) => String(p.id) === propertyChoice)?.name ?? 'Select a property';
	});
	const selectedExistingUnitLabel = $derived.by(() => {
		if (unitChoice === CREATE) return 'Create new from the lease';
		const u = unitsQuery.data?.find((u) => String(u.id) === unitChoice);
		return u ? `Unit ${u.unitNumber}` : 'Select a unit';
	});
	const selectedExistingTenantLabel = $derived.by(() => {
		if (tenantChoice === CREATE) return 'Create new from the lease';
		const t = tenantsQuery.data?.find((t) => String(t.id) === tenantChoice);
		return t ? t.fullName || `${t.firstName} ${t.lastName}`.trim() : 'Select a tenant';
	});

	// ----- confirm: emit Contract-3 override JSON, ONE ConfirmAsLeaseAsync -----
	function buildOverrides(): string {
		const o: Record<string, unknown> = {};
		if (isCreatingProperty) {
			o.propertyId = null;
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
		o.rentTrackingStartMode = leaseForm.rentTrackingStartMode;
		if (leaseForm.rentTrackingStartMode === 'CustomCutoffDate') {
			o.rentTrackingStartDate = leaseForm.rentTrackingStartDate;
		}
		if (leaseForm.rentTrackingStartMode === 'OpeningBalanceOnly') {
			if (leaseForm.openingBalanceAmount.trim()) o.openingBalanceAmount = Number(leaseForm.openingBalanceAmount);
			if (leaseForm.openingBalanceAsOfDate.trim()) o.openingBalanceAsOfDate = leaseForm.openingBalanceAsOfDate;
			if (leaseForm.openingBalanceNote.trim()) o.openingBalanceNote = leaseForm.openingBalanceNote.trim();
		}
		return JSON.stringify(o);
	}

	const confirmMutation = createMutation(() => ({
		mutationFn: () => scan.confirm(draftId as number, buildOverrides()),
		onSuccess: (res) => {
			toast.success('Your rental is set up.');
			phase = 'done';
			// A linked existing unit has an id in scope; a freshly created unit (unitChoice === CREATE)
			// does not, so we hand the parent null and it falls back to /leases/{id}.
			const unitId = unitChoice !== CREATE ? Number(unitChoice) : null;
			oncomplete({ leaseId: res.leaseId, propertyId: null, unitId, tenantId: null });
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
				<p class="mb-1 text-xs font-medium text-muted-foreground">Is this one of your existing properties?</p>
				<Select.Root type="single" bind:value={propertyChoice}>
					<Select.Trigger class="w-full" data-testid="new-rental-property-choice">{selectedExistingPropertyLabel}</Select.Trigger>
					<Select.Content>
						<Select.Item value={CREATE} label="Create new from the lease">Create new from the lease</Select.Item>
						{#each propertiesQuery.data ?? [] as p (p.id)}
							<Select.Item value={String(p.id)} label={p.name}>{p.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				{#if propertiesQuery.data && propertiesQuery.data.length > 0 && isCreatingProperty}
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
					<p class="mb-1 text-xs font-medium text-muted-foreground">Pick an existing unit, or create one.</p>
					<Select.Root type="single" bind:value={unitChoice}>
						<Select.Trigger class="w-full" data-testid="new-rental-unit-choice">{selectedExistingUnitLabel}</Select.Trigger>
						<Select.Content>
							<Select.Item value={CREATE} label="Create new from the lease">Create new from the lease</Select.Item>
							{#each unitsQuery.data ?? [] as u (u.id)}
								<Select.Item value={String(u.id)} label={`Unit ${u.unitNumber}`}>Unit {u.unitNumber} ({u.status})</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
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
				<p class="mb-1 text-xs font-medium text-muted-foreground">Is this one of your existing tenants?</p>
				<Select.Root type="single" bind:value={tenantChoice}>
					<Select.Trigger class="w-full" data-testid="new-rental-tenant-choice">{selectedExistingTenantLabel}</Select.Trigger>
					<Select.Content>
						<Select.Item value={CREATE} label="Create new from the lease">Create new from the lease</Select.Item>
						{#each tenantsQuery.data ?? [] as t (t.id)}
							<Select.Item value={String(t.id)} label={t.fullName || `${t.firstName} ${t.lastName}`}>{t.fullName || `${t.firstName} ${t.lastName}`}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				{#if tenantsQuery.data && tenantsQuery.data.length > 0 && isCreatingTenant}
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
		</div>
	{:else}
		<!-- AC-4: final review before save -->
		<div class="space-y-3" data-testid="new-rental-review">
			<h2 class="text-base font-semibold text-foreground">Here's what I'll add</h2>
			<ul class="space-y-2 rounded-md border border-border bg-muted/30 p-3 text-sm">
				<li data-testid="review-property"><span class="font-medium">Property:</span>
					{#if isCreatingProperty}{propertyForm.name || propertyForm.addressLine1} — {[propertyForm.addressLine1, propertyForm.city, propertyForm.state].filter(Boolean).join(', ')} <span class="text-xs text-[var(--success)]">(new)</span>
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
			<p class="text-xs text-muted-foreground">Nothing is saved until you tap Confirm. We'll create everything in one step.</p>
		</div>
	{/if}

	<div class="flex items-center justify-between pt-2">
		<Button variant="ghost" onclick={back} disabled={step === 0} data-testid="new-rental-back">Back</Button>
		{#if step < TOTAL}
			<Button onclick={next} data-testid="new-rental-next">Next</Button>
		{:else}
			<Button onclick={() => confirmMutation.mutate()} disabled={confirmMutation.isPending} data-testid="new-rental-confirm">
				{confirmMutation.isPending ? 'Creating…' : 'Confirm & create'}
			</Button>
		{/if}
	</div>
{/if}
