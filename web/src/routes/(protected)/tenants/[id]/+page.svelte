<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { leaseManagements } from '$lib/api/endpoints/lease-managements';
	import type { LeaseManagementSummary, Tenant } from '$lib/types';
	import { recordHref } from '$lib/navigation/record-href';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { tenantSchema, parseForm } from '$lib/schemas';
	import { formatDateOnly } from '$lib/utils/date';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { getTenantDeleteState } from '$lib/tenants/tenant-delete-state';
	import {
		clearTenantNoticeActionUrl,
		readTenantNoticeAction,
		tenantNoticeActionKey,
	} from '$lib/tenants/tenant-notice-action';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import TenantNoticeDialog from '$lib/components/notices/TenantNoticeDialog.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Mail, Phone, AlertCircle, Pencil, Save, Trash2, User, X, Contact, FileClock, BellRing } from '@lucide/svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';
	import RecordHistory from '$lib/components/shared/RecordHistory.svelte';
	import { debounced } from '$lib/utils/debounce.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const id = $derived(Number(page.params.id));
	const RELATED_PAGE_SIZE = 20;
	let leaseSearch = $state('');
	let leasePage = $state(1);
	const debouncedLeaseSearch = debounced(() => leaseSearch, 300);

	const tenantQuery = createQuery(() => ({
		queryKey: ['tenant', id],
		queryFn: () => tenants.get(id),
		enabled: !isNaN(id) && id > 0,
	}));

	const leasesQuery = createQuery(() => ({
		queryKey: ['lease-managements', { tenantId: id, search: debouncedLeaseSearch.value, page: leasePage }],
		queryFn: () => leaseManagements.listPage({
			tenantId: id,
			search: debouncedLeaseSearch.value || undefined,
			skip: (leasePage - 1) * RELATED_PAGE_SIZE,
			take: RELATED_PAGE_SIZE,
			sort: '-updatedAtUtc'
		}),
		enabled: !isNaN(id) && id > 0 && portfolioId > 0,
	}));

	$effect(() => {
		debouncedLeaseSearch.value;
		leasePage = 1;
	});

	const tenant = $derived(tenantQuery.data);
	const tenantLeases = $derived(leasesQuery.data?.items ?? []);
	const activeTenantLeaseCount = $derived(tenant?.activeLeaseCount ?? 0);
	const fullName = $derived(
		tenant ? (tenant.fullName ?? `${tenant.firstName} ${tenant.lastName}`) : ''
	);
	const deleteState = $derived(tenant ? getTenantDeleteState(tenant) : null);

	// ── Inline edit ────────────────────────────────────────────────────────────
	const empty = { firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' };
	let editing = $state(false);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);

	function startEditing() {
		if (!tenant) return;
		form = {
			firstName: tenant.firstName,
			lastName: tenant.lastName,
			email: tenant.email ?? '',
			phone: tenant.phone ?? '',
			emergencyContact: tenant.emergencyContact ?? '',
		};
		formErrors = {};
		editing = true;
	}
	function cancelEditing() {
		editing = false;
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
			editing = false;
			formErrors = {};
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

	// ── Per-tenant notice: Create / Send ─────────────────────────────────────────
	// Generate the notice draft(s) due for THIS tenant (POST /notices/generate { tenantId }, owned by a
	// sibling lane), review them in a dialog, then send each on the chosen channels (reusing the notice
	// approve endpoint) or dismiss it. Mirrors the portfolio-wide flow on /notices, scoped to one tenant.
	let showNoticeDialog = $state(false);
	let noticeDialogType = $state<string | undefined>(undefined);
	let handledNoticeActionKey: string | null = null;

	function openNoticeDialog(noticeType?: string) {
		noticeDialogType = noticeType;
		showNoticeDialog = true;
	}

	$effect(() => {
		const action = readTenantNoticeAction(page.url.searchParams);
		if (!action) {
			handledNoticeActionKey = null;
			return;
		}
		if (!tenant) return;
		const actionKey = tenantNoticeActionKey(id, action);
		if (handledNoticeActionKey === actionKey) return;
		handledNoticeActionKey = actionKey;
		openNoticeDialog(action.noticeType);
		void goto(clearTenantNoticeActionUrl(page.url), {
			replaceState: true,
			noScroll: true,
			keepFocus: true,
		});
	});

	// ── Lease columns ──────────────────────────────────────────────────────────
	const leaseColumns: ColumnDef<LeaseManagementSummary>[] = [
		{
			key: 'agreementNumber',
			title: 'Agreement',
			accessor: (relationship) => relationship.agreementNumber ?? 'No governing agreement',
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
			key: 'baseRentAmount',
			title: 'Rent',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
		},
		{
			key: 'termStartOn',
			title: 'Start',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'termEndOn',
			title: 'End',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
		{
			key: 'lifecycle',
			title: 'Relationship',
			mobileRole: 'badge',
			cell: leaseStatusCell,
		},
	];
</script>

{#snippet leaseStatusCell(relationship: LeaseManagementSummary)}
	<StatusBadge status={relationship.lifecycle} />
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
		<div class="rc-hero mb-6 flex flex-wrap items-start justify-between gap-3">
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
				{#if editing}
					<Button variant="outline" class="gap-2" onclick={cancelEditing} disabled={saveMutation.isPending} data-testid="tenant-detail-cancel">
						<X class="h-4 w-4" />
						Cancel
					</Button>
					<Button class="gap-2" onclick={submit} disabled={saveMutation.isPending} data-testid="tenant-detail-save">
						<Save class="h-4 w-4" />
						{saveMutation.isPending ? 'Saving…' : 'Save'}
					</Button>
				{:else}
					<Button variant="outline" class="gap-2" onclick={() => openNoticeDialog()} data-testid="tenant-detail-create-notice">
						<BellRing class="h-4 w-4" />
						Create / Send notice
					</Button>
					<Button variant="outline" class="gap-2" onclick={startEditing} data-testid="tenant-detail-edit">
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
				{/if}
			</div>
		</div>

		<!-- Grouped detail cards -->
		<div class="mb-6 grid gap-6 lg:grid-cols-2">
			<DetailCard title="Contact" icon={Contact} accent="primary" testid="tenant-detail-card" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				<InlineField label="First Name" bind:value={form.firstName} display={tenant.firstName} {editing} error={formErrors.firstName} testid="tenant-detail-first-name" />
				<InlineField label="Last Name" bind:value={form.lastName} display={tenant.lastName} {editing} error={formErrors.lastName} testid="tenant-detail-last-name" />
				<InlineField label="Email" bind:value={form.email} display={tenant.email} {editing} type="email" error={formErrors.email} testid="tenant-detail-email" />
				<InlineField label="Phone" bind:value={form.phone} display={tenant.phone} {editing} type="tel" error={formErrors.phone} testid="tenant-detail-phone" />
				<InlineField label="Emergency Contact" bind:value={form.emergencyContact} display={tenant.emergencyContact} {editing} error={formErrors.emergencyContact} testid="tenant-detail-emergency" class="sm:col-span-2" />
			</DetailCard>

			<DetailCard title="Record" icon={FileClock} accent="muted" testid="tenant-detail-record" contentClass="grid gap-x-6 gap-y-4 sm:grid-cols-2">
				{#if tenant.dateOfBirth}
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Date of Birth</dt>
						<dd class="mt-1 text-sm text-foreground">
							{formatDateOnly(tenant.dateOfBirth)}
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
					<div class="sm:col-span-2">
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Notes</dt>
						<dd class="mt-1 text-sm text-foreground">{tenant.notes}</dd>
					</div>
				{/if}
			</DetailCard>
		</div>

		<!-- Leases section -->
		<div data-testid="tenant-detail-leases">
			<h2 class="mb-3 text-lg font-semibold">Leases</h2>
			{#if leasesQuery.isError}
				<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="tenant-leases-error">
					<p class="text-sm font-medium text-destructive">Could not load this tenant’s leases.</p>
					<Button class="mt-3" variant="outline" size="sm" onclick={() => leasesQuery.refetch()}>Try again</Button>
				</div>
			{:else}
			<DataGrid
				data={tenantLeases}
				columns={leaseColumns}
				loading={leasesQuery.isLoading}
				emptyMessage="No leases found for this tenant."
				onRowClick={(relationship) => goto(recordHref('leaseManagement', { id: relationship.leaseManagementId, unitId: relationship.unitId }))}
				getRowKey={(relationship) => relationship.leaseManagementId}
				pageSize={RELATED_PAGE_SIZE}
				page={leasePage}
				totalCount={leasesQuery.data?.totalCount ?? 0}
				serverSide
				onPageChange={(next) => (leasePage = next)}
				data-testid="tenant-leases-grid"
			>
				{#snippet toolbar()}
					<SearchInput bind:value={leaseSearch} placeholder="Search this tenant’s leases…" testid="tenant-lease-search" />
				{/snippet}
			</DataGrid>
			{/if}
		</div>

		<!-- Documents section -->
		<div class="mt-6" data-testid="tenant-detail-documents">
			<DocumentsPanel entityType="Tenant" entityId={id} />
		</div>

		<!-- Per-record audit history -->
		<div class="mt-6 rounded-lg border border-border bg-card p-4" data-testid="tenant-history-section">
			<h2 class="mb-1 text-base font-semibold">History</h2>
			<p class="mb-3 text-sm text-muted-foreground">Every recorded change to this tenant — who, what, and when.</p>
			<RecordHistory entityType="Tenant" entityId={id} />
		</div>
	{/if}
</div>

<ConfirmDialog
	open={showDeleteConfirm}
	title="Delete tenant"
	message={deleteState?.message ?? ''}
	busy={deleteMutation.isPending}
	confirmDisabled={deleteState?.confirmDisabled ?? false}
	testid="tenant-detail-delete-confirm"
	onconfirm={() => {
		if (!tenant || deleteState?.confirmDisabled) return;
		deleteMutation.mutate(tenant.id);
	}}
	oncancel={() => (showDeleteConfirm = false)}
/>

<TenantNoticeDialog
	bind:open={showNoticeDialog}
	recipientTenantId={id}
	tenantName={fullName}
	activeLeaseCount={activeTenantLeaseCount}
	initialNoticeType={noticeDialogType}
/>
