<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { tenants } from '$lib/api/endpoints/tenants';
	import type { Tenant } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { tenantSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import Dialog from '$lib/components/ui/Dialog.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const PAGE_SIZE = 20;
	let search = $state('');
	let skip = $state(0);
	const debouncedSearch = debounced(() => search, 300);
	$effect(() => {
		debouncedSearch.value;
		skip = 0;
	});

	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId, debouncedSearch.value, skip],
		queryFn: () => tenants.list(portfolioId, { search: debouncedSearch.value, skip, take: PAGE_SIZE }),
	}));

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

	const list = $derived(tenantsQuery.data ?? []);
	const inputClass = 'rounded border border-border bg-bg px-3 py-2 text-sm';
</script>

<svelte:head>
	<title>Tenants - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="tenants-page">
	<div class="mb-4 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Tenants</h1>
			<p class="text-sm text-text-secondary">Resident contacts and lease participation.</p>
		</div>
		<button data-testid="tenant-create-button" class="inline-flex items-center gap-2 rounded-md bg-accent px-3 py-2 text-sm text-white" onclick={openCreate}>
			<Plus class="h-4 w-4" />
			New Tenant
		</button>
	</div>

	<div class="mb-4 max-w-sm">
		<SearchInput bind:value={search} placeholder="Search tenants…" testid="tenant-search" />
	</div>

	<div class="rounded-lg border border-border bg-surface">
		<div class="overflow-x-auto">
			<table class="min-w-full text-sm">
				<thead class="border-b border-border bg-bg text-left text-xs uppercase text-text-tertiary">
					<tr>
						<th class="px-3 py-2">Name</th>
						<th class="px-3 py-2">Email</th>
						<th class="px-3 py-2">Phone</th>
						<th class="px-3 py-2">Emergency Contact</th>
						<th class="px-3 py-2">Active Leases</th>
						<th class="px-3 py-2 text-right">Actions</th>
					</tr>
				</thead>
				<tbody data-testid="tenants-list">
					{#if tenantsQuery.isLoading}
						<tr><td colspan="6" class="px-3 py-6 text-center text-text-secondary" data-testid="tenants-loading">Loading…</td></tr>
					{:else if list.length === 0}
						<tr><td colspan="6" class="px-3 py-6 text-center text-text-secondary" data-testid="tenants-empty">No tenants found.</td></tr>
					{:else}
						{#each list as tenant (tenant.id)}
							<tr class="border-b border-border/70" data-testid="tenant-row" data-tenant-id={tenant.id}>
								<td class="px-3 py-2 font-medium" data-testid="tenant-name">{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}</td>
								<td class="px-3 py-2 text-text-secondary">{tenant.email || '—'}</td>
								<td class="px-3 py-2 text-text-secondary">{tenant.phone || '—'}</td>
								<td class="px-3 py-2 text-text-secondary">{tenant.emergencyContact || '—'}</td>
								<td class="px-3 py-2">{tenant.activeLeaseCount || 0}</td>
								<td class="px-3 py-2">
									<div class="flex justify-end gap-1">
										<button data-testid="tenant-edit" aria-label="Edit tenant" class="rounded p-1.5 text-text-tertiary hover:bg-surface-hover hover:text-text-primary" onclick={() => openEdit(tenant)}>
											<Pencil class="h-4 w-4" />
										</button>
										<button data-testid="tenant-delete" aria-label="Delete tenant" class="rounded p-1.5 text-text-tertiary hover:bg-surface-hover hover:text-danger" onclick={() => (deleteTarget = tenant)}>
											<Trash2 class="h-4 w-4" />
										</button>
									</div>
								</td>
							</tr>
						{/each}
					{/if}
				</tbody>
			</table>
		</div>
		<div class="border-t border-border px-3 py-2">
			<Pagination bind:skip take={PAGE_SIZE} count={list.length} testid="tenant-pagination" />
		</div>
	</div>
</div>

<Dialog open={showForm} title={editingId == null ? 'New Tenant' : 'Edit Tenant'} class="max-w-lg" onclose={closeForm}>
	<div class="grid gap-3 md:grid-cols-2" data-testid="tenant-form">
		<div>
			<input data-testid="tenant-first-name-input" bind:value={form.firstName} class="{inputClass} w-full" placeholder="First name" />
			{#if formErrors.firstName}<p class="mt-1 text-xs text-danger" data-testid="tenant-first-name-error">{formErrors.firstName}</p>{/if}
		</div>
		<div>
			<input data-testid="tenant-last-name-input" bind:value={form.lastName} class="{inputClass} w-full" placeholder="Last name" />
			{#if formErrors.lastName}<p class="mt-1 text-xs text-danger" data-testid="tenant-last-name-error">{formErrors.lastName}</p>{/if}
		</div>
		<div>
			<input data-testid="tenant-email-input" bind:value={form.email} class="{inputClass} w-full" placeholder="Email" />
			{#if formErrors.email}<p class="mt-1 text-xs text-danger" data-testid="tenant-email-error">{formErrors.email}</p>{/if}
		</div>
		<input data-testid="tenant-phone-input" bind:value={form.phone} class={inputClass} placeholder="Phone" />
		<input data-testid="tenant-emergency-input" bind:value={form.emergencyContact} class="{inputClass} md:col-span-2" placeholder="Emergency contact" />
	</div>
	<div class="mt-4 flex justify-end gap-2">
		<button data-testid="tenant-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-text-secondary hover:bg-surface-hover" onclick={closeForm}>Cancel</button>
		<button data-testid="tenant-form-save" onclick={submit} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={saveMutation.isPending}>
			{saveMutation.isPending ? 'Saving…' : 'Save Tenant'}
		</button>
	</div>
</Dialog>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete tenant"
	message={deleteTarget ? `Delete “${deleteTarget.fullName || `${deleteTarget.firstName} ${deleteTarget.lastName}`}”?` : ''}
	busy={deleteMutation.isPending}
	testid="tenant-delete"
	onconfirm={() => deleteTarget && deleteMutation.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
