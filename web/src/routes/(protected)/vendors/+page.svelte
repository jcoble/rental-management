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
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import StateSelect from '$lib/components/shared/StateSelect.svelte';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';

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
		is1099Eligible: true,
		w9OnFile: false,
		preferred: false
	};
	let showVendorForm = $state(false);
	let editingVendorId = $state<number | null>(null);
	let vendorForm = $state({ ...emptyVendor });
	let vendorErrors = $state<Record<string, string>>({});
	let vendorDeleteTarget = $state<Vendor | null>(null);
	let createParamHandled = $state(false);

	function clearVendorError(field: string) {
		const next = clearFieldError(vendorErrors, field);
		if (next !== vendorErrors) vendorErrors = next;
	}

	$effect(() => {
		if (vendorForm.name.trim()) clearVendorError('name');
	});
	$effect(() => {
		if (vendorForm.serviceType.trim()) clearVendorError('serviceType');
	});
	$effect(() => {
		if (!vendorForm.email.trim() || vendorForm.email.includes('@')) clearVendorError('email');
	});

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
		onError: (err) => showError(apiErrorMessage(err)),
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
			is1099Eligible: v.is1099Eligible,
			w9OnFile: v.w9OnFile,
			preferred: v.preferred
		};
		vendorErrors = {};
		showVendorForm = true;
	}
	function closeVendorForm() {
		showVendorForm = false;
		editingVendorId = null;
		vendorErrors = {};
	}
	function submitVendor() {
		const result = parseForm(vendorSchema, vendorForm);
		if (result.errors) {
			vendorErrors = result.errors;
			return;
		}
		vendorErrors = {};
		saveVendorMutation.mutate({ id: editingVendorId, data: { portfolioId, ...result.data } });
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
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>{editingVendorId == null ? 'New Vendor' : 'Edit Vendor'}</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-2" data-testid="vendor-form">
			<div>
				<Input data-testid="vendor-name-input" bind:value={vendorForm.name} placeholder="Vendor name" />
				{#if vendorErrors.name}<p class="mt-1 text-xs text-destructive" data-testid="vendor-name-error">{vendorErrors.name}</p>{/if}
			</div>
			<div>
				<Input data-testid="vendor-service-input" bind:value={vendorForm.serviceType} placeholder="Service type" />
				{#if vendorErrors.serviceType}<p class="mt-1 text-xs text-destructive" data-testid="vendor-service-error">{vendorErrors.serviceType}</p>{/if}
			</div>
			<div>
				<Input data-testid="vendor-email-input" bind:value={vendorForm.email} placeholder="Vendor email" type="email" autocomplete="email" />
				{#if vendorErrors.email}<p class="mt-1 text-xs text-destructive" data-testid="vendor-email-error">{vendorErrors.email}</p>{/if}
			</div>
			<Input data-testid="vendor-phone-input" bind:value={vendorForm.phone} placeholder="Vendor phone" type="tel" autocomplete="tel" inputmode="tel" mask="phone" />
			<div class="space-y-2">
				<AddressAutocomplete
					testid="vendor-address-input"
					bind:value={vendorForm.addressLine1}
					placeholder="Vendor address"
					onresolved={(a) => {
						if (a.city) vendorForm.city = a.city;
						if (a.state) vendorForm.state = a.state;
						if (a.zip) vendorForm.postalCode = a.zip;
					}}
				/>
				<div class="grid grid-cols-1 gap-2 sm:grid-cols-[1fr_8rem_9rem]">
					<Input data-testid="vendor-city-input" bind:value={vendorForm.city} placeholder="City" />
					<StateSelect
						testid="vendor-state-input"
						bind:value={vendorForm.state}
						placeholder="State"
					/>
					<Input data-testid="vendor-zip-input" bind:value={vendorForm.postalCode} placeholder="ZIP" inputmode="numeric" autocomplete="postal-code" maxlength={10} mask="zip" />
				</div>
			</div>
			<div class="flex flex-wrap gap-4 text-sm">
				<label class="flex items-center gap-1.5"><input data-testid="vendor-1099-input" type="checkbox" bind:checked={vendorForm.is1099Eligible} /> 1099 eligible</label>
				<label class="flex items-center gap-1.5"><input data-testid="vendor-w9-input" type="checkbox" bind:checked={vendorForm.w9OnFile} /> W-9 on file</label>
				<label class="flex items-center gap-1.5"><input data-testid="vendor-preferred-input" type="checkbox" bind:checked={vendorForm.preferred} /> Preferred</label>
			</div>
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="vendor-form-cancel" variant="outline" onclick={closeVendorForm}>Cancel</Button>
			<Button data-testid="vendor-form-save" onclick={submitVendor} disabled={saveVendorMutation.isPending}>{saveVendorMutation.isPending ? 'Saving…' : 'Save vendor'}</Button>
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
