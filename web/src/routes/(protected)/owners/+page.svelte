<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { owners } from '$lib/api/endpoints/owners';
	import { vendors } from '$lib/api/endpoints/vendors';
	import type { Owner, Vendor } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { ownerSchema, vendorSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import type { OwnerEntityType } from '$lib/types';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const OWNER_ENTITY_TYPES: OwnerEntityType[] = ['Person', 'LLC', 'Trust'];

	let ownerSearch = $state('');
	let vendorSearch = $state('');
	const debouncedOwnerSearch = debounced(() => ownerSearch, 300);
	const debouncedVendorSearch = debounced(() => vendorSearch, 300);

	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId, debouncedOwnerSearch.value],
		queryFn: () => owners.list(portfolioId, { search: debouncedOwnerSearch.value, take: 100 }),
	}));
	const vendorsQuery = createQuery(() => ({
		queryKey: ['vendors', portfolioId, debouncedVendorSearch.value],
		queryFn: () => vendors.list(portfolioId, { search: debouncedVendorSearch.value, take: 100 }),
	}));

	// --- Owner form/dialog ---
	const emptyOwner = { name: '', ownerEntityType: 'Person' as OwnerEntityType, taxId: '', address: '', phone: '', email: '' };
	let showOwnerForm = $state(false);
	let editingOwnerId = $state<number | null>(null);
	let ownerForm = $state({ ...emptyOwner });
	let ownerErrors = $state<Record<string, string>>({});
	let ownerDeleteTarget = $state<Owner | null>(null);

	function invalidateOwners() {
		queryClient.invalidateQueries({ queryKey: ['owners', portfolioId] });
	}

	const saveOwnerMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null ? owners.create(data) : owners.update(id, data),
		onSuccess: (_r, vars) => {
			showSuccess(vars.id == null ? 'Owner created.' : 'Owner updated.');
			closeOwnerForm();
			invalidateOwners();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteOwnerMutation = createMutation(() => ({
		mutationFn: (id: number) => owners.delete(id),
		onSuccess: () => {
			showSuccess('Owner deleted.');
			ownerDeleteTarget = null;
			invalidateOwners();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreateOwner() {
		editingOwnerId = null;
		ownerForm = { ...emptyOwner };
		ownerErrors = {};
		showOwnerForm = true;
	}
	function openEditOwner(o: Owner) {
		editingOwnerId = o.id;
		ownerForm = { name: o.name, ownerEntityType: o.ownerEntityType, taxId: o.taxId ?? '', address: o.address ?? '', phone: o.phone ?? '', email: o.email ?? '' };
		ownerErrors = {};
		showOwnerForm = true;
	}
	function closeOwnerForm() {
		showOwnerForm = false;
		editingOwnerId = null;
		ownerErrors = {};
	}
	function submitOwner() {
		const result = parseForm(ownerSchema, ownerForm);
		if (result.errors) {
			ownerErrors = result.errors;
			return;
		}
		ownerErrors = {};
		saveOwnerMutation.mutate({ id: editingOwnerId, data: { portfolioId, ...result.data } });
	}

	// --- Vendor form/dialog ---
	const emptyVendor = { name: '', serviceType: 'Plumbing', email: '', phone: '', is1099Eligible: true, w9OnFile: false, preferred: false };
	let showVendorForm = $state(false);
	let editingVendorId = $state<number | null>(null);
	let vendorForm = $state({ ...emptyVendor });
	let vendorErrors = $state<Record<string, string>>({});
	let vendorDeleteTarget = $state<Vendor | null>(null);

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
	function openEditVendor(v: Vendor) {
		editingVendorId = v.id;
		vendorForm = { name: v.name, serviceType: v.serviceType, email: v.email ?? '', phone: v.phone ?? '', is1099Eligible: v.is1099Eligible, w9OnFile: v.w9OnFile, preferred: v.preferred };
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

	const ownersList = $derived(ownersQuery.data ?? []);
	const vendorsList = $derived(vendorsQuery.data ?? []);

	// --- Owner columns ---
	const ownerColumns: ColumnDef<Owner>[] = [
		{
			key: 'name',
			title: 'Name',
			sortable: true,
			mobileRole: 'title',
			accessor: (o) => o.name,
			cell: ownerNameCell,
		},
		{
			key: 'ownerEntityType',
			title: 'Type',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (o) => o.ownerEntityType,
		},
		{
			key: 'taxId',
			title: 'Tax ID',
			mobileRole: 'meta',
			accessor: (o) => o.taxId ?? '—',
		},
		{
			key: 'phone',
			title: 'Phone',
			mobileRole: 'meta',
			accessor: (o) => o.phone ?? '—',
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			align: 'right',
			width: '6rem',
			cell: ownerActionsCell,
		},
	];

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

{#snippet ownerNameCell(o: Owner)}
	<span data-testid="owner-name">{o.name}</span>
{/snippet}

{#snippet ownerActionsCell(o: Owner)}
	<div class="flex items-center justify-end gap-1" onclick={(e) => e.stopPropagation()} role="none">
		<button
			type="button"
			data-testid="owner-edit"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
			aria-label="Edit owner"
			onclick={(e) => { e.stopPropagation(); openEditOwner(o); }}
		>
			<Pencil class="h-3.5 w-3.5" />
		</button>
		<button
			type="button"
			data-testid="owner-delete"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-destructive/10 hover:text-destructive"
			aria-label="Delete owner"
			onclick={(e) => { e.stopPropagation(); ownerDeleteTarget = o; }}
		>
			<Trash2 class="h-3.5 w-3.5" />
		</button>
	</div>
{/snippet}

{#snippet vendorNameCell(v: Vendor)}
	<span data-testid="vendor-name">{v.name}</span>
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
	<title>Owners & Vendors - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="owners-page">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Owners & Vendors</h1>
		<p class="text-sm text-muted-foreground">Manage ownership contacts and service provider compliance data.</p>
	</div>

	<div class="space-y-6">
		<DataGrid
			data={ownersList}
			columns={ownerColumns}
			loading={ownersQuery.isLoading}
			emptyMessage="No owners found."
			getRowKey={(o) => o.id}
			getRowTestId={() => 'owner-row'}
			data-testid="owners-list"
		>
			{#snippet toolbar()}
				<div class="flex flex-1 items-center gap-2">
					<SearchInput bind:value={ownerSearch} placeholder="Search owners…" testid="owner-search" />
				</div>
				<Button data-testid="owner-create-button" class="gap-2 shrink-0" onclick={openCreateOwner}>
					<Plus class="h-4 w-4" />
					Add Owner
				</Button>
			{/snippet}
		</DataGrid>

		<DataGrid
			data={vendorsList}
			columns={vendorColumns}
			loading={vendorsQuery.isLoading}
			emptyMessage="No vendors found."
			getRowKey={(v) => v.id}
			getRowTestId={() => 'vendor-row'}
			data-testid="vendors-list"
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
</div>

<Dialog.Root open={showOwnerForm} onOpenChange={(v) => { if (!v) closeOwnerForm(); }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>{editingOwnerId == null ? 'New Owner' : 'Edit Owner'}</Dialog.Title>
		</Dialog.Header>
		<div class="space-y-2" data-testid="owner-form">
			<div>
				<Input data-testid="owner-name-input" bind:value={ownerForm.name} placeholder="Owner name" />
				{#if ownerErrors.name}<p class="mt-1 text-xs text-destructive" data-testid="owner-name-error">{ownerErrors.name}</p>{/if}
			</div>
			<div>
				<span class="mb-1 block text-xs text-muted-foreground">Type</span>
				<Select.Root type="single" bind:value={ownerForm.ownerEntityType}>
					<Select.Trigger class="w-full" data-testid="owner-type-input">
						{ownerForm.ownerEntityType || 'Select type'}
					</Select.Trigger>
					<Select.Content>
						{#each OWNER_ENTITY_TYPES as t}
							<Select.Item value={t} label={t}>{t}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</div>
			<Input data-testid="owner-taxid-input" bind:value={ownerForm.taxId} placeholder="Tax ID / EIN (optional)" />
			<Input data-testid="owner-address-input" bind:value={ownerForm.address} placeholder="Address (optional)" />
			<Input data-testid="owner-phone-input" bind:value={ownerForm.phone} placeholder="Phone (optional)" />
			<div>
				<Input data-testid="owner-email-input" bind:value={ownerForm.email} type="email" placeholder="Email (optional)" />
				{#if ownerErrors.email}<p class="mt-1 text-xs text-destructive" data-testid="owner-email-error">{ownerErrors.email}</p>{/if}
			</div>
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="owner-form-cancel" variant="outline" onclick={closeOwnerForm}>Cancel</Button>
			<Button data-testid="owner-form-save" onclick={submitOwner} disabled={saveOwnerMutation.isPending}>{saveOwnerMutation.isPending ? 'Saving…' : 'Save Owner'}</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root open={showVendorForm} onOpenChange={(v) => { if (!v) closeVendorForm(); }}>
	<Dialog.Content class="max-w-md">
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
				<Input data-testid="vendor-email-input" bind:value={vendorForm.email} placeholder="Vendor email" />
				{#if vendorErrors.email}<p class="mt-1 text-xs text-destructive" data-testid="vendor-email-error">{vendorErrors.email}</p>{/if}
			</div>
			<Input data-testid="vendor-phone-input" bind:value={vendorForm.phone} placeholder="Vendor phone" />
			<div class="flex flex-wrap gap-4 text-sm">
				<label class="flex items-center gap-1.5"><input data-testid="vendor-1099-input" type="checkbox" bind:checked={vendorForm.is1099Eligible} /> 1099 eligible</label>
				<label class="flex items-center gap-1.5"><input data-testid="vendor-w9-input" type="checkbox" bind:checked={vendorForm.w9OnFile} /> W-9 on file</label>
				<label class="flex items-center gap-1.5"><input data-testid="vendor-preferred-input" type="checkbox" bind:checked={vendorForm.preferred} /> Preferred</label>
			</div>
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="vendor-form-cancel" variant="outline" onclick={closeVendorForm}>Cancel</Button>
			<Button data-testid="vendor-form-save" onclick={submitVendor} disabled={saveVendorMutation.isPending}>{saveVendorMutation.isPending ? 'Saving…' : 'Save Vendor'}</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={ownerDeleteTarget !== null}
	title="Delete owner"
	message={ownerDeleteTarget ? `Delete "${ownerDeleteTarget.name}"?` : ''}
	busy={deleteOwnerMutation.isPending}
	testid="owner-delete"
	onconfirm={() => ownerDeleteTarget && deleteOwnerMutation.mutate(ownerDeleteTarget.id)}
	oncancel={() => (ownerDeleteTarget = null)}
/>
<ConfirmDialog
	open={vendorDeleteTarget !== null}
	title="Delete vendor"
	message={vendorDeleteTarget ? `Delete "${vendorDeleteTarget.name}"?` : ''}
	busy={deleteVendorMutation.isPending}
	testid="vendor-delete"
	onconfirm={() => vendorDeleteTarget && deleteVendorMutation.mutate(vendorDeleteTarget.id)}
	oncancel={() => (vendorDeleteTarget = null)}
/>
