<script lang="ts">
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { leases } from '$lib/api/endpoints/leases';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import type { Lease } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { leaseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import Pagination from '$lib/components/shared/Pagination.svelte';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const LEASE_STATUSES = ['Draft', 'Active', 'NoticeGiven', 'Expired', 'Terminated'];

	const PAGE_SIZE = 20;
	let search = $state('');
	let statusFilter = $state('');
	let skip = $state(0);
	const debouncedSearch = debounced(() => search, 300);
	$effect(() => {
		debouncedSearch.value;
		statusFilter;
		skip = 0;
	});

	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId, debouncedSearch.value, statusFilter, skip],
		queryFn: () => leases.list(portfolioId, { search: debouncedSearch.value, skip, take: PAGE_SIZE }),
	}));

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId, { take: 200 }),
	}));
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId, { take: 200 }),
	}));

	let formPropertyId = $state('');
	const unitsForPropertyQuery = createQuery(() => ({
		queryKey: ['units-for-lease', formPropertyId],
		enabled: !!formPropertyId,
		queryFn: () => properties.listUnits(Number(formPropertyId)),
	}));

	const empty = {
		leaseNumber: '', propertyId: '', unitId: '', tenantId: '', startDate: '', endDate: '',
		moveInDate: '', moveOutDate: '', monthlyRent: '', securityDeposit: '',
		lateFeeAmount: '75', rentDueDay: '1', status: 'Draft', notes: '',
	};
	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let deleteTarget = $state<Lease | null>(null);

	$effect(() => {
		if (form.propertyId !== formPropertyId) {
			formPropertyId = form.propertyId;
		}
	});

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['leases', portfolioId] });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null ? leases.create(data) : leases.update(id, data),
		onSuccess: (_r, vars) => {
			showSuccess(vars.id == null ? 'Lease created.' : 'Lease updated.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: ({ id, status }: { id: number; status: string }) => leases.update(id, { status }),
		onSuccess: () => {
			showSuccess('Lease status updated.');
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: (id: number) => leases.delete(id),
		onSuccess: () => {
			showSuccess('Lease deleted.');
			deleteTarget = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreate() {
		editingId = null;
		form = { ...empty };
		formPropertyId = '';
		formErrors = {};
		showForm = true;
	}
	function openEdit(l: Lease) {
		editingId = l.id;
		form = {
			leaseNumber: l.leaseNumber,
			propertyId: String(l.propertyId),
			unitId: String(l.unitId),
			tenantId: String(l.tenantId),
			startDate: l.startDate?.slice(0, 10) ?? '',
			endDate: l.endDate?.slice(0, 10) ?? '',
			moveInDate: l.moveInDate?.slice(0, 10) ?? '',
			moveOutDate: l.moveOutDate?.slice(0, 10) ?? '',
			monthlyRent: String(l.monthlyRent),
			securityDeposit: String(l.securityDeposit),
			lateFeeAmount: String(l.lateFeeAmount),
			rentDueDay: String(l.rentDueDay),
			status: l.status,
			notes: l.notes ?? '',
		};
		formPropertyId = String(l.propertyId);
		formErrors = {};
		showForm = true;
	}
	function closeForm() {
		showForm = false;
		editingId = null;
		formErrors = {};
	}

	function submit() {
		const result = parseForm(leaseSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ id: editingId, data: { portfolioId, ...result.data } });
	}

	const list = $derived((leasesQuery.data ?? []).filter((l) => !statusFilter || l.status === statusFilter));
	const inputClass = 'h-10 rounded border border-border bg-background px-3 py-2 text-sm';
</script>

<svelte:head>
	<title>Leases - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="leases-page">
	<div class="mb-4 flex items-center justify-between gap-3">
		<div>
			<h1 class="text-2xl font-bold">Leases</h1>
			<p class="text-sm text-muted-foreground">Lease lifecycle, rent terms, and status updates.</p>
		</div>
		<button data-testid="lease-create-button" class="inline-flex items-center gap-2 rounded-md bg-primary px-3 py-2 text-sm text-white" onclick={openCreate}>
			<Plus class="h-4 w-4" />
			New Lease
		</button>
	</div>

	<div class="mb-4 flex flex-wrap items-center gap-3">
		<div class="max-w-sm flex-1"><SearchInput bind:value={search} placeholder="Search leases…" testid="lease-search" /></div>
		<select data-testid="lease-status-filter" bind:value={statusFilter} class="{inputClass} h-9">
			<option value="">All statuses</option>
			{#each LEASE_STATUSES as s}<option value={s}>{s}</option>{/each}
		</select>
	</div>

	<div class="rounded-lg border border-border bg-card">
		<div class="overflow-x-auto">
			<table class="min-w-full text-sm">
				<thead class="border-b border-border bg-background text-left text-xs uppercase text-muted-foreground">
					<tr>
						<th class="px-3 py-2">Lease</th>
						<th class="px-3 py-2">Tenant</th>
						<th class="px-3 py-2">Unit</th>
						<th class="px-3 py-2">Rent</th>
						<th class="px-3 py-2">Term</th>
						<th class="px-3 py-2">Status</th>
						<th class="px-3 py-2 text-right">Actions</th>
					</tr>
				</thead>
				<tbody data-testid="leases-list">
					{#if leasesQuery.isLoading}
						<tr><td colspan="7" class="px-3 py-6 text-center text-muted-foreground" data-testid="leases-loading">Loading…</td></tr>
					{:else if list.length === 0}
						<tr><td colspan="7" class="px-3 py-6 text-center text-muted-foreground" data-testid="leases-empty">No leases found.</td></tr>
					{:else}
						{#each list as lease (lease.id)}
							<tr class="border-b border-border/70" data-testid="lease-row" data-lease-id={lease.id}>
								<td class="px-3 py-2 font-medium" data-testid="lease-number">{lease.leaseNumber}</td>
								<td class="px-3 py-2">{lease.tenantName || '—'}</td>
								<td class="px-3 py-2">{lease.propertyName} · {lease.unitNumber}</td>
								<td class="px-3 py-2">${lease.monthlyRent}</td>
								<td class="px-3 py-2 text-muted-foreground">{new Date(lease.startDate).toLocaleDateString()} - {new Date(lease.endDate).toLocaleDateString()}</td>
								<td class="px-3 py-2" data-testid="lease-status">{lease.status}</td>
								<td class="px-3 py-2">
									<div class="flex justify-end gap-1">
										{#if lease.status !== 'Active'}
											<button data-testid="lease-set-active" class="rounded border border-border px-2 py-1 text-xs" onclick={() => statusMutation.mutate({ id: lease.id, status: 'Active' })}>Set Active</button>
										{:else}
											<button data-testid="lease-give-notice" class="rounded border border-border px-2 py-1 text-xs" onclick={() => statusMutation.mutate({ id: lease.id, status: 'NoticeGiven' })}>Give Notice</button>
										{/if}
										<a href={`/leases/${lease.id}`} data-testid="lease-details" class="rounded border border-border px-2 py-1 text-xs text-primary hover:bg-secondary">Details</a>
										<button data-testid="lease-edit" aria-label="Edit lease" class="rounded p-1.5 text-muted-foreground hover:bg-secondary hover:text-foreground" onclick={() => goto(`/leases/${lease.id}`)}>
											<Pencil class="h-4 w-4" />
										</button>
										<button data-testid="lease-delete" aria-label="Delete lease" class="rounded p-1.5 text-muted-foreground hover:bg-secondary hover:text-destructive" onclick={() => (deleteTarget = lease)}>
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
			<Pagination bind:skip take={PAGE_SIZE} count={leasesQuery.data?.length ?? 0} testid="lease-pagination" />
		</div>
	</div>
</div>

<Dialog.Root
	open={showForm}
	onOpenChange={(v) => { if (!v) closeForm(); }}
>
	<Dialog.Content class="max-w-2xl">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'New Lease' : 'Edit Lease'}</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3 md:grid-cols-3" data-testid="lease-form">
			<div>
				<label for="lease-number-input" class="mb-1 block text-xs font-medium text-muted-foreground">Lease number</label>
				<input id="lease-number-input" data-testid="lease-number-input" bind:value={form.leaseNumber} class="{inputClass} w-full" placeholder="Lease number" />
				{#if formErrors.leaseNumber}<p class="mt-1 text-xs text-destructive" data-testid="lease-number-error">{formErrors.leaseNumber}</p>{/if}
			</div>
			<div>
				<label for="lease-property-input" class="mb-1 block text-xs font-medium text-muted-foreground">Property</label>
				<select id="lease-property-input" data-testid="lease-property-input" bind:value={form.propertyId} class="{inputClass} w-full">
					<option value="">Select property</option>
					{#each propertiesQuery.data || [] as property}<option value={property.id}>{property.name}</option>{/each}
				</select>
			</div>
			<div>
				<label for="lease-unit-input" class="mb-1 block text-xs font-medium text-muted-foreground">Unit</label>
				<select id="lease-unit-input" data-testid="lease-unit-input" bind:value={form.unitId} class="{inputClass} w-full" disabled={!form.propertyId}>
					<option value="">Select unit</option>
					{#each unitsForPropertyQuery.data || [] as unit}<option value={unit.id}>Unit {unit.unitNumber} ({unit.status})</option>{/each}
				</select>
				{#if formErrors.unitId}<p class="mt-1 text-xs text-destructive" data-testid="lease-unit-error">{formErrors.unitId}</p>{/if}
			</div>
			<div>
				<label for="lease-tenant-input" class="mb-1 block text-xs font-medium text-muted-foreground">Tenant</label>
				<select id="lease-tenant-input" data-testid="lease-tenant-input" bind:value={form.tenantId} class="{inputClass} w-full">
					<option value="">Select tenant</option>
					{#each tenantsQuery.data || [] as tenant}<option value={tenant.id}>{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}</option>{/each}
				</select>
				{#if formErrors.tenantId}<p class="mt-1 text-xs text-destructive" data-testid="lease-tenant-error">{formErrors.tenantId}</p>{/if}
			</div>
			<div>
				<label for="lease-start-input" class="mb-1 block text-xs font-medium text-muted-foreground">Lease start date</label>
				<input id="lease-start-input" data-testid="lease-start-input" type="date" bind:value={form.startDate} class="{inputClass} w-full" />
				{#if formErrors.startDate}<p class="mt-1 text-xs text-destructive" data-testid="lease-start-error">{formErrors.startDate}</p>{/if}
			</div>
			<div>
				<label for="lease-end-input" class="mb-1 block text-xs font-medium text-muted-foreground">Lease end date</label>
				<input id="lease-end-input" data-testid="lease-end-input" type="date" bind:value={form.endDate} class="{inputClass} w-full" />
				{#if formErrors.endDate}<p class="mt-1 text-xs text-destructive" data-testid="lease-end-error">{formErrors.endDate}</p>{/if}
			</div>
			<div>
				<label for="lease-move-in-input" class="mb-1 block text-xs font-medium text-muted-foreground">Move-in date</label>
				<input id="lease-move-in-input" data-testid="lease-move-in-input" type="date" bind:value={form.moveInDate} class="{inputClass} w-full" />
			</div>
			<div>
				<label for="lease-move-out-input" class="mb-1 block text-xs font-medium text-muted-foreground">Move-out date</label>
				<input id="lease-move-out-input" data-testid="lease-move-out-input" type="date" bind:value={form.moveOutDate} class="{inputClass} w-full" />
			</div>
			<div>
				<label for="lease-status-input" class="mb-1 block text-xs font-medium text-muted-foreground">Status</label>
				<select id="lease-status-input" data-testid="lease-status-input" bind:value={form.status} class="{inputClass} w-full">
					{#each LEASE_STATUSES as s}<option value={s}>{s}</option>{/each}
				</select>
			</div>
			<div>
				<label for="lease-rent-input" class="mb-1 block text-xs font-medium text-muted-foreground">Monthly rent</label>
				<input id="lease-rent-input" data-testid="lease-rent-input" bind:value={form.monthlyRent} class="{inputClass} w-full" placeholder="Monthly rent" />
				{#if formErrors.monthlyRent}<p class="mt-1 text-xs text-destructive" data-testid="lease-rent-error">{formErrors.monthlyRent}</p>{/if}
			</div>
			<div>
				<label for="lease-deposit-input" class="mb-1 block text-xs font-medium text-muted-foreground">Security deposit</label>
				<input id="lease-deposit-input" data-testid="lease-deposit-input" bind:value={form.securityDeposit} class="{inputClass} w-full" placeholder="Security deposit" />
				{#if formErrors.securityDeposit}<p class="mt-1 text-xs text-destructive" data-testid="lease-deposit-error">{formErrors.securityDeposit}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<div>
					<label for="lease-late-fee-input" class="mb-1 block text-xs font-medium text-muted-foreground">Late fee</label>
					<input id="lease-late-fee-input" data-testid="lease-late-fee-input" bind:value={form.lateFeeAmount} class="{inputClass} w-full" placeholder="Late fee" />
				</div>
				<div>
					<label for="lease-due-day-input" class="mb-1 block text-xs font-medium text-muted-foreground">Due day</label>
					<input id="lease-due-day-input" data-testid="lease-due-day-input" bind:value={form.rentDueDay} class="{inputClass} w-full" placeholder="Due day" />
				</div>
			</div>
			<div class="md:col-span-3">
				<label for="lease-notes-input" class="mb-1 block text-xs font-medium text-muted-foreground">Notes</label>
				<textarea id="lease-notes-input" data-testid="lease-notes-input" bind:value={form.notes} class="{inputClass} min-h-20 w-full" placeholder="Notes"></textarea>
			</div>
		</div>
		<Dialog.Footer>
			<button data-testid="lease-form-cancel" class="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground hover:bg-secondary" onclick={closeForm}>Cancel</button>
			<button data-testid="lease-form-save" onclick={submit} class="rounded bg-primary px-3 py-2 text-sm text-white" disabled={saveMutation.isPending}>
				{saveMutation.isPending ? 'Saving…' : 'Save Lease'}
			</button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Delete lease"
	message={deleteTarget ? `Delete lease ${deleteTarget.leaseNumber}?` : ''}
	busy={deleteMutation.isPending}
	testid="lease-delete"
	onconfirm={() => deleteTarget && deleteMutation.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
