<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { vendors } from '$lib/api/endpoints/vendors';
	import type { Vendor } from '$lib/types';
	import StarRating from '$lib/components/shared/StarRating.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { vendorSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import StepperNextButton from '$lib/components/shared/StepperNextButton.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import { formErrorsFromApiError } from '$lib/forms/form-errors';
	import VendorFields from '$lib/components/forms/VendorFields.svelte';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { withBlanksAsEmpty } from '$lib/utils/with-blanks-as-empty';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;

	// Search / sort / page persisted in the URL so they survive navigating away and back. Sort/page seed
	// the server-side DataGrid query so vendors are filtered/sorted/paged in SQL.
	const initialParams = page.url.searchParams;
	let vendorSearch = $state(readGridParam(initialParams, 'q'));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));
	const debouncedVendorSearch = debounced(() => vendorSearch, 300);

	// Reset to page 1 when the search changes — but not on initial mount.
	let filterResetPrimed = false;
	$effect(() => {
		vendorSearch;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl({ q: vendorSearch, sort: gridSort, page: gridPage }, { page: 1 });
	});

	const vendorsQuery = createQuery(() => ({
		queryKey: ['vendors', portfolioId, 'page', debouncedVendorSearch.value, gridSort, gridPage, PAGE_SIZE],
		queryFn: () => vendors.listPage(portfolioId, {
			search: debouncedVendorSearch.value,
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		}),
	}));

	// --- Vendor form/dialog ---
	const emptyVendor = {
		name: '',
		serviceType: 'Plumbing',
		addressLine1: '',
		city: '',
		state: '',
		postalCode: '',
		email: '',
		phone: '',
		website: '',
		is1099Eligible: true,
		w9OnFile: false,
		preferred: false
	};
	let showVendorForm = $state(false);
	let editingVendorId = $state<number | null>(null);
	let vendorForm = $state({ ...emptyVendor });
	let vendorErrors = $state<Record<string, string>>({});
	let vendorStep = $state(0);
	let completedVendorSteps = $state<number[]>([]);
	let vendorDeleteTarget = $state<Vendor | null>(null);
	let createParamHandled = $state(false);

	const vendorSteps: FormStepperStep[] = [
		{ id: 'basics', label: 'Basics', description: 'Name and service' },
		{ id: 'contact', label: 'Contact', description: 'Email and website' },
		{ id: 'address', label: 'Address', description: 'Mailing details' },
		{ id: 'compliance', label: 'Compliance', description: '1099 and W-9' },
	];
	const vendorStepFields = [
		['name', 'serviceType'],
		['email', 'phone', 'website'],
		['addressLine1', 'city', 'state', 'postalCode'],
		['is1099Eligible', 'w9OnFile', 'preferred'],
	] as const;

	function invalidateVendors() {
		queryClient.invalidateQueries({ queryKey: ['vendors', portfolioId] });
	}

	const saveVendorMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null ? vendors.create(data) : vendors.update(id, data),
		onSuccess: (_r, vars) => {
			showSuccess(vars.id == null ? 'Vendor created.' : 'Vendor updated.');
			closeVendorForm();
			invalidateVendors();
		},
		onError: (err) => {
			const validationErrors = formErrorsFromApiError(err);
			if (Object.keys(validationErrors).length > 0) {
				vendorErrors = validationErrors;
				const firstErrorStep = firstVendorErrorStep(validationErrors);
				if (firstErrorStep >= 0) {
					vendorStep = firstErrorStep;
					markVendorStepInvalid(firstErrorStep);
				}
				return;
			}
			showError(apiErrorMessage(err));
		},
	}));

	const deleteVendorMutation = createMutation(() => ({
		mutationFn: (id: number) => vendors.delete(id),
		onSuccess: () => {
			showSuccess('Vendor deleted.');
			vendorDeleteTarget = null;
			invalidateVendors();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreateVendor() {
		editingVendorId = null;
		vendorForm = { ...emptyVendor };
		vendorErrors = {};
		vendorStep = 0;
		completedVendorSteps = [];
		showVendorForm = true;
	}

	$effect(() => {
		if (createParamHandled || page.url.searchParams.get('create') !== '1') return;
		createParamHandled = true;
		openCreateVendor();
	});

	function openEditVendor(v: Vendor) {
		editingVendorId = v.id;
		vendorForm = {
			name: v.name,
			serviceType: v.serviceType,
			addressLine1: v.addressLine1 ?? '',
			city: v.city ?? '',
			state: v.state ?? '',
			postalCode: v.postalCode ?? '',
			email: v.email ?? '',
			phone: v.phone ?? '',
			website: v.website ?? '',
			is1099Eligible: v.is1099Eligible,
			w9OnFile: v.w9OnFile,
			preferred: v.preferred
		};
		vendorErrors = {};
		vendorStep = 0;
		completedVendorSteps = [];
		showVendorForm = true;
	}
	function closeVendorForm() {
		showVendorForm = false;
		editingVendorId = null;
		vendorErrors = {};
		vendorStep = 0;
		completedVendorSteps = [];
	}
	function vendorStepErrorFields(step: number, errors: Record<string, string>) {
		const visibleFields = new Set<string>(vendorStepFields[step] ?? []);
		return Object.entries(errors).filter(([field]) => visibleFields.has(field));
	}
	function firstVendorErrorStep(errors: Record<string, string>) {
		return vendorStepFields.findIndex((fields) => fields.some((field) => errors[field]));
	}
	function markVendorStepInvalid(step: number) {
		completedVendorSteps = completedVendorSteps.filter((completedStep) => completedStep < step);
	}
	function validateVendorStep(step: number) {
		const result = parseForm(vendorSchema, vendorForm);
		const currentErrors = result.errors ? Object.fromEntries(vendorStepErrorFields(step, result.errors)) : {};
		const currentFields = new Set<string>(vendorStepFields[step] ?? []);
		const nextErrors = Object.fromEntries(Object.entries(vendorErrors).filter(([field]) => !currentFields.has(field)));
		vendorErrors = { ...nextErrors, ...currentErrors };
		const isValid = Object.keys(currentErrors).length === 0;
		if (!isValid) markVendorStepInvalid(step);
		return isValid;
	}
	function nextVendorStep() {
		if (!validateVendorStep(vendorStep)) return;
		if (completedVendorSteps.includes(vendorStep)) {
			vendorStep = Math.min(vendorStep + 1, vendorSteps.length - 1);
			return;
		}
		completedVendorSteps = [...completedVendorSteps, vendorStep];
		window.setTimeout(() => {
			vendorStep = Math.min(vendorStep + 1, vendorSteps.length - 1);
		}, 260);
	}
	function submitVendor() {
		const result = parseForm(vendorSchema, vendorForm);
		if (result.errors) {
			vendorErrors = result.errors;
			const firstErrorStep = firstVendorErrorStep(result.errors);
			if (firstErrorStep >= 0) {
				vendorStep = firstErrorStep;
				markVendorStepInvalid(firstErrorStep);
			}
			return;
		}
		vendorErrors = {};
		saveVendorMutation.mutate({
			id: editingVendorId,
			data: { portfolioId, ...(editingVendorId == null ? result.data : withBlanksAsEmpty(result.data)) }
		});
	}

	const vendorsList = $derived(vendorsQuery.data?.items ?? []);
	const vendorsTotalCount = $derived(vendorsQuery.data?.totalCount ?? 0);

	// --- Vendor columns ---
	const vendorColumns: ColumnDef<Vendor>[] = [
		{
			key: 'name',
			title: 'Name',
			sortable: true,
			mobileRole: 'title',
			accessor: (v) => v.name,
			cell: vendorNameCell,
		},
		{
			key: 'serviceType',
			title: 'Category',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (v) => v.serviceType,
		},
		{
			key: 'rating',
			title: 'Rating',
			mobileRole: 'meta',
			accessor: (v) =>
				v.averageRating != null && v.ratingCount > 0
					? `${v.averageRating.toFixed(1)} stars · ${v.jobsCompleted} jobs done`
					: 'No ratings yet',
			cell: vendorRatingCell,
		},
		{
			key: 'contact',
			title: 'Contact',
			mobileRole: 'meta',
			accessor: (v) => [v.email, v.phone].filter(Boolean).join(' · ') || '—',
		},
		{
			key: 'compliance',
			title: 'Compliance',
			mobileRole: 'meta',
			accessor: (v) => `1099: ${v.is1099Eligible ? 'Yes' : 'No'} · W-9: ${v.w9OnFile ? 'On file' : 'Missing'}`,
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			align: 'right',
			width: '6rem',
			cell: vendorActionsCell,
		},
	];
</script>

{#snippet vendorNameCell(v: Vendor)}
	<span data-testid="vendor-name">{v.name}</span>
{/snippet}

{#snippet vendorRatingCell(v: Vendor)}
	{#if v.averageRating != null && v.ratingCount > 0}
		<span class="flex items-center gap-1.5" data-testid="vendor-rating">
			<StarRating value={v.averageRating} size="sm" testid="vendor-rating-stars" />
			<span class="text-xs text-muted-foreground tabular-nums">{v.averageRating.toFixed(1)} · {v.jobsCompleted} done</span>
		</span>
	{:else}
		<span class="text-xs text-muted-foreground" data-testid="vendor-rating">No ratings yet</span>
	{/if}
{/snippet}

{#snippet vendorActionsCell(v: Vendor)}
	<div class="flex items-center justify-end gap-1" onclick={(e) => e.stopPropagation()} role="none">
		<button
			type="button"
			data-testid="vendor-edit"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
			aria-label="Edit vendor"
			onclick={(e) => { e.stopPropagation(); openEditVendor(v); }}
		>
			<Pencil class="h-3.5 w-3.5" />
		</button>
		<button
			type="button"
			data-testid="vendor-delete"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-destructive/10 hover:text-destructive"
			aria-label="Delete vendor"
			onclick={(e) => { e.stopPropagation(); vendorDeleteTarget = v; }}
		>
			<Trash2 class="h-3.5 w-3.5" />
		</button>
	</div>
{/snippet}

<svelte:head>
	<title>Vendors - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="vendors-page">
	<PageHeader
		class="mb-4"
		band
		art={10}
		tone="coral"
		eyebrow="Work"
		title="Vendors"
		description="Service providers, ratings, and 1099/W-9 compliance."
		data-testid="vendors-header"
	/>

	<DataGrid
		data={vendorsList}
		columns={vendorColumns}
		loading={vendorsQuery.isLoading || vendorsQuery.isFetching}
		emptyMessage="No vendors found."
		getRowKey={(v) => v.id}
		getRowTestId={() => 'vendor-row'}
		onRowClick={(v) => goto(`/vendors/${v.id}`)}
		data-testid="vendors-list"
		pageSize={PAGE_SIZE}
		page={gridPage}
		totalCount={vendorsTotalCount}
		serverSide
		onPageChange={(page) => (gridPage = page)}
		sort={gridSort}
		onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
		mobileActions={vendorActionsCell}
	>
		{#snippet toolbar()}
			<div class="flex flex-1 items-center gap-2">
				<SearchInput bind:value={vendorSearch} placeholder="Search vendors…" testid="vendor-search" />
			</div>
			<Button data-testid="vendor-create-button" class="gap-2 shrink-0" onclick={openCreateVendor}>
				<Plus class="h-4 w-4" />
				Add Vendor
			</Button>
		{/snippet}
	</DataGrid>
</div>

<Dialog.Root open={showVendorForm} onOpenChange={(v) => { if (!v) closeVendorForm(); }}>
	<Dialog.Content class="max-h-[85vh] max-w-2xl overflow-y-auto [background:var(--m3c-surface-container-highest)]">
		<Dialog.Header>
			<Dialog.Title>{editingVendorId == null ? 'New Vendor' : 'Edit Vendor'}</Dialog.Title>
		</Dialog.Header>
		<FormStepper
			steps={vendorSteps}
			bind:currentStep={vendorStep}
			completedSteps={completedVendorSteps}
			testid="vendor-stepper"
		>
			<VendorFields bind:form={vendorForm} bind:errors={vendorErrors} step={vendorStep} testidPrefix="vendor" />
		</FormStepper>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="vendor-form-cancel" variant="outline" onclick={closeVendorForm}>Cancel</Button>
			{#if vendorStep > 0}
				<Button data-testid="vendor-step-back" variant="outline" onclick={() => (vendorStep = Math.max(vendorStep - 1, 0))}>Back</Button>
				{/if}
				{#if vendorStep < vendorSteps.length - 1}
					<StepperNextButton
						testid="vendor-step-next"
						onclick={nextVendorStep}
						complete={completedVendorSteps.includes(vendorStep)}
					/>
				{:else}
				<Button data-testid="vendor-form-save" onclick={submitVendor} disabled={saveVendorMutation.isPending}>{saveVendorMutation.isPending ? 'Saving…' : 'Save vendor'}</Button>
			{/if}
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={vendorDeleteTarget !== null}
	title="Delete vendor"
	message={vendorDeleteTarget ? `Delete "${vendorDeleteTarget.name}"?` : ''}
	busy={deleteVendorMutation.isPending}
	testid="vendor-delete"
	onconfirm={() => vendorDeleteTarget && deleteVendorMutation.mutate(vendorDeleteTarget.id)}
	oncancel={() => (vendorDeleteTarget = null)}
/>
