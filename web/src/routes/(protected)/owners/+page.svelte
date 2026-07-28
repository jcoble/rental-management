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
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import StepperNextButton from '$lib/components/shared/StepperNextButton.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';
	import StateSelect from '$lib/components/shared/StateSelect.svelte';
	import { Plus, Pencil, Trash2, UserCheck } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import type { OwnerEntityType } from '$lib/types';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;

	const OWNER_ENTITY_TYPES: OwnerEntityType[] = ['Person', 'LLC', 'Trust'];

	// Search / sort / page persisted in the URL so they survive navigating away and back. Sort/page seed
	// the server-side DataGrid query so owners are filtered/sorted/paged in SQL.
	const initialParams = page.url.searchParams;
	let ownerSearch = $state(readGridParam(initialParams, 'q'));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));
	const debouncedOwnerSearch = debounced(() => ownerSearch, 300);

	// Reset to page 1 when the search changes — but not on initial mount.
	let filterResetPrimed = false;
	$effect(() => {
		ownerSearch;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl({ q: ownerSearch, sort: gridSort, page: gridPage }, { page: 1 });
	});

	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId, 'page', debouncedOwnerSearch.value, gridSort, gridPage, PAGE_SIZE],
		queryFn: () => owners.listPage(portfolioId, {
			search: debouncedOwnerSearch.value,
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		}),
	}));

	// --- Owner form/dialog ---
	const emptyOwner = { name: '', ownerEntityType: 'Person' as OwnerEntityType, taxId: '', addressLine1: '', addressLine2: '', city: '', state: '', postalCode: '', phone: '', email: '' };
	let showOwnerForm = $state(false);
	let editingOwnerId = $state<number | null>(null);
	let ownerForm = $state({ ...emptyOwner });
	let ownerErrors = $state<Record<string, string>>({});
	let ownerStep = $state(0);
	let completedOwnerSteps = $state<number[]>([]);
	let ownerDeleteTarget = $state<Owner | null>(null);

	const ownerSteps: FormStepperStep[] = [
		{ id: 'identity', label: 'Identity', description: 'Name and tax info' },
		{ id: 'address', label: 'Address', description: 'Mailing address' },
		{ id: 'contact', label: 'Contact', description: 'Phone and email' },
	];
	const ownerStepFields = [
		['name', 'ownerEntityType', 'taxId'],
		['addressLine1', 'addressLine2', 'city', 'state', 'postalCode'],
		['phone', 'email'],
	] as const;

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

	const activatePortalMutation = createMutation(() => ({
		mutationFn: (id: number) => owners.activatePortalAccess(id),
		onSuccess: (result) => {
			showSuccess(result.message);
			invalidateOwners();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreateOwner() {
		editingOwnerId = null;
		ownerForm = { ...emptyOwner };
		ownerErrors = {};
		ownerStep = 0;
		completedOwnerSteps = [];
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
		ownerStep = 0;
		completedOwnerSteps = [];
		showOwnerForm = true;
	}
	function closeOwnerForm() {
		showOwnerForm = false;
		editingOwnerId = null;
		ownerErrors = {};
		ownerStep = 0;
		completedOwnerSteps = [];
	}
	function ownerStepErrorFields(step: number, errors: Record<string, string>) {
		const visibleFields = new Set<string>(ownerStepFields[step] ?? []);
		return Object.entries(errors).filter(([field]) => visibleFields.has(field));
	}
	function firstOwnerErrorStep(errors: Record<string, string>) {
		return ownerStepFields.findIndex((fields) => fields.some((field) => errors[field]));
	}
	function markOwnerStepInvalid(step: number) {
		completedOwnerSteps = completedOwnerSteps.filter((completedStep) => completedStep < step);
	}
	function validateOwnerStep(step: number) {
		const result = parseForm(ownerSchema, ownerForm);
		const currentErrors = result.errors ? Object.fromEntries(ownerStepErrorFields(step, result.errors)) : {};
		const currentFields = new Set<string>(ownerStepFields[step] ?? []);
		const nextErrors = Object.fromEntries(Object.entries(ownerErrors).filter(([field]) => !currentFields.has(field)));
		ownerErrors = { ...nextErrors, ...currentErrors };
		const isValid = Object.keys(currentErrors).length === 0;
		if (!isValid) markOwnerStepInvalid(step);
		return isValid;
	}
	function nextOwnerStep() {
		if (!validateOwnerStep(ownerStep)) return;
		if (completedOwnerSteps.includes(ownerStep)) {
			ownerStep = Math.min(ownerStep + 1, ownerSteps.length - 1);
			return;
		}
		completedOwnerSteps = [...completedOwnerSteps, ownerStep];
		window.setTimeout(() => {
			ownerStep = Math.min(ownerStep + 1, ownerSteps.length - 1);
		}, 260);
	}
	function submitOwner() {
		const result = parseForm(ownerSchema, ownerForm);
		if (result.errors) {
			ownerErrors = result.errors;
			const firstErrorStep = firstOwnerErrorStep(result.errors);
			if (firstErrorStep >= 0) {
				ownerStep = firstErrorStep;
				markOwnerStepInvalid(firstErrorStep);
			}
			return;
		}
		ownerErrors = {};
		saveOwnerMutation.mutate({ id: editingOwnerId, data: { portfolioId, ...result.data } });
	}

	const ownersList = $derived(ownersQuery.data?.items ?? []);
	const ownersTotalCount = $derived(ownersQuery.data?.totalCount ?? 0);
	const hasOwnerSearch = $derived(ownerSearch.trim().length > 0);
	const ownerEmptyMessage = $derived(hasOwnerSearch ? 'No owners match this search.' : 'No owners yet.');
	const ownerEmptyDescription = $derived(
		hasOwnerSearch
			? 'Try a different name, email, phone number, or tax ID.'
			: 'Add the people or organizations that own your rentals.'
	);

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
			key: 'portal',
			title: 'Portal',
			mobileRole: 'meta',
			accessor: (o) => o.hasPendingOwnerPortalInvitation ? 'Pending invitation' : o.hasActiveOwnerPortalAccess ? 'Active' : 'Not active',
			cell: ownerPortalCell,
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

{#snippet ownerPortalCell(o: Owner)}
	<div class="flex items-center gap-2" onclick={(e) => e.stopPropagation()} role="none">
		{#if o.hasPendingOwnerPortalInvitation}
			<span class="text-xs font-medium text-amber-700" data-testid="owner-portal-pending">Pending invitation</span>
			<button
				type="button"
				data-testid="owner-portal-retry-invitation"
				class="inline-flex h-7 items-center gap-1.5 rounded-md border px-2 text-xs font-medium text-foreground transition-colors hover:bg-accent disabled:cursor-not-allowed disabled:opacity-50"
				aria-label="Retry owner portal invitation"
				disabled={activatePortalMutation.isPending}
				title="Retry owner portal invitation"
				onclick={(e) => { e.stopPropagation(); activatePortalMutation.mutate(o.id); }}
			>
				<UserCheck class="h-3.5 w-3.5" />
				Retry
			</button>
		{:else if o.hasActiveOwnerPortalAccess}
			<span class="text-xs font-medium text-emerald-700" data-testid="owner-portal-active">Active</span>
		{:else}
			<button
				type="button"
				data-testid="owner-portal-activate"
				class="inline-flex h-7 items-center gap-1.5 rounded-md border px-2 text-xs font-medium text-foreground transition-colors hover:bg-accent disabled:cursor-not-allowed disabled:opacity-50"
				aria-label="Activate owner portal access"
				disabled={!o.email || o.isPrimary || activatePortalMutation.isPending}
				title={!o.email ? 'Add an owner email before activation' : o.isPrimary ? 'Primary owners use the management account' : 'Activate owner portal access'}
				onclick={(e) => { e.stopPropagation(); activatePortalMutation.mutate(o.id); }}
			>
				<UserCheck class="h-3.5 w-3.5" />
				Activate
			</button>
		{/if}
	</div>
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
	<PageHeader
		class="mb-4"
		band
		art={9}
		tone="violet"
		eyebrow="Settings"
		title="Owners"
		description="Ownership entities consumed by owner statements. Service providers live under Vendors."
		data-testid="owners-header"
	/>

	{#if ownersQuery.isError}
		<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="owners-list-error">
			<p class="font-medium text-destructive">Could not load owners.</p>
			<p class="mt-1 text-sm text-muted-foreground">Try again. No owner records have been changed.</p>
			<Button class="mt-4" variant="outline" onclick={() => ownersQuery.refetch()}>Try again</Button>
		</div>
	{:else}
	<DataGrid
		data={ownersList}
		columns={ownerColumns}
		loading={ownersQuery.isLoading || ownersQuery.isFetching}
		emptyMessage={ownerEmptyMessage}
		emptyDescription={ownerEmptyDescription}
		getRowKey={(o) => o.id}
		getRowTestId={() => 'owner-row'}
		onRowClick={(o) => goto(`/owners/${o.id}`)}
		data-testid="owners-list"
		pageSize={PAGE_SIZE}
		page={gridPage}
		totalCount={ownersTotalCount}
		serverSide
		onPageChange={(page) => (gridPage = page)}
		sort={gridSort}
		onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
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
	{/if}
</div>

<Dialog.Root open={showOwnerForm} onOpenChange={(v) => { if (!v) closeOwnerForm(); }}>
	<Dialog.Content class="max-h-[85vh] max-w-2xl overflow-y-auto [background:var(--m3c-surface-container-highest)]">
		<Dialog.Header>
			<Dialog.Title>{editingOwnerId == null ? 'New Owner' : 'Edit Owner'}</Dialog.Title>
		</Dialog.Header>
		<FormStepper
			steps={ownerSteps}
			bind:currentStep={ownerStep}
			completedSteps={completedOwnerSteps}
			testid="owner-stepper"
		>
			<div class="space-y-3" data-testid="owner-form">
				{#if ownerStep === 0}
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Owner name</span>
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
				{:else if ownerStep === 1}
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
						<Input data-testid="owner-zip-input" bind:value={ownerForm.postalCode} placeholder="ZIP" inputmode="numeric" autocomplete="postal-code" maxlength={10} mask="zip" />
					</div>
				{:else}
					<Input data-testid="owner-phone-input" bind:value={ownerForm.phone} placeholder="Phone (optional)" type="tel" autocomplete="tel" inputmode="tel" mask="phone" />
					<div>
						<Input data-testid="owner-email-input" bind:value={ownerForm.email} type="email" autocomplete="email" placeholder="Email (optional)" />
						{#if ownerErrors.email}<p class="mt-1 text-xs text-destructive" data-testid="owner-email-error">{ownerErrors.email}</p>{/if}
					</div>
				{/if}
			</div>
		</FormStepper>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="owner-form-cancel" variant="outline" onclick={closeOwnerForm}>Cancel</Button>
			{#if ownerStep > 0}
				<Button data-testid="owner-step-back" variant="outline" onclick={() => (ownerStep = Math.max(ownerStep - 1, 0))}>Back</Button>
				{/if}
				{#if ownerStep < ownerSteps.length - 1}
					<StepperNextButton
						testid="owner-step-next"
						onclick={nextOwnerStep}
						complete={completedOwnerSteps.includes(ownerStep)}
					/>
				{:else}
				<Button data-testid="owner-form-save" onclick={submitOwner} disabled={saveOwnerMutation.isPending}>{saveOwnerMutation.isPending ? 'Saving…' : 'Save owner'}</Button>
			{/if}
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
