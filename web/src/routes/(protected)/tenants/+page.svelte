<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { tenants } from '$lib/api/endpoints/tenants';
	import type { Tenant } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { tenantSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import TenantFields from '$lib/components/forms/TenantFields.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import { Plus, Pencil, Trash2, Users } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	// Search/sort/page persisted in the URL so they survive navigating away and back. Sort/page seed the
	// client-side DataGrid (initialSort / page) and are mirrored back via its onSortChange/bind:page.
	const initialParams = page.url.searchParams;
	let search = $state(readGridParam(initialParams, 'q'));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));

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
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId, { take: 500 }),
	}));

	// Client-side filter by name / email
	const list = $derived.by(() => {
		const all = tenantsQuery.data ?? [];
		if (!search.trim()) return all;
		const q = search.trim().toLowerCase();
		return all.filter(
			(t) =>
				`${t.firstName} ${t.lastName}`.toLowerCase().includes(q) ||
				(t.email ?? '').toLowerCase().includes(q)
		);
	});

	const empty = { firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' };
	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let deleteTarget = $state<Tenant | null>(null);

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
		showForm = true;
	}
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
		showForm = true;
	}
	function closeForm() {
		showForm = false;
		editingId = null;
		formErrors = {};
	}

	function submit() {
		const result = parseForm(tenantSchema, form);
		if (result.errors) {
			formErrors = result.errors;
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
	<div class="mb-4 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Tenants</h1>
			<p class="text-sm text-muted-foreground">Resident contacts and lease participation.</p>
		</div>
	</div>

	<DataGrid
		data={list}
		{columns}
		loading={tenantsQuery.isLoading}
		emptyMessage="No tenants yet"
		emptyDescription="Tenants are the people who rent from you. Add your first to start tracking leases and rent."
		emptyIcon={Users}
		emptyActionLabel="Add your first tenant"
		emptyOnAction={openCreate}
		emptyTone="primary"
		onRowClick={(t) => goto(`/tenants/${t.id}`)}
		getRowKey={(t) => t.id}
		getRowTestId={() => 'tenant-row'}
		data-testid="tenants-list"
		initialSort={gridSort}
		bind:page={gridPage}
		onSortChange={(s) => (gridSort = s ?? '')}
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
</div>

<!-- Edit/Create dialog — actions column kept inside the row via the edit button -->
<!-- Row-level edit / delete: rendered via an actions column snippet -->

<Dialog.Root open={showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'New Tenant' : 'Edit Tenant'}</Dialog.Title>
		</Dialog.Header>
		<div data-testid="tenant-form">
			<TenantFields bind:form errors={formErrors} />
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="tenant-form-cancel" variant="outline" onclick={closeForm}>Cancel</Button>
			<Button data-testid="tenant-form-save" onclick={submit} disabled={saveMutation.isPending}>
				{saveMutation.isPending ? 'Saving…' : 'Save tenant'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete tenant"
	message={deleteTarget ? `Delete "${deleteTarget.fullName || `${deleteTarget.firstName} ${deleteTarget.lastName}`}"?` : ''}
	busy={deleteMutation.isPending}
	testid="tenant-delete"
	onconfirm={() => deleteTarget && deleteMutation.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
