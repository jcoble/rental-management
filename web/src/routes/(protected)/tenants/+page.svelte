<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { tenants } from '$lib/api/endpoints/tenants';
	import type { Tenant } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { tenantSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { clearFieldError } from '$lib/forms/form-errors';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import StepperNextButton from '$lib/components/shared/StepperNextButton.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { getTenantsEmptyStateCopy } from '$lib/tenants/tenant-list-state';
	import { getTenantDeleteState } from '$lib/tenants/tenant-delete-state';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import { Plus, Pencil, Trash2, Users } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;

	// Search/sort/page persisted in the URL so they survive navigating away and back. The DataGrid is
	// server-side here: search/sort/page drive the API query instead of fetching a capped list first.
	const initialParams = page.url.searchParams;
	let search = $state(readGridParam(initialParams, 'q'));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));
	const debouncedSearch = debounced(() => search, 300);

	// Reset to page 1 when the search changes — but not on initial mount, so a restored ?page= loads as-is.
	let filterResetPrimed = false;
	$effect(() => {
		search;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl({ q: search, sort: gridSort, page: gridPage }, { page: 1 });
	});

	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId, 'page', debouncedSearch.value, gridSort, gridPage, PAGE_SIZE],
		queryFn: () => tenants.listPage(portfolioId, {
			search: debouncedSearch.value,
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		}),
	}));

	const list = $derived(tenantsQuery.data?.items ?? []);
	const totalCount = $derived(tenantsQuery.data?.totalCount ?? 0);
	const emptyCopy = $derived(getTenantsEmptyStateCopy({ hasActiveFilters: search.trim().length > 0 }));

	const empty = { firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' };
	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let tenantStep = $state(0);
	let completedTenantSteps = $state<number[]>([]);
	let deleteTarget = $state<Tenant | null>(null);
	const deleteState = $derived(deleteTarget ? getTenantDeleteState(deleteTarget) : null);
	let createParamHandled = $state(false);

	const tenantSteps: FormStepperStep[] = [
		{ id: 'identity', label: 'Identity', description: 'Resident name' },
		{ id: 'contact', label: 'Contact', description: 'Email and phone' },
	];
	const tenantStepFields = [
		['firstName', 'lastName'],
		['email', 'phone', 'emergencyContact'],
	] as const;

	function clearTenantError(field: string) {
		const next = clearFieldError(formErrors, field);
		if (next !== formErrors) formErrors = next;
	}

	$effect(() => {
		if (form.firstName.trim()) clearTenantError('firstName');
	});
	$effect(() => {
		if (form.lastName.trim()) clearTenantError('lastName');
	});
	$effect(() => {
		if (!form.email.trim() || form.email.includes('@')) clearTenantError('email');
	});

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['tenants', portfolioId] });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null ? tenants.create(data) : tenants.update(id, data),
		onSuccess: (_r, vars) => {
			showSuccess(vars.id == null ? 'Tenant created.' : 'Tenant updated.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: (id: number) => tenants.delete(id),
		onSuccess: () => {
			showSuccess('Tenant deleted.');
			deleteTarget = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreate() {
		editingId = null;
		form = { ...empty };
		formErrors = {};
		tenantStep = 0;
		completedTenantSteps = [];
		showForm = true;
	}

	$effect(() => {
		if (createParamHandled || page.url.searchParams.get('create') !== '1') return;
		createParamHandled = true;
		openCreate();
	});

	function openEdit(t: Tenant) {
		editingId = t.id;
		form = {
			firstName: t.firstName,
			lastName: t.lastName,
			email: t.email ?? '',
			phone: t.phone ?? '',
			emergencyContact: t.emergencyContact ?? '',
		};
		formErrors = {};
		tenantStep = 0;
		completedTenantSteps = [];
		showForm = true;
	}
	function closeForm() {
		showForm = false;
		editingId = null;
		formErrors = {};
		tenantStep = 0;
		completedTenantSteps = [];
	}

	function tenantStepErrorFields(step: number, errors: Record<string, string>) {
		const visibleFields = new Set<string>(tenantStepFields[step] ?? []);
		return Object.entries(errors).filter(([field]) => visibleFields.has(field));
	}

	function firstTenantErrorStep(errors: Record<string, string>) {
		return tenantStepFields.findIndex((fields) => fields.some((field) => errors[field]));
	}

	function markTenantStepInvalid(step: number) {
		completedTenantSteps = completedTenantSteps.filter((completedStep) => completedStep < step);
	}

	function validateTenantStep(step: number) {
		const result = parseForm(tenantSchema, form);
		const currentErrors = result.errors ? Object.fromEntries(tenantStepErrorFields(step, result.errors)) : {};
		const currentFields = new Set<string>(tenantStepFields[step] ?? []);
		const nextErrors = Object.fromEntries(Object.entries(formErrors).filter(([field]) => !currentFields.has(field)));
		formErrors = { ...nextErrors, ...currentErrors };
		const isValid = Object.keys(currentErrors).length === 0;
		if (!isValid) markTenantStepInvalid(step);
		return isValid;
	}

	function nextTenantStep() {
		if (!validateTenantStep(tenantStep)) return;
		if (completedTenantSteps.includes(tenantStep)) {
			tenantStep = Math.min(tenantStep + 1, tenantSteps.length - 1);
			return;
		}
		completedTenantSteps = [...completedTenantSteps, tenantStep];
		window.setTimeout(() => {
			tenantStep = Math.min(tenantStep + 1, tenantSteps.length - 1);
		}, 260);
	}

	function submit() {
		const result = parseForm(tenantSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			const firstErrorStep = firstTenantErrorStep(result.errors);
			if (firstErrorStep >= 0) {
				tenantStep = firstErrorStep;
				markTenantStepInvalid(firstErrorStep);
			}
			return;
		}
		formErrors = {};
		saveMutation.mutate({ id: editingId, data: { portfolioId, ...result.data } });
	}

	// DataGrid column definitions
	const columns: ColumnDef<Tenant>[] = [
		{
			key: 'name',
			title: 'Name',
			sortable: true,
			mobileRole: 'title',
			accessor: (t) => t.fullName ?? `${t.firstName} ${t.lastName}`,
			cell: nameCellSnippet,
		},
		{
			key: 'email',
			title: 'Email',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (t) => t.email ?? '',
		},
		{
			key: 'phone',
			title: 'Phone',
			mobileRole: 'meta',
			accessor: (t) => t.phone ?? '',
		},
		{
			key: 'activeLeaseCount',
			title: 'Active Leases',
			format: 'number',
			sortable: true,
			mobileRole: 'metric',
			accessor: (t) => t.activeLeaseCount ?? 0,
		},
		{
			key: 'createdAt',
			title: 'Created',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
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

{#snippet nameCellSnippet(t: Tenant)}
	<span data-testid="tenant-name">{t.fullName ?? `${t.firstName} ${t.lastName}`}</span>
{/snippet}

{#snippet actionsCellSnippet(t: Tenant)}
	<div class="flex items-center justify-end gap-1" onclick={(e) => e.stopPropagation()} role="none">
		<button
			type="button"
			data-testid="tenant-edit"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
			aria-label="Edit tenant"
			onclick={(e) => { e.stopPropagation(); openEdit(t); }}
		>
			<Pencil class="h-3.5 w-3.5" />
		</button>
		<button
			type="button"
			data-testid="tenant-delete-row"
			class="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-destructive/10 hover:text-destructive"
			aria-label="Delete tenant"
			onclick={(e) => { e.stopPropagation(); deleteTarget = t; }}
		>
			<Trash2 class="h-3.5 w-3.5" />
		</button>
	</div>
{/snippet}

<svelte:head>
	<title>Tenants - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="tenants-page">
	<PageHeader
		class="mb-4"
		band
		art={4}
		tone="mint"
		eyebrow="Rentals"
		title="Tenants"
		description="Resident contacts and lease participation."
		data-testid="tenants-header"
	/>

	{#if tenantsQuery.isError}
		<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-6" role="alert" data-testid="tenants-list-error">
			<p class="font-medium text-destructive">Could not load tenants.</p>
			<p class="mt-1 text-sm text-muted-foreground">Try again. An unavailable list is not an empty tenant directory.</p>
			<Button class="mt-4" variant="outline" onclick={() => tenantsQuery.refetch()}>Try again</Button>
		</div>
	{:else}
	<DataGrid
		data={list}
		{columns}
		loading={tenantsQuery.isLoading || tenantsQuery.isFetching}
		emptyMessage={emptyCopy.message}
		emptyDescription={emptyCopy.description}
		emptyIcon={Users}
		emptyActionLabel={emptyCopy.actionLabel}
		emptyOnAction={openCreate}
		emptyTone="primary"
		onRowClick={(t) => goto(`/tenants/${t.id}`)}
		getRowKey={(t) => t.id}
		getRowTestId={() => 'tenant-row'}
		data-testid="tenants-list"
		pageSize={PAGE_SIZE}
		page={gridPage}
		totalCount={totalCount}
		serverSide
		onPageChange={(page) => (gridPage = page)}
		sort={gridSort}
		onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
	>
		{#snippet toolbar()}
			<div class="flex flex-1 items-center gap-2">
				<SearchInput bind:value={search} placeholder="Search tenants…" testid="tenant-search" />
			</div>
			<Button data-testid="tenant-create-button" data-coach="add-tenant" class="gap-2 shrink-0" onclick={openCreate}>
				<Plus class="h-4 w-4" />
				New Tenant
			</Button>
		{/snippet}
	</DataGrid>
	{/if}
</div>

<!-- Edit/Create dialog — actions column kept inside the row via the edit button -->
<!-- Row-level edit / delete: rendered via an actions column snippet -->

<Dialog.Root open={showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-h-[85vh] max-w-lg overflow-y-auto [background:var(--m3c-surface-container-highest)]">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'New Tenant' : 'Edit Tenant'}</Dialog.Title>
		</Dialog.Header>
		<FormStepper
			steps={tenantSteps}
			bind:currentStep={tenantStep}
			completedSteps={completedTenantSteps}
			testid="tenant-stepper"
		>
			<div data-testid="tenant-form">
				{#if tenantStep === 0}
					<div class="grid gap-3 md:grid-cols-2">
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">First name</span>
							<Input data-testid="tenant-first-name-input" bind:value={form.firstName} placeholder="First name" />
							{#if formErrors.firstName}<p class="mt-1 text-xs text-destructive" data-testid="tenant-first-name-error">{formErrors.firstName}</p>{/if}
						</div>
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Last name</span>
							<Input data-testid="tenant-last-name-input" bind:value={form.lastName} placeholder="Last name" />
							{#if formErrors.lastName}<p class="mt-1 text-xs text-destructive" data-testid="tenant-last-name-error">{formErrors.lastName}</p>{/if}
						</div>
					</div>
				{:else}
					<div class="grid gap-3 md:grid-cols-2">
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Email</span>
							<Input data-testid="tenant-email-input" bind:value={form.email} placeholder="Email" type="email" autocomplete="email" />
							{#if formErrors.email}<p class="mt-1 text-xs text-destructive" data-testid="tenant-email-error">{formErrors.email}</p>{/if}
						</div>
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Phone</span>
							<Input data-testid="tenant-phone-input" bind:value={form.phone} placeholder="Phone" type="tel" autocomplete="tel" inputmode="tel" mask="phone" />
						</div>
						<div class="md:col-span-2">
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Emergency contact</span>
							<Input data-testid="tenant-emergency-input" bind:value={form.emergencyContact} placeholder="Emergency contact" />
						</div>
					</div>
				{/if}
			</div>
		</FormStepper>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="tenant-form-cancel" variant="outline" onclick={closeForm}>Cancel</Button>
			{#if tenantStep > 0}
				<Button data-testid="tenant-step-back" variant="outline" onclick={() => (tenantStep = Math.max(tenantStep - 1, 0))}>Back</Button>
				{/if}
				{#if tenantStep < tenantSteps.length - 1}
					<StepperNextButton
						testid="tenant-step-next"
						onclick={nextTenantStep}
						complete={completedTenantSteps.includes(tenantStep)}
					/>
				{:else}
				<Button data-testid="tenant-form-save" onclick={submit} disabled={saveMutation.isPending}>
					{saveMutation.isPending ? 'Saving…' : 'Save tenant'}
				</Button>
			{/if}
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete tenant"
	message={deleteState?.message ?? ''}
	busy={deleteMutation.isPending}
	confirmDisabled={deleteState?.confirmDisabled ?? false}
	testid="tenant-delete"
	onconfirm={() => {
		if (!deleteTarget || deleteState?.confirmDisabled) return;
		deleteMutation.mutate(deleteTarget.id);
	}}
	oncancel={() => (deleteTarget = null)}
/>
