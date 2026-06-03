<script lang="ts">
	import { page } from '$app/stores';
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { leases } from '$lib/api/endpoints/leases';
	import type { Lease, Tenant } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { tenantSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Mail, Phone, AlertCircle, Pencil, Trash2, User } from '@lucide/svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const id = $derived(Number($page.params.id));

	const tenantQuery = createQuery(() => ({
		queryKey: ['tenant', id],
		queryFn: () => tenants.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId, { tenantId: id }],
		queryFn: () => leases.list(portfolioId, { tenantId: id, take: 100 }),
		enabled: !isNaN(id) && id > 0 && portfolioId > 0,
	}));

	const tenant = $derived(tenantQuery.data);
	const tenantLeases = $derived(leasesQuery.data ?? []);
	const fullName = $derived(
		tenant ? (tenant.fullName ?? `${tenant.firstName} ${tenant.lastName}`) : ''
	);

	// ── Edit dialog ────────────────────────────────────────────────────────────
	const empty = { firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' };
	let showForm = $state(false);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);

	function openEdit() {
		if (!tenant) return;
		form = {
			firstName: tenant.firstName,
			lastName: tenant.lastName,
			email: tenant.email ?? '',
			phone: tenant.phone ?? '',
			emergencyContact: tenant.emergencyContact ?? '',
		};
		formErrors = {};
		showForm = true;
	}
	function closeForm() {
		showForm = false;
		formErrors = {};
	}

	function submit() {
		const result = parseForm(tenantSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ id, data: { portfolioId, ...result.data } });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: ({ id: tid, data }: { id: number; data: Record<string, unknown> }) =>
			tenants.update(tid, data),
		onSuccess: () => {
			showSuccess('Tenant updated.');
			closeForm();
			queryClient.invalidateQueries({ queryKey: ['tenant', id] });
			queryClient.invalidateQueries({ queryKey: ['tenants', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: (tid: number) => tenants.delete(tid),
		onSuccess: () => {
			showSuccess('Tenant deleted.');
			queryClient.invalidateQueries({ queryKey: ['tenants', portfolioId] });
			goto('/tenants');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// ── Lease columns ──────────────────────────────────────────────────────────
	const leaseColumns: ColumnDef<Lease>[] = [
		{
			key: 'leaseNumber',
			title: 'Lease #',
			sortable: true,
			mobileRole: 'title',
		},
		{
			key: 'unitNumber',
			title: 'Unit',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (l) => l.unitNumber ?? '–',
		},
		{
			key: 'monthlyRent',
			title: 'Rent',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
		},
		{
			key: 'startDate',
			title: 'Start',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'endDate',
			title: 'End',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: leaseStatusCell,
		},
	];
</script>

{#snippet leaseStatusCell(lease: Lease)}
	<StatusBadge status={lease.status} />
{/snippet}

<svelte:head>
	<title>{fullName ? `${fullName} - Tenant` : 'Tenant'} - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="tenant-detail-page">
	<!-- Breadcrumb -->
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Tenants', href: '/tenants' },
				{ label: fullName || '…' },
			]}
		/>
	</div>

	<!-- Loading state -->
	{#if tenantQuery.isLoading}
		<div class="flex items-center justify-center py-20 text-muted-foreground" data-testid="tenant-detail-loading">
			<div class="flex flex-col items-center gap-2">
				<div class="h-6 w-6 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
				<span class="text-sm">Loading…</span>
			</div>
		</div>

	<!-- Error state -->
	{:else if tenantQuery.isError}
		<div class="rounded-lg border border-destructive/30 bg-destructive/10 p-6 text-center" data-testid="tenant-detail-error">
			<AlertCircle class="mx-auto mb-2 h-8 w-8 text-destructive" />
			<p class="font-medium text-destructive">Could not load tenant</p>
			<p class="mt-1 text-sm text-muted-foreground">{apiErrorMessage(tenantQuery.error)}</p>
			<Button variant="outline" class="mt-4" onclick={() => tenantQuery.refetch()}>Retry</Button>
		</div>

	<!-- Not found -->
	{:else if !tenant}
		<div class="rounded-lg border border-border p-6 text-center" data-testid="tenant-detail-not-found">
			<User class="mx-auto mb-2 h-8 w-8 text-muted-foreground" />
			<p class="font-medium">Tenant not found</p>
			<Button variant="outline" class="mt-4" onclick={() => goto('/tenants')}>Back to Tenants</Button>
		</div>

	{:else}
		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start justify-between gap-3">
			<div>
				<h1 class="text-2xl font-bold" data-testid="tenant-detail-name">{fullName}</h1>
				<div class="mt-1 flex flex-wrap items-center gap-3 text-sm text-muted-foreground">
					{#if tenant.email}
						<span class="flex items-center gap-1">
							<Mail class="h-3.5 w-3.5" />
							{tenant.email}
						</span>
					{/if}
					{#if tenant.phone}
						<span class="flex items-center gap-1">
							<Phone class="h-3.5 w-3.5" />
							{tenant.phone}
						</span>
					{/if}
				</div>
			</div>
			<div class="flex items-center gap-2">
				<Button variant="outline" class="gap-2" onclick={openEdit} data-testid="tenant-detail-edit">
					<Pencil class="h-4 w-4" />
					Edit
				</Button>
				<Button
					variant="destructive"
					class="gap-2"
					onclick={() => (showDeleteConfirm = true)}
					data-testid="tenant-detail-delete"
				>
					<Trash2 class="h-4 w-4" />
					Delete
				</Button>
			</div>
		</div>

		<!-- Info card -->
		<Card.Root class="mb-6" data-testid="tenant-detail-card">
			<Card.Header>
				<Card.Title>Tenant Details</Card.Title>
			</Card.Header>
			<Card.Content>
				<dl class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">First Name</dt>
						<dd class="mt-1 text-sm text-foreground">{tenant.firstName}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Last Name</dt>
						<dd class="mt-1 text-sm text-foreground">{tenant.lastName}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Email</dt>
						<dd class="mt-1 text-sm text-foreground">{tenant.email ?? '–'}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Phone</dt>
						<dd class="mt-1 text-sm text-foreground">{tenant.phone ?? '–'}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Emergency Contact</dt>
						<dd class="mt-1 text-sm text-foreground">{tenant.emergencyContact ?? '–'}</dd>
					</div>
					{#if tenant.dateOfBirth}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Date of Birth</dt>
							<dd class="mt-1 text-sm text-foreground">
								{new Date(tenant.dateOfBirth).toLocaleDateString()}
							</dd>
						</div>
					{/if}
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Active Leases</dt>
						<dd class="mt-1 text-sm font-semibold tabular-nums text-foreground">
							{tenant.activeLeaseCount ?? 0}
						</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Created</dt>
						<dd class="mt-1 text-sm text-foreground">
							{new Date(tenant.createdAt).toLocaleDateString()}
						</dd>
					</div>
					{#if tenant.notes}
						<div class="sm:col-span-2 lg:col-span-3">
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Notes</dt>
							<dd class="mt-1 text-sm text-foreground">{tenant.notes}</dd>
						</div>
					{/if}
				</dl>
			</Card.Content>
		</Card.Root>

		<!-- Leases section -->
		<div data-testid="tenant-detail-leases">
			<h2 class="mb-3 text-lg font-semibold">Leases</h2>
			<DataGrid
				data={tenantLeases}
				columns={leaseColumns}
				loading={leasesQuery.isLoading}
				emptyMessage="No leases found for this tenant."
				onRowClick={(lease) => goto(`/leases/${lease.id}`)}
				getRowKey={(l) => l.id}
				pageSize={10}
				data-testid="tenant-leases-grid"
			/>
		</div>

		<!-- Documents section -->
		<div class="mt-6" data-testid="tenant-detail-documents">
			<DocumentsPanel entityType="Tenant" entityId={id} />
		</div>
	{/if}
</div>

<!-- Edit dialog -->
<Dialog.Root open={showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-w-lg">
		<Dialog.Header>
			<Dialog.Title>Edit Tenant</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3 md:grid-cols-2" data-testid="tenant-form">
			<div>
				<Input data-testid="tenant-first-name-input" bind:value={form.firstName} placeholder="First name" />
				{#if formErrors.firstName}<p class="mt-1 text-xs text-destructive" data-testid="tenant-first-name-error">{formErrors.firstName}</p>{/if}
			</div>
			<div>
				<Input data-testid="tenant-last-name-input" bind:value={form.lastName} placeholder="Last name" />
				{#if formErrors.lastName}<p class="mt-1 text-xs text-destructive" data-testid="tenant-last-name-error">{formErrors.lastName}</p>{/if}
			</div>
			<div>
				<Input data-testid="tenant-email-input" bind:value={form.email} placeholder="Email" />
				{#if formErrors.email}<p class="mt-1 text-xs text-destructive" data-testid="tenant-email-error">{formErrors.email}</p>{/if}
			</div>
			<Input data-testid="tenant-phone-input" bind:value={form.phone} placeholder="Phone" />
			<Input data-testid="tenant-emergency-input" bind:value={form.emergencyContact} class="md:col-span-2" placeholder="Emergency contact" />
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button data-testid="tenant-form-cancel" variant="outline" onclick={closeForm}>Cancel</Button>
			<Button data-testid="tenant-form-save" onclick={submit} disabled={saveMutation.isPending}>
				{saveMutation.isPending ? 'Saving…' : 'Save Tenant'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={showDeleteConfirm}
	title="Delete tenant"
	message={tenant ? `Delete "${fullName}"? This cannot be undone.` : ''}
	busy={deleteMutation.isPending}
	testid="tenant-detail-delete-confirm"
	onconfirm={() => tenant && deleteMutation.mutate(tenant.id)}
	oncancel={() => (showDeleteConfirm = false)}
/>
