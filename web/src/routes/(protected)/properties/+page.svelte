<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { properties } from '$lib/api/endpoints/properties';
	import { owners } from '$lib/api/endpoints/owners';
	import type { Property, RentalStructure } from '$lib/types';
	import { getPropertyDeleteState } from '$lib/properties/property-delete-state';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { propertySchema, propertyBasisSchema, propertyOperationsSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import StepperNextButton from '$lib/components/shared/StepperNextButton.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import PropertyFields from '$lib/components/forms/PropertyFields.svelte';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { Plus, Pencil, Trash2, Building, ChevronDown, X } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import HelpPopover from '$lib/components/ui/HelpPopover.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { page } from '$app/state';
	import type { WorkspaceExperience } from '$lib/types/user';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { CAPABILITY } from '$lib/auth/experience-policy';
	import { hasAllPropertiesRentalsManageAuthority } from '$lib/auth/property-authority';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import {
		formatPropertyStatus,
		formatPropertyType,
		propertyStatusOptions,
		propertyTypeOptions
	} from '$lib/properties/property-labels';
	import {
		createEmptyPropertyDraft,
		getPropertiesEmptyStateCopy
	} from '$lib/properties/property-list-state';
	import { propertyUpdateFields } from '$lib/properties/property-update-payload';
	import {
		propertyWorkspaceRoute,
		resolvePropertyWorkspaceEntry,
		type PropertyWithWorkspaceEntry
	} from '$lib/components/property/property-workspace';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const authState = getAuthState();
	const currentAccess = $derived(page.data.access ?? authState.accessEnvelope ?? null);
	const activeExperience = $derived(authState.activeExperience ?? currentAccess?.selectedContext.activeExperience ?? null);
	const activeCapabilities = $derived<Set<string>>(new Set<string>(
		currentAccess?.navigation.find((entry: { experience: WorkspaceExperience; capabilityKeys: string[] }) =>
			entry.experience === activeExperience)?.capabilityKeys ?? []
	));
	const canManageRentals = $derived(
		activeExperience === 'Management' && activeCapabilities.has(CAPABILITY.rentalsManage)
	);
	const canCreateProperty = $derived(hasAllPropertiesRentalsManageAuthority(currentAccess));
	const PAGE_SIZE = 20;

	// Second coach hop for the "add a unit" checklist step. That step lands here (the property LIST)
	// and spotlights "open a property", but units are added on the property DETAIL page. When we arrive
	// via that coach, latch it so the row the user opens carries ?coach=add-unit onward — the detail
	// page's CoachTrigger then spotlights its "Add Unit" button, continuing the guidance.
	let forwardUnitCoach = $state(false);
	$effect(() => {
		if (page.url.searchParams.get('coach') === 'open-property-for-units') forwardUnitCoach = true;
	});
	function openProperty(p: PropertyWithWorkspaceEntry) {
		const entry = resolvePropertyWorkspaceEntry(p);
		const route = propertyWorkspaceRoute(entry);
		const separator = route.includes('?') ? '&' : '?';
		goto(`${route}${forwardUnitCoach ? `${separator}coach=add-unit` : ''}`);
	}

	// Filter/search/sort/page state persisted in the URL so it survives navigating away and back (and
	// browser Back/Forward, which remounts and re-seeds from these params). The DataGrid is server-side:
	// this state drives the API query instead of fetching a broad cap and sorting/paging in Svelte.
	const initialParams = page.url.searchParams;
	let search = $state(readGridParam(initialParams, 'q'));
	const ALL_TYPES = 'all';
	let typeValue = $state(readGridParam(initialParams, 'type') || ALL_TYPES);
	const typeFilter = $derived(typeValue === ALL_TYPES ? '' : typeValue);
	let statusValue = $state(readGridParam(initialParams, 'status') || ALL_TYPES);
	const statusFilter = $derived(statusValue === ALL_TYPES ? '' : statusValue);
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));
	const debouncedSearch = debounced(() => search, 300);

	// Reset to page 1 when a filter/search changes — but not on initial mount, so a deep-linked/restored
	// ?page= loads as-is.
	let filterResetPrimed = false;
	$effect(() => {
		search;
		typeFilter;
		statusFilter;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl(
			{ q: search, type: typeFilter, status: statusFilter, sort: gridSort, page: gridPage },
			{ page: 1 }
		);
	});

	const propertiesQuery = createQuery(() => ({
		queryKey: [
			'properties',
			portfolioId,
			'page',
			debouncedSearch.value,
			typeFilter,
			statusFilter,
			gridSort,
			gridPage,
			PAGE_SIZE
		],
		queryFn: () => properties.listPage(portfolioId, {
			search: debouncedSearch.value,
			type: typeFilter || undefined,
			status: statusFilter || undefined,
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		}),
	}));

	async function loadOwnerOptions(params: { search?: string; skip: number; take: number }) {
		const result = await owners.listPage(portfolioId, { ...params, sort: 'name' });
		return {
			...result,
			items: result.items.map((owner) => ({
				id: owner.id,
				label: owner.name,
				description: owner.ownerEntityType
			}))
		};
	}

	const list = $derived<PropertyWithWorkspaceEntry[]>(propertiesQuery.data?.items ?? []);
	const totalCount = $derived(propertiesQuery.data?.totalCount ?? 0);
	const hasActiveFilters = $derived(Boolean(search.trim() || typeFilter || statusFilter));
	const emptyStateCopy = $derived(getPropertiesEmptyStateCopy({ hasActiveFilters }));

	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state(createEmptyPropertyDraft());
	let inServiceDateInvalid = $state(false);
	let formErrors = $state<Record<string, string>>({});
	let propertyStep = $state(0);
	let completedPropertySteps = $state<number[]>([]);
	let selectedOwnerLabel = $state<string | null>(null);
	let initialOwnerEntityId = $state('');
	let deleteTarget = $state<Property | null>(null);

	// "Add rental" happens here on the list. Six things the landlord always knows sit on the face of
	// the dialog; type, owner, fees and the tax numbers wait under a collapsed "More details".
	let showCreate = $state(false);
	let createForm = $state(createEmptyPropertyDraft());
	let createMultiple = $state(false);
	let createRent = $state('');
	let createBeds = $state('');
	let createBaths = $state('');
	let createUnitNames = $state<string[]>(['', '']);
	let createMoreOpen = $state(false);
	let createErrors = $state<Record<string, string>>({});
	let createInServiceDateInvalid = $state(false);
	let createOwnerLabel = $state<string | null>(null);

	/** Fields that live under "More details" — an error in any of them opens the disclosure. */
	const CREATE_MORE_FIELDS = [
		'type',
		'status',
		'addressLine2',
		'ownerEntityId',
		'bedrooms',
		'bathrooms',
		'yearBuilt',
		'managementFeePercent',
		'notes',
		'purchasePrice',
		'landValue',
		'inServiceDate',
		'manualAnnualDepreciation',
	];

	const deleteState = $derived(deleteTarget ? getPropertyDeleteState(deleteTarget) : null);

	const propertySteps: FormStepperStep[] = [
		{ id: 'identity', label: 'Identity', description: 'Name and rental setup' },
		{ id: 'address', label: 'Address', description: 'Street and ZIP' },
		{ id: 'setup', label: 'Setup', description: 'Status and owner' },
		{ id: 'operations', label: 'Operations', description: 'Year, fee, and notes' },
		{ id: 'basis', label: 'Tax basis', description: 'Cost and depreciation' },
	];
	const propertyStepFields = [
		['name', 'type', 'rentalStructure'],
		['addressLine1', 'city', 'state', 'postalCode'],
		['status', 'addressLine2', 'ownerEntityId'],
		['yearBuilt', 'managementFeePercent', 'notes'],
		['purchasePrice', 'landValue', 'inServiceDate', 'manualAnnualDepreciation'],
	] as const;

	function clearPropertyError(field: string) {
		const next = clearFieldError(formErrors, field);
		if (next !== formErrors) formErrors = next;
	}

	$effect(() => {
		if (form.name.trim()) clearPropertyError('name');
	});
	$effect(() => {
		if (form.addressLine1.trim()) clearPropertyError('addressLine1');
	});
	$effect(() => {
		if (form.city.trim()) clearPropertyError('city');
	});
	$effect(() => {
		if (form.state.trim()) clearPropertyError('state');
	});
	$effect(() => {
		if (form.postalCode.trim()) clearPropertyError('postalCode');
	});

	$effect(() => {
		if (createForm.name.trim()) clearCreateError('name');
	});
	$effect(() => {
		if (createForm.addressLine1.trim()) clearCreateError('addressLine1');
	});
	$effect(() => {
		if (createForm.city.trim()) clearCreateError('city');
	});
	$effect(() => {
		if (createForm.state.trim()) clearCreateError('state');
	});
	$effect(() => {
		if (createForm.postalCode.trim()) clearCreateError('postalCode');
	});

	function invalidateList() {
		queryClient.invalidateQueries({ queryKey: ['properties'] });
	}

	type SavePropertyVariables = { id: number; data: Record<string, unknown> };

	const savePropertyMutation = createMutation(() => ({
		mutationFn: (vars: SavePropertyVariables) => properties.update(vars.id, vars.data),
		onSuccess: () => {
			showSuccess('Property updated.');
			closeForm();
			invalidateList();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	type CreatePropertyVariables = {
		property: Record<string, unknown> & { rentalStructure: RentalStructure };
		units: Record<string, unknown>[];
	};

	const createPropertyMutation = createMutation(() => ({
		mutationFn: (vars: CreatePropertyVariables) => properties.setup({ property: vars.property, units: vars.units }),
		onSuccess: () => {
			showSuccess('Rental added.');
			closeCreate();
			invalidateList();
			queryClient.invalidateQueries({ queryKey: ['units'] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deletePropertyMutation = createMutation(() => ({
		mutationFn: (id: number) => properties.delete(id),
		onSuccess: () => {
			showSuccess('Property deleted.');
			deleteTarget = null;
			invalidateList();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreate() {
		if (!canCreateProperty) return;
		createForm = createEmptyPropertyDraft({ typeFilter, statusFilter });
		createMultiple = false;
		createRent = '';
		createBeds = '';
		createBaths = '';
		createUnitNames = ['', ''];
		createMoreOpen = false;
		createErrors = {};
		createInServiceDateInvalid = false;
		createOwnerLabel = null;
		showCreate = true;
	}

	function closeCreate() {
		showCreate = false;
		createErrors = {};
		createMoreOpen = false;
		createInServiceDateInvalid = false;
		createOwnerLabel = null;
	}

	function clearCreateError(field: string) {
		const next = clearFieldError(createErrors, field);
		if (next !== createErrors) createErrors = next;
	}

	/** Rent, beds and baths (one rental) or the rental names (several) — checked against the server limits. */
	function createUnitErrors(): Record<string, string> {
		const errors: Record<string, string> = {};
		if (createMultiple) {
			const names = createUnitNames.map((name) => name.trim()).filter(Boolean);
			if (names.length === 0) errors.unitNames = 'Name at least one rental at this address.';
			else if (names.some((name) => name.length > 50)) errors.unitNames = 'Each rental name must be 50 characters or fewer.';
			else if (new Set(names.map((name) => name.toLowerCase())).size !== names.length) errors.unitNames = 'Give each rental a different name.';
			return errors;
		}
		const rent = Number(createRent);
		if (!createRent.trim() || !Number.isFinite(rent) || rent < 0) errors.marketRent = 'Enter the monthly rent (use 0 if it is empty).';
		else if (rent > 99999999) errors.marketRent = 'Monthly rent cannot exceed 99,999,999.';
		for (const [field, value, label] of [
			['bedrooms', createBeds, 'Bedrooms'],
			['bathrooms', createBaths, 'Bathrooms'],
		] as const) {
			if (!value.trim()) continue;
			const count = Number(value);
			if (!Number.isFinite(count) || count < 0 || count > 99) errors[field] = `${label} must be between 0 and 99.`;
		}
		return errors;
	}

	function createUnitPayload(): Record<string, unknown>[] {
		// Beds and baths are optional here — a home always has a count on file, so anything the
		// landlord leaves blank is saved as 0 and can be filled in on the rental's own page.
		const beds = createBeds.trim() ? Number(createBeds) : 0;
		const baths = createBaths.trim() ? Number(createBaths) : 0;
		if (createMultiple) {
			return createUnitNames
				.map((name) => name.trim())
				.filter(Boolean)
				.map((unitNumber) => ({ unitNumber, marketRent: 0, bedrooms: 0, bathrooms: 0 }));
		}
		// One rental at this address: the single unit is created for the landlord, named "1".
		return [{ unitNumber: '1', marketRent: Number(createRent), bedrooms: beds, bathrooms: baths }];
	}

	function submitCreate() {
		if (!canCreateProperty) return;
		if (createInServiceDateInvalid) {
			createErrors = { ...createErrors, inServiceDate: 'Enter a valid in-service date.' };
			createMoreOpen = true;
			return;
		}
		const rentalStructure: RentalStructure = createMultiple ? 'MultiRental' : 'SingleRental';
		const draft = { ...createForm, rentalStructure };
		const result = parseForm(propertySchema, draft);
		const operations = parseForm(propertyOperationsSchema, draft);
		const basis = parseForm(propertyBasisSchema, draft);
		const errors = {
			...(result.errors ?? {}),
			...(operations.errors ?? {}),
			...(basis.errors ?? {}),
			...createUnitErrors(),
		};
		if (Object.keys(errors).length > 0) {
			createErrors = errors;
			// Never hide a problem: if something under "More details" needs fixing, open it.
			if (CREATE_MORE_FIELDS.some((field) => errors[field])) createMoreOpen = true;
			return;
		}
		if (!result.data || !operations.data || !basis.data) return;
		createErrors = {};
		const ownerEntityId = result.data.ownerEntityId;
		createPropertyMutation.mutate({
			property: {
				...propertyUpdateFields(result.data),
				...operations.data,
				...basis.data,
				rentalStructure,
				...(ownerEntityId == null
					? {}
					: { ownerships: [{ ownerEntityId, ownershipSharePercent: 100 }], clearOwnership: false }),
			},
			units: createUnitPayload(),
		});
	}

	function openEdit(p: Property) {
		if (!canManageRentals) return;
		editingId = p.id;
		form = {
			name: p.name,
			type: p.type ?? 'MultiFamily',
			rentalStructure: p.rentalStructure,
			status: p.status ?? 'Active',
			addressLine1: p.addressLine1,
			addressLine2: p.addressLine2 ?? '',
			city: p.city,
			state: p.state,
			postalCode: p.postalCode,
			ownerEntityId: p.ownerships.length === 1 ? String(p.ownerships[0].ownerEntityId) : '',
			yearBuilt: p.yearBuilt != null ? String(p.yearBuilt) : '',
			managementFeePercent: p.managementFeePercent != null ? String(p.managementFeePercent) : '',
			notes: p.notes ?? '',
			purchasePrice: p.purchasePrice != null ? String(p.purchasePrice) : '',
			landValue: p.landValue != null ? String(p.landValue) : '',
			inServiceDate: p.inServiceDate ? p.inServiceDate.slice(0, 10) : '',
			manualAnnualDepreciation: p.manualAnnualDepreciation != null ? String(p.manualAnnualDepreciation) : '',
		};
		initialOwnerEntityId = form.ownerEntityId;
		selectedOwnerLabel = p.ownerships.length === 1 ? p.ownerships[0].ownerName : null;
		inServiceDateInvalid = false;
		formErrors = {};
		propertyStep = 0;
		completedPropertySteps = [];
		showForm = true;
	}

	function closeForm() {
		showForm = false;
		editingId = null;
		selectedOwnerLabel = null;
		initialOwnerEntityId = '';
		inServiceDateInvalid = false;
		formErrors = {};
		propertyStep = 0;
		completedPropertySteps = [];
	}

	function propertyStepErrorFields(step: number, errors: Record<string, string>) {
		const visibleFields = new Set<string>(propertyStepFields[step] ?? []);
		return Object.entries(errors).filter(([field]) => visibleFields.has(field));
	}

	function firstPropertyErrorStep(errors: Record<string, string>) {
		return propertyStepFields.findIndex((fields) => fields.some((field) => errors[field]));
	}

	function markPropertyStepInvalid(step: number) {
		completedPropertySteps = completedPropertySteps.filter((completedStep) => completedStep < step);
	}

	function validatePropertyStep(step: number) {
		const result = parseForm(propertySchema, form);
		const currentErrors = result.errors ? Object.fromEntries(propertyStepErrorFields(step, result.errors)) : {};
		const currentFields = new Set<string>(propertyStepFields[step] ?? []);
		const nextErrors = Object.fromEntries(Object.entries(formErrors).filter(([field]) => !currentFields.has(field)));
		formErrors = { ...nextErrors, ...currentErrors };
		const isValid = Object.keys(currentErrors).length === 0;
		if (!isValid) markPropertyStepInvalid(step);
		return isValid;
	}

	function nextPropertyStep() {
		if (!validatePropertyStep(propertyStep)) return;
		if (completedPropertySteps.includes(propertyStep)) {
			propertyStep = Math.min(propertyStep + 1, propertySteps.length - 1);
			return;
		}
		completedPropertySteps = [...completedPropertySteps, propertyStep];
		window.setTimeout(() => {
			propertyStep = Math.min(propertyStep + 1, propertySteps.length - 1);
		}, 260);
	}

	function submitProperty() {
		if (!canManageRentals) return;
		if (editingId == null) return;
		if (inServiceDateInvalid) {
			formErrors = { ...formErrors, inServiceDate: 'Enter a valid in-service date.' };
			propertyStep = propertySteps.length - 1;
			markPropertyStepInvalid(propertyStep);
			return;
		}
		const result = parseForm(propertySchema, form);
		const operations = parseForm(propertyOperationsSchema, form);
		const basis = parseForm(propertyBasisSchema, form);
		if (result.errors || operations.errors || basis.errors) {
			const errors = { ...(result.errors ?? {}), ...(operations.errors ?? {}), ...(basis.errors ?? {}) };
			formErrors = errors;
			const firstErrorStep = firstPropertyErrorStep(errors);
			if (firstErrorStep >= 0) {
				propertyStep = firstErrorStep;
				markPropertyStepInvalid(firstErrorStep);
			}
			return;
		}
		formErrors = {};
		const mutableProperty = propertyUpdateFields(result.data);
		const ownerEntityId = result.data.ownerEntityId;
		const ownershipChange = editingId != null && String(ownerEntityId ?? '') === initialOwnerEntityId
			? {}
			: ownerEntityId == null
				? { ownerships: [], clearOwnership: true }
				: {
						ownerships: [{ ownerEntityId, ownershipSharePercent: 100 }],
						clearOwnership: false
					};
		const data = {
			portfolioId,
			...mutableProperty,
			...operations.data,
			...basis.data,
			...ownershipChange,
		};
		savePropertyMutation.mutate(
			{ id: editingId, data }
		);
	}

	// DataGrid column definitions
	const columns: ColumnDef<PropertyWithWorkspaceEntry>[] = [
		{
			key: 'name',
			title: 'Name',
			sortable: true,
			mobileRole: 'title',
			cell: nameCellSnippet,
		},
		{
			key: 'address',
			title: 'Address',
			sortable: false,
			mobileRole: 'subtitle',
			accessor: (p) => `${p.addressLine1}, ${p.city}, ${p.state}`,
		},
		{
			key: 'type',
			title: 'Type',
			sortable: true,
			mobileRole: 'meta',
			accessor: (p) => formatPropertyType(p.type),
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCellSnippet,
		},
		{
			key: 'unitCount',
			title: 'Units',
			format: 'number',
			sortable: true,
			mobileRole: 'metric',
			accessor: (p) => p.unitCount ?? 0,
		},
		{
			key: 'occupiedUnits',
			title: 'Occupied',
			format: 'number',
			sortable: true,
			mobileRole: 'meta',
			accessor: (p) => p.occupiedUnits ?? 0,
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			align: 'right',
			width: '6rem',
			cell: actionsCellSnippet,
		},
	];
</script>

{#snippet nameCellSnippet(p: PropertyWithWorkspaceEntry)}
	<span data-testid="property-name">{p.name}</span>
{/snippet}

{#snippet statusCellSnippet(p: PropertyWithWorkspaceEntry)}
	<StatusBadge status={p.status} />
{/snippet}

{#snippet actionsCellSnippet(p: PropertyWithWorkspaceEntry)}
	{#if canManageRentals}
	<div class="flex items-center justify-end gap-1" onclick={(e) => e.stopPropagation()} role="none">
		<button
			type="button"
			data-testid="property-edit"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
			aria-label="Edit property"
			onclick={(e) => { e.stopPropagation(); openEdit(p); }}
		>
			<Pencil class="h-3.5 w-3.5" />
		</button>
		<button
			type="button"
			data-testid="property-delete"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-destructive/10 hover:text-destructive"
			aria-label="Delete property"
			onclick={(e) => { e.stopPropagation(); deleteTarget = p; }}
		>
			<Trash2 class="h-3.5 w-3.5" />
		</button>
	</div>
	{/if}
{/snippet}

<svelte:head>
	<title>Properties - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="properties-page">
	<PageHeader
		class="mb-4"
		band
		art={2}
		tone="sky"
		density="compact"
		eyebrow="Portfolio"
		title="Properties"
		description="Portfolio, units, and occupancy setup."
		data-testid="properties-header"
	/>

	<!-- data-coach anchor: the getting-started "add a unit" step lands here and spotlights the list so
	     the user opens a property, then adds units on its detail page. -->
	<div data-coach="open-property-for-units">
	{#if propertiesQuery.isError}
		<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="properties-list-error">
			<p class="font-medium text-destructive">Could not load properties.</p>
			<p class="mt-1 text-sm text-muted-foreground">Try again. An unavailable list is not the same as an empty portfolio.</p>
			<Button class="mt-4" variant="outline" onclick={() => propertiesQuery.refetch()}>Try again</Button>
		</div>
	{:else}
	<DataGrid
		data={list}
		{columns}
		loading={propertiesQuery.isLoading || propertiesQuery.isFetching}
		emptyMessage={emptyStateCopy.message}
		emptyDescription={emptyStateCopy.description}
		emptyIcon={Building}
		emptyActionLabel={emptyStateCopy.actionLabel}
		emptyOnAction={canCreateProperty ? openCreate : undefined}
		emptyTone="primary"
		onRowClick={openProperty}
		getRowKey={(p) => p.id}
		getRowTestId={() => 'property-row'}
		data-testid="properties-list"
		pageSize={PAGE_SIZE}
		page={gridPage}
		totalCount={totalCount}
		serverSide
		onPageChange={(page) => (gridPage = page)}
		sort={gridSort}
		onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
	>
		{#snippet toolbar()}
			<div class="flex flex-1 flex-wrap items-center gap-2">
				<SearchInput bind:value={search} placeholder="Search properties…" testid="property-search" />
				<Select.Root type="single" bind:value={typeValue}>
					<Select.Trigger class="h-9 w-40 text-sm" data-testid="property-type-filter">
						{typeFilter ? formatPropertyType(typeFilter) : 'All types'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value={ALL_TYPES} label="All types">All types</Select.Item>
						{#each propertyTypeOptions as option}
							<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				<Select.Root type="single" bind:value={statusValue}>
					<Select.Trigger class="h-9 w-44 text-sm" data-testid="property-status-filter">
						{statusFilter ? formatPropertyStatus(statusFilter) : 'All statuses'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value={ALL_TYPES} label="All statuses">All statuses</Select.Item>
						{#each propertyStatusOptions as option}
							<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			{#if canCreateProperty}
				<Button data-testid="property-create-button" data-coach="add-property" class="gap-2 shrink-0" onclick={openCreate}>
					<Plus class="h-4 w-4" />
					Add rental
				</Button>
			{/if}
		{/snippet}
	</DataGrid>
	{/if}
	</div>
</div>

<Dialog.Root open={canManageRentals && showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-h-[85vh] max-w-2xl overflow-y-auto [background:var(--m3c-surface-container-highest)]">
		<Dialog.Header>
			<Dialog.Title>Edit Property</Dialog.Title>
		</Dialog.Header>
		<FormStepper
			steps={propertySteps}
			bind:currentStep={propertyStep}
			completedSteps={completedPropertySteps}
			testid="property-stepper"
		>
			<div class="space-y-3" data-testid="property-form">
				{#if propertyStep === 0}
					<PropertyFields bind:form errors={formErrors} section="identity" rentalStructureLocked={editingId != null} />
				{:else if propertyStep === 1}
					<PropertyFields bind:form errors={formErrors} section="address" />
				{:else if propertyStep === 2}
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Status</span>
						<Select.Root type="single" bind:value={form.status}>
							<Select.Trigger class="w-full" data-testid="property-status-input">{form.status ? formatPropertyStatus(form.status) : 'Select status'}</Select.Trigger>
							<Select.Content>
								{#each propertyStatusOptions as option}
									<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
								{/each}
							</Select.Content>
						</Select.Root>
					</div>
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Apt / Suite / Unit #</span>
						<Input data-testid="property-address2-input" bind:value={form.addressLine2} placeholder="Apt / Suite / Unit # (optional)" />
					</div>
					<RemoteRecordSelect
						queryKey={['property-owner', portfolioId]}
						label="Owner"
						bind:value={form.ownerEntityId}
						selectedLabel={selectedOwnerLabel}
						placeholder="No owner assigned"
						clearLabel="No owner assigned"
						searchPlaceholder="Search owners…"
						emptyLabel="No matching owners"
						disabled={!canManageRentals}
						loadPage={loadOwnerOptions}
						onValueChange={(_value, option) => (selectedOwnerLabel = option?.label ?? null)}
						testid="property-owner-input"
					/>
				{:else if propertyStep === 3}
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Year built</span>
						<Input data-testid="property-year-built-input" bind:value={form.yearBuilt} type="number" inputmode="numeric" placeholder="1998" />
						{#if formErrors.yearBuilt}<p class="mt-1 text-xs text-destructive">{formErrors.yearBuilt}</p>{/if}
					</div>
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Management fee %</span>
						<Input data-testid="property-management-fee-input" bind:value={form.managementFeePercent} type="number" inputmode="decimal" placeholder="8" />
						{#if formErrors.managementFeePercent}<p class="mt-1 text-xs text-destructive">{formErrors.managementFeePercent}</p>{/if}
					</div>
					<div class="md:col-span-2">
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Notes</span>
						<textarea data-testid="property-notes-input" bind:value={form.notes} class="min-h-24 w-full rounded-md border border-input bg-background px-3 py-2 text-sm" maxlength="2000"></textarea>
						{#if formErrors.notes}<p class="mt-1 text-xs text-destructive">{formErrors.notes}</p>{/if}
					</div>
				{:else}
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Purchase price</span>
						<Input data-testid="property-purchase-price-input" bind:value={form.purchasePrice} type="number" inputmode="decimal" />
						{#if formErrors.purchasePrice}<p class="mt-1 text-xs text-destructive">{formErrors.purchasePrice}</p>{/if}
					</div>
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Land value</span>
						<Input data-testid="property-land-value-input" bind:value={form.landValue} type="number" inputmode="decimal" />
						{#if formErrors.landValue}<p class="mt-1 text-xs text-destructive">{formErrors.landValue}</p>{/if}
					</div>
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">In-service date</span>
						<DatePicker id="property-in-service-date-input" testid="property-in-service-date-input" bind:value={form.inServiceDate} bind:invalid={inServiceDateInvalid} />
						{#if formErrors.inServiceDate}<p class="mt-1 text-xs text-destructive">{formErrors.inServiceDate}</p>{/if}
					</div>
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Manual annual depreciation</span>
						<Input data-testid="property-manual-depreciation-input" bind:value={form.manualAnnualDepreciation} type="number" inputmode="decimal" />
						{#if formErrors.manualAnnualDepreciation}<p class="mt-1 text-xs text-destructive">{formErrors.manualAnnualDepreciation}</p>{/if}
					</div>
				{/if}
			</div>
		</FormStepper>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="property-form-cancel" variant="outline" onclick={closeForm}>Cancel</Button>
			{#if propertyStep > 0}
				<Button data-testid="property-step-back" variant="outline" onclick={() => (propertyStep = Math.max(propertyStep - 1, 0))}>Back</Button>
			{/if}
			{#if propertyStep < propertySteps.length - 1}
				<StepperNextButton
					testid="property-step-next"
					complete={completedPropertySteps.includes(propertyStep)}
					onclick={nextPropertyStep}
				/>
			{:else}
				<Button data-testid="property-form-save" onclick={submitProperty} disabled={savePropertyMutation.isPending || inServiceDateInvalid}>
					{savePropertyMutation.isPending ? 'Saving…' : 'Save property'}
				</Button>
			{/if}
		</div>
	</Dialog.Content>
</Dialog.Root>

<!-- Add rental: one screen on the list, so a new rental never ejects the landlord into the wizard. -->
<Dialog.Root open={canCreateProperty && showCreate} onOpenChange={(v) => { if (!v) closeCreate(); }}>
	<Dialog.Content class="max-h-[85vh] max-w-2xl overflow-y-auto [background:var(--m3c-surface-container-highest)]" data-testid="property-create-dialog">
		<Dialog.Header>
			<Dialog.Title>Add rental</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-4" data-testid="property-create-form">
			<div class="space-y-3" data-testid="property-create-essentials">
				<div>
					<span class="mb-1 block text-xs font-medium text-muted-foreground">Name</span>
					<Input data-testid="property-create-name-input" bind:value={createForm.name} placeholder="Maple Street House" />
					{#if createErrors.name}<p class="mt-1 text-xs text-destructive" data-testid="property-create-name-error">{createErrors.name}</p>{/if}
				</div>

				<PropertyFields bind:form={createForm} errors={createErrors} section="address" testidPrefix="property-create" />

				<label class="flex items-start gap-3 rounded-lg border border-border p-3">
					<Checkbox
						checked={createMultiple}
						onCheckedChange={(v) => (createMultiple = v === true)}
						data-testid="property-create-multiple-toggle"
					/>
					<span class="text-sm leading-tight">
						<span class="font-medium">This address has more than one rental</span>
						<span class="block text-xs text-muted-foreground">A duplex or apartment building, where each rental is rented separately.</span>
					</span>
				</label>

				{#if createMultiple}
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Name each rental</span>
						<div class="space-y-2" data-testid="property-create-unit-names">
							{#each createUnitNames as _unitName, index (index)}
								<div class="flex items-center gap-2">
									<Input
										data-testid={`property-create-unit-name-${index}`}
										bind:value={createUnitNames[index]}
										placeholder={`Unit ${index + 1}`}
										maxlength={50}
									/>
									{#if createUnitNames.length > 1}
										<button
											type="button"
											class="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
											aria-label="Remove this rental"
											data-testid={`property-create-unit-remove-${index}`}
											onclick={() => (createUnitNames = createUnitNames.filter((_, i) => i !== index))}
										>
											<X class="h-3.5 w-3.5" />
										</button>
									{/if}
								</div>
							{/each}
						</div>
						<Button
							class="mt-2 gap-2"
							variant="outline"
							size="sm"
							data-testid="property-create-unit-add"
							onclick={() => (createUnitNames = [...createUnitNames, ''])}
						>
							<Plus class="h-3.5 w-3.5" />
							Add another rental
						</Button>
						{#if createErrors.unitNames}<p class="mt-1 text-xs text-destructive" data-testid="property-create-unit-names-error">{createErrors.unitNames}</p>{/if}
						<p class="mt-2 text-xs text-muted-foreground">Set the rent for each one after they are added.</p>
					</div>
				{:else}
					<div class="grid gap-3 sm:grid-cols-2">
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Monthly rent</span>
							<Input data-testid="property-create-rent-input" bind:value={createRent} type="number" inputmode="decimal" placeholder="1450" />
							{#if createErrors.marketRent}<p class="mt-1 text-xs text-destructive" data-testid="property-create-rent-error">{createErrors.marketRent}</p>{/if}
						</div>
					</div>
				{/if}
			</div>

			<div class="border-t border-border pt-3">
				<button
					type="button"
					class="flex w-full items-center justify-between rounded px-1 py-2 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground"
					aria-expanded={createMoreOpen}
					aria-controls="property-more-details"
					onclick={() => (createMoreOpen = !createMoreOpen)}
					data-testid="property-more-details-toggle"
				>
					<span>More details</span>
					<ChevronDown class="h-4 w-4 transition-transform {createMoreOpen ? 'rotate-180' : ''}" />
				</button>
				{#if createMoreOpen}
					<div id="property-more-details" class="space-y-3 pt-2" data-testid="property-more-details">
						{#if !createMultiple}
							<div class="grid gap-3 sm:grid-cols-2">
								<div>
									<span class="mb-1 block text-xs font-medium text-muted-foreground">Beds</span>
									<Input data-testid="property-create-beds-input" bind:value={createBeds} type="number" inputmode="numeric" placeholder="3" />
									{#if createErrors.bedrooms}<p class="mt-1 text-xs text-destructive" data-testid="property-create-beds-error">{createErrors.bedrooms}</p>{/if}
								</div>
								<div>
									<span class="mb-1 block text-xs font-medium text-muted-foreground">Baths</span>
									<Input data-testid="property-create-baths-input" bind:value={createBaths} type="number" inputmode="decimal" placeholder="2" />
									{#if createErrors.bathrooms}<p class="mt-1 text-xs text-destructive" data-testid="property-create-baths-error">{createErrors.bathrooms}</p>{/if}
								</div>
							</div>
						{/if}
						<div class="grid gap-3 sm:grid-cols-2">
							<div>
								<span class="mb-1 block text-xs font-medium text-muted-foreground">Type</span>
								<Select.Root type="single" bind:value={createForm.type}>
									<Select.Trigger class="w-full" data-testid="property-create-type-input">{createForm.type ? formatPropertyType(createForm.type) : 'Select type'}</Select.Trigger>
									<Select.Content>
										{#each propertyTypeOptions as option}
											<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
										{/each}
									</Select.Content>
								</Select.Root>
							</div>
							<div>
								<span class="mb-1 block text-xs font-medium text-muted-foreground">Status</span>
								<Select.Root type="single" bind:value={createForm.status}>
									<Select.Trigger class="w-full" data-testid="property-create-status-input">{createForm.status ? formatPropertyStatus(createForm.status) : 'Select status'}</Select.Trigger>
									<Select.Content>
										{#each propertyStatusOptions as option}
											<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
										{/each}
									</Select.Content>
								</Select.Root>
							</div>
						</div>
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Apt / Suite / Unit #</span>
							<Input data-testid="property-create-address2-input" bind:value={createForm.addressLine2} placeholder="Apt / Suite / Unit # (optional)" />
							{#if createErrors.addressLine2}<p class="mt-1 text-xs text-destructive">{createErrors.addressLine2}</p>{/if}
						</div>
						<RemoteRecordSelect
							queryKey={['property-create-owner', portfolioId]}
							label="Owner"
							bind:value={createForm.ownerEntityId}
							selectedLabel={createOwnerLabel}
							placeholder="No owner assigned"
							clearLabel="No owner assigned"
							searchPlaceholder="Search owners…"
							emptyLabel="No matching owners"
							loadPage={loadOwnerOptions}
							onValueChange={(_value, option) => (createOwnerLabel = option?.label ?? null)}
							testid="property-create-owner-input"
						/>
						<div class="grid gap-3 sm:grid-cols-2">
							<div>
								<div class="mb-1 flex items-center gap-1.5">
									<span class="text-xs font-medium text-muted-foreground">Management fee %</span>
									<HelpPopover
										title="Management fee %"
										summary="The share of the rent a manager keeps for running this rental, so Rental Command can subtract it from what you take home."
										learnMoreUrl="/docs/property-details"
										testid="property-create-management-fee-help"
									/>
								</div>
								<Input data-testid="property-create-management-fee-input" bind:value={createForm.managementFeePercent} type="number" inputmode="decimal" placeholder="8" />
								{#if createErrors.managementFeePercent}<p class="mt-1 text-xs text-destructive">{createErrors.managementFeePercent}</p>{/if}
							</div>
							<div>
								<span class="mb-1 block text-xs font-medium text-muted-foreground">Year built</span>
								<Input data-testid="property-create-year-built-input" bind:value={createForm.yearBuilt} type="number" inputmode="numeric" placeholder="1998" />
								{#if createErrors.yearBuilt}<p class="mt-1 text-xs text-destructive">{createErrors.yearBuilt}</p>{/if}
							</div>
						</div>
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Notes</span>
							<textarea data-testid="property-create-notes-input" bind:value={createForm.notes} class="min-h-20 w-full rounded-md border border-input bg-background px-3 py-2 text-sm" maxlength="2000"></textarea>
							{#if createErrors.notes}<p class="mt-1 text-xs text-destructive">{createErrors.notes}</p>{/if}
						</div>
						<div class="grid gap-3 sm:grid-cols-2">
							<div>
								<span class="mb-1 block text-xs font-medium text-muted-foreground">Purchase price</span>
								<Input data-testid="property-create-purchase-price-input" bind:value={createForm.purchasePrice} type="number" inputmode="decimal" />
								{#if createErrors.purchasePrice}<p class="mt-1 text-xs text-destructive">{createErrors.purchasePrice}</p>{/if}
							</div>
							<div>
								<span class="mb-1 block text-xs font-medium text-muted-foreground">Land value</span>
								<Input data-testid="property-create-land-value-input" bind:value={createForm.landValue} type="number" inputmode="decimal" />
								{#if createErrors.landValue}<p class="mt-1 text-xs text-destructive">{createErrors.landValue}</p>{/if}
							</div>
						</div>
						<div class="grid gap-3 sm:grid-cols-2">
							<div>
								<div class="mb-1 flex items-center gap-1.5">
									<span class="text-xs font-medium text-muted-foreground">In-service date</span>
									<HelpPopover
										title="In-service date"
										summary="The day this rental was first ready to rent, which is when its yearly wear-and-tear write-off starts counting at tax time."
										learnMoreUrl="/docs/cost-basis-depreciation"
										testid="property-create-in-service-date-help"
									/>
								</div>
								<DatePicker id="property-create-in-service-date-input" testid="property-create-in-service-date-input" bind:value={createForm.inServiceDate} bind:invalid={createInServiceDateInvalid} />
								{#if createErrors.inServiceDate}<p class="mt-1 text-xs text-destructive">{createErrors.inServiceDate}</p>{/if}
							</div>
							<div>
								<span class="mb-1 block text-xs font-medium text-muted-foreground">Yearly write-off amount</span>
								<Input data-testid="property-create-manual-depreciation-input" bind:value={createForm.manualAnnualDepreciation} type="number" inputmode="decimal" />
								{#if createErrors.manualAnnualDepreciation}<p class="mt-1 text-xs text-destructive">{createErrors.manualAnnualDepreciation}</p>{/if}
							</div>
						</div>
					</div>
				{/if}
			</div>
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="property-create-cancel" variant="outline" onclick={closeCreate}>Cancel</Button>
			<Button data-testid="property-create-save" onclick={submitCreate} disabled={createPropertyMutation.isPending || createInServiceDateInvalid}>
				{createPropertyMutation.isPending ? 'Saving…' : 'Add rental'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={canManageRentals && deleteTarget !== null}
	title="Delete property"
	message={deleteState?.message ?? ''}
	busy={deletePropertyMutation.isPending}
	confirmDisabled={deleteState?.confirmDisabled ?? false}
	testid="property-delete"
	onconfirm={() => {
		if (!deleteTarget || deleteState?.confirmDisabled) return;
		deletePropertyMutation.mutate(deleteTarget.id);
	}}
	oncancel={() => (deleteTarget = null)}
/>
