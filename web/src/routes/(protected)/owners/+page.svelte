<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { owners } from '$lib/api/endpoints/owners';
	import type { Owner } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { ownerSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';
	import StateSelect from '$lib/components/shared/StateSelect.svelte';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import type { OwnerEntityType } from '$lib/types';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const OWNER_ENTITY_TYPES: OwnerEntityType[] = ['Person', 'LLC', 'Trust'];

	let ownerSearch = $state('');
	const debouncedOwnerSearch = debounced(() => ownerSearch, 300);

	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId, debouncedOwnerSearch.value],
		queryFn: () => owners.list(portfolioId, { search: debouncedOwnerSearch.value, take: 100 }),
	}));

	// --- Owner form/dialog ---
	const emptyOwner = { name: '', ownerEntityType: 'Person' as OwnerEntityType, taxId: '', addressLine1: '', addressLine2: '', city: '', state: '', postalCode: '', phone: '', email: '' };
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
		ownerForm = {
			name: o.name,
			ownerEntityType: o.ownerEntityType,
			taxId: o.taxId ?? '',
			// Back-compat: older owners only have the legacy single-line `address` — seed line 1 with it.
			addressLine1: o.addressLine1 ?? o.address ?? '',
			addressLine2: o.addressLine2 ?? '',
			city: o.city ?? '',
			state: o.state ?? '',
			postalCode: o.postalCode ?? '',
			phone: o.phone ?? '',
			email: o.email ?? '',
		};
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

	const ownersList = $derived(ownersQuery.data ?? []);

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

<svelte:head>
	<title>Owners - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="owners-page">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Owners</h1>
		<p class="text-sm text-muted-foreground">Ownership entities consumed by owner statements. Looking for service providers? They're under Vendors.</p>
	</div>

	<DataGrid
		data={ownersList}
		columns={ownerColumns}
		loading={ownersQuery.isLoading}
		emptyMessage="No owners found."
		getRowKey={(o) => o.id}
		getRowTestId={() => 'owner-row'}
		onRowClick={(o) => goto(`/owners/${o.id}`)}
		data-testid="owners-list"
	>
		{#snippet toolbar()}
			<div class="flex flex-1 items-center gap-2">
				<SearchInput bind:value={ownerSearch} placeholder="Search owners…" testid="owner-search" />
			</div>
			<Button data-testid="owner-create-button" data-coach="add-owner" class="gap-2 shrink-0" onclick={openCreateOwner}>
				<Plus class="h-4 w-4" />
				Add Owner
			</Button>
		{/snippet}
	</DataGrid>
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
			<AddressAutocomplete
				testid="owner-address-input"
				bind:value={ownerForm.addressLine1}
				placeholder="Address (optional)"
				onresolved={(a) => {
					if (a.city) ownerForm.city = a.city;
					if (a.state) ownerForm.state = a.state;
					if (a.zip) ownerForm.postalCode = a.zip;
				}}
			/>
			<Input data-testid="owner-address2-input" bind:value={ownerForm.addressLine2} placeholder="Apt / Suite / Unit # (optional)" />
			<div class="grid grid-cols-1 gap-2 sm:grid-cols-[1fr_auto_auto]">
				<Input data-testid="owner-city-input" bind:value={ownerForm.city} placeholder="City" />
				<StateSelect testid="owner-state-input" bind:value={ownerForm.state} placeholder="State" />
				<Input data-testid="owner-zip-input" bind:value={ownerForm.postalCode} placeholder="ZIP" />
			</div>
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

<ConfirmDialog
	open={ownerDeleteTarget !== null}
	title="Delete owner"
	message={ownerDeleteTarget ? `Delete "${ownerDeleteTarget.name}"?` : ''}
	busy={deleteOwnerMutation.isPending}
	testid="owner-delete"
	onconfirm={() => ownerDeleteTarget && deleteOwnerMutation.mutate(ownerDeleteTarget.id)}
	oncancel={() => (ownerDeleteTarget = null)}
/>
