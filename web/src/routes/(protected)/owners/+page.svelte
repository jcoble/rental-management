<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { owners } from '$lib/api/endpoints/owners';
	import { vendors } from '$lib/api/endpoints/vendors';
	import type { Owner, Vendor } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { ownerSchema, vendorSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import Dialog from '$lib/components/ui/Dialog.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

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
	const emptyOwner = { name: '', email: '', phone: '' };
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
		ownerForm = { name: o.name, email: o.email ?? '', phone: o.phone ?? '' };
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
	const inputClass = 'rounded border border-border bg-bg px-3 py-2 text-sm';
</script>

<svelte:head>
	<title>Owners & Vendors - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="owners-page">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Owners & Vendors</h1>
		<p class="text-sm text-text-secondary">Manage ownership contacts and service provider compliance data.</p>
	</div>

	<div class="grid gap-4 lg:grid-cols-2">
		<div class="rounded-lg border border-border bg-surface">
			<div class="flex items-center justify-between border-b border-border px-4 py-3">
				<span class="font-semibold">Owners</span>
				<button data-testid="owner-create-button" class="inline-flex items-center gap-1 rounded bg-accent px-2.5 py-1.5 text-xs text-white" onclick={openCreateOwner}><Plus class="h-3.5 w-3.5" /> Add</button>
			</div>
			<div class="border-b border-border px-3 py-2"><SearchInput bind:value={ownerSearch} placeholder="Search owners…" testid="owner-search" /></div>
			<div class="space-y-2 p-3" data-testid="owners-list">
				{#if ownersList.length === 0}
					<p class="py-4 text-center text-sm text-text-secondary" data-testid="owners-empty">No owners found.</p>
				{:else}
					{#each ownersList as owner (owner.id)}
						<div class="rounded border border-border bg-bg p-3 text-sm" data-testid="owner-row" data-owner-id={owner.id}>
							<div class="flex items-start justify-between gap-2">
								<div class="min-w-0">
									<p class="truncate font-medium" data-testid="owner-name">{owner.name}</p>
									<p class="text-xs text-text-secondary">{owner.email || 'No email'} · {owner.phone || 'No phone'}</p>
									<p class="text-xs text-text-secondary">{owner.propertyCount || 0} properties</p>
								</div>
								<div class="flex shrink-0 gap-1">
									<button data-testid="owner-edit" aria-label="Edit owner" class="rounded p-1.5 text-text-tertiary hover:bg-surface-hover hover:text-text-primary" onclick={() => openEditOwner(owner)}><Pencil class="h-4 w-4" /></button>
									<button data-testid="owner-delete" aria-label="Delete owner" class="rounded p-1.5 text-text-tertiary hover:bg-surface-hover hover:text-danger" onclick={() => (ownerDeleteTarget = owner)}><Trash2 class="h-4 w-4" /></button>
								</div>
							</div>
						</div>
					{/each}
				{/if}
			</div>
		</div>

		<div class="rounded-lg border border-border bg-surface">
			<div class="flex items-center justify-between border-b border-border px-4 py-3">
				<span class="font-semibold">Vendors</span>
				<button data-testid="vendor-create-button" class="inline-flex items-center gap-1 rounded bg-accent px-2.5 py-1.5 text-xs text-white" onclick={openCreateVendor}><Plus class="h-3.5 w-3.5" /> Add</button>
			</div>
			<div class="border-b border-border px-3 py-2"><SearchInput bind:value={vendorSearch} placeholder="Search vendors…" testid="vendor-search" /></div>
			<div class="space-y-2 p-3" data-testid="vendors-list">
				{#if vendorsList.length === 0}
					<p class="py-4 text-center text-sm text-text-secondary" data-testid="vendors-empty">No vendors found.</p>
				{:else}
					{#each vendorsList as vendor (vendor.id)}
						<div class="rounded border border-border bg-bg p-3 text-sm" data-testid="vendor-row" data-vendor-id={vendor.id}>
							<div class="flex items-start justify-between gap-2">
								<div class="min-w-0">
									<p class="truncate font-medium" data-testid="vendor-name">{vendor.name}</p>
									<p class="text-xs text-text-secondary">{vendor.serviceType}</p>
									<p class="text-xs text-text-secondary">{vendor.email || 'No email'} · {vendor.phone || 'No phone'}</p>
									<p class="text-xs text-text-secondary">1099: {vendor.is1099Eligible ? 'Yes' : 'No'} · W-9: {vendor.w9OnFile ? 'On file' : 'Missing'}</p>
								</div>
								<div class="flex shrink-0 gap-1">
									<button data-testid="vendor-edit" aria-label="Edit vendor" class="rounded p-1.5 text-text-tertiary hover:bg-surface-hover hover:text-text-primary" onclick={() => openEditVendor(vendor)}><Pencil class="h-4 w-4" /></button>
									<button data-testid="vendor-delete" aria-label="Delete vendor" class="rounded p-1.5 text-text-tertiary hover:bg-surface-hover hover:text-danger" onclick={() => (vendorDeleteTarget = vendor)}><Trash2 class="h-4 w-4" /></button>
								</div>
							</div>
						</div>
					{/each}
				{/if}
			</div>
		</div>
	</div>
</div>

<Dialog open={showOwnerForm} title={editingOwnerId == null ? 'New Owner' : 'Edit Owner'} class="max-w-md" onclose={closeOwnerForm}>
	<div class="space-y-2" data-testid="owner-form">
		<div>
			<input data-testid="owner-name-input" bind:value={ownerForm.name} class="{inputClass} w-full" placeholder="Owner name" />
			{#if ownerErrors.name}<p class="mt-1 text-xs text-danger" data-testid="owner-name-error">{ownerErrors.name}</p>{/if}
		</div>
		<div>
			<input data-testid="owner-email-input" bind:value={ownerForm.email} class="{inputClass} w-full" placeholder="Owner email" />
			{#if ownerErrors.email}<p class="mt-1 text-xs text-danger" data-testid="owner-email-error">{ownerErrors.email}</p>{/if}
		</div>
		<input data-testid="owner-phone-input" bind:value={ownerForm.phone} class="{inputClass} w-full" placeholder="Owner phone" />
	</div>
	<div class="mt-4 flex justify-end gap-2">
		<button data-testid="owner-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-text-secondary hover:bg-surface-hover" onclick={closeOwnerForm}>Cancel</button>
		<button data-testid="owner-form-save" onclick={submitOwner} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={saveOwnerMutation.isPending}>{saveOwnerMutation.isPending ? 'Saving…' : 'Save Owner'}</button>
	</div>
</Dialog>

<Dialog open={showVendorForm} title={editingVendorId == null ? 'New Vendor' : 'Edit Vendor'} class="max-w-md" onclose={closeVendorForm}>
	<div class="space-y-2" data-testid="vendor-form">
		<div>
			<input data-testid="vendor-name-input" bind:value={vendorForm.name} class="{inputClass} w-full" placeholder="Vendor name" />
			{#if vendorErrors.name}<p class="mt-1 text-xs text-danger" data-testid="vendor-name-error">{vendorErrors.name}</p>{/if}
		</div>
		<div>
			<input data-testid="vendor-service-input" bind:value={vendorForm.serviceType} class="{inputClass} w-full" placeholder="Service type" />
			{#if vendorErrors.serviceType}<p class="mt-1 text-xs text-danger" data-testid="vendor-service-error">{vendorErrors.serviceType}</p>{/if}
		</div>
		<div>
			<input data-testid="vendor-email-input" bind:value={vendorForm.email} class="{inputClass} w-full" placeholder="Vendor email" />
			{#if vendorErrors.email}<p class="mt-1 text-xs text-danger" data-testid="vendor-email-error">{vendorErrors.email}</p>{/if}
		</div>
		<input data-testid="vendor-phone-input" bind:value={vendorForm.phone} class="{inputClass} w-full" placeholder="Vendor phone" />
		<div class="flex flex-wrap gap-4 text-sm">
			<label class="flex items-center gap-1.5"><input data-testid="vendor-1099-input" type="checkbox" bind:checked={vendorForm.is1099Eligible} /> 1099 eligible</label>
			<label class="flex items-center gap-1.5"><input data-testid="vendor-w9-input" type="checkbox" bind:checked={vendorForm.w9OnFile} /> W-9 on file</label>
			<label class="flex items-center gap-1.5"><input data-testid="vendor-preferred-input" type="checkbox" bind:checked={vendorForm.preferred} /> Preferred</label>
		</div>
	</div>
	<div class="mt-4 flex justify-end gap-2">
		<button data-testid="vendor-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-text-secondary hover:bg-surface-hover" onclick={closeVendorForm}>Cancel</button>
		<button data-testid="vendor-form-save" onclick={submitVendor} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={saveVendorMutation.isPending}>{saveVendorMutation.isPending ? 'Saving…' : 'Save Vendor'}</button>
	</div>
</Dialog>

<ConfirmDialog
	open={ownerDeleteTarget !== null}
	title="Delete owner"
	message={ownerDeleteTarget ? `Delete “${ownerDeleteTarget.name}”?` : ''}
	busy={deleteOwnerMutation.isPending}
	testid="owner-delete"
	onconfirm={() => ownerDeleteTarget && deleteOwnerMutation.mutate(ownerDeleteTarget.id)}
	oncancel={() => (ownerDeleteTarget = null)}
/>
<ConfirmDialog
	open={vendorDeleteTarget !== null}
	title="Delete vendor"
	message={vendorDeleteTarget ? `Delete “${vendorDeleteTarget.name}”?` : ''}
	busy={deleteVendorMutation.isPending}
	testid="vendor-delete"
	onconfirm={() => vendorDeleteTarget && deleteVendorMutation.mutate(vendorDeleteTarget.id)}
	oncancel={() => (vendorDeleteTarget = null)}
/>
