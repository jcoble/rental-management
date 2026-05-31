<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { leases } from '$lib/api/endpoints/leases';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { payments } from '$lib/api/endpoints/payments';
	import type { Lease, Payment } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { leaseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import { Pencil, Trash2 } from '@lucide/svelte';
	import DocumentsPanel from '$lib/components/shared/DocumentsPanel.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const LEASE_STATUSES = ['Draft', 'Active', 'NoticeGiven', 'Expired', 'Terminated'];

	const leaseId = $derived(parseInt($page.params.id ?? '0', 10));

	const leaseQuery = createQuery(() => ({
		queryKey: ['lease', leaseId],
		queryFn: () => leases.get(leaseId),
		enabled: leaseId > 0,
	}));

	const lease = $derived(leaseQuery.data);

	// Payments for this lease
	const paymentsQuery = createQuery(() => ({
		queryKey: ['payments', portfolioId, leaseId],
		queryFn: () => payments.list(portfolioId, { leaseId, take: 200 }),
		enabled: portfolioId > 0 && leaseId > 0,
	}));

	// Form (edit dialog) state
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
		propertyId: '', unitId: '', tenantId: '', startDate: '', endDate: '',
		monthlyRent: '', securityDeposit: '', lateFeeAmount: '75', rentDueDay: '1', status: 'Draft',
	};
	let showForm = $state(false);
	let form = $state({ ...empty });
	let formErrors = $state<Record<string, string>>({});
	let showDeleteConfirm = $state(false);

	$effect(() => {
		if (form.propertyId !== formPropertyId) {
			formPropertyId = form.propertyId;
		}
	});

	function invalidateLease() {
		queryClient.invalidateQueries({ queryKey: ['lease', leaseId] });
		queryClient.invalidateQueries({ queryKey: ['leases', portfolioId] });
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => leases.update(leaseId, data),
		onSuccess: () => {
			showSuccess('Lease updated.');
			closeForm();
			invalidateLease();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const statusMutation = createMutation(() => ({
		mutationFn: (status: string) => leases.update(leaseId, { status }),
		onSuccess: () => {
			showSuccess('Lease status updated.');
			invalidateLease();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteMutation = createMutation(() => ({
		mutationFn: () => leases.delete(leaseId),
		onSuccess: () => {
			showSuccess('Lease deleted.');
			goto('/leases');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const markPaidMutation = createMutation(() => ({
		mutationFn: (paymentId: number) => payments.markPaid(paymentId, {}),
		onSuccess: () => {
			showSuccess('Payment marked as paid.');
			queryClient.invalidateQueries({ queryKey: ['payments', portfolioId, leaseId] });
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openEdit() {
		if (!lease) return;
		form = {
			propertyId: String(lease.propertyId),
			unitId: String(lease.unitId),
			tenantId: String(lease.tenantId),
			startDate: lease.startDate?.slice(0, 10) ?? '',
			endDate: lease.endDate?.slice(0, 10) ?? '',
			monthlyRent: String(lease.monthlyRent),
			securityDeposit: String(lease.securityDeposit),
			lateFeeAmount: String(lease.lateFeeAmount),
			rentDueDay: String(lease.rentDueDay),
			status: lease.status,
		};
		formPropertyId = String(lease.propertyId);
		formErrors = {};
		showForm = true;
	}
	function closeForm() {
		showForm = false;
		formErrors = {};
	}

	function submit() {
		const { propertyId: _p, ...rest } = form;
		const result = parseForm(leaseSchema, rest);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data });
	}

	// Derived labels for select triggers
	const selectedPropertyLabel = $derived(
		propertiesQuery.data?.find((p) => String(p.id) === form.propertyId)?.name ?? ''
	);
	const selectedUnitLabel = $derived(
		unitsForPropertyQuery.data?.find((u) => String(u.id) === form.unitId)
			? `Unit ${unitsForPropertyQuery.data?.find((u) => String(u.id) === form.unitId)?.unitNumber} (${unitsForPropertyQuery.data?.find((u) => String(u.id) === form.unitId)?.status})`
			: ''
	);
	const selectedTenantLabel = $derived(
		tenantsQuery.data?.find((t) => String(t.id) === form.tenantId)
			? (tenantsQuery.data?.find((t) => String(t.id) === form.tenantId)?.fullName ||
				`${tenantsQuery.data?.find((t) => String(t.id) === form.tenantId)?.firstName} ${tenantsQuery.data?.find((t) => String(t.id) === form.tenantId)?.lastName}`)
			: ''
	);

	// Payments DataGrid columns
	const paymentColumns: ColumnDef<Payment>[] = [
		{
			key: 'dueDate',
			title: 'Due Date',
			format: 'date',
			sortable: true,
			mobileRole: 'title',
		},
		{
			key: 'type',
			title: 'Type',
			mobileRole: 'subtitle',
		},
		{
			key: 'amount',
			title: 'Amount',
			format: 'currency',
			sortable: true,
			mobileRole: 'metric',
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: paymentStatusCell,
		},
		{
			key: 'actions',
			title: '',
			mobileRole: 'hidden',
			align: 'right',
			cell: paymentActionsCell,
		},
	];

	function formatCurrency(val: number): string {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(val);
	}

	function formatDate(val: string | undefined): string {
		if (!val) return '—';
		const d = new Date(val);
		return isNaN(d.getTime()) ? val : d.toLocaleDateString();
	}
</script>

{#snippet paymentStatusCell(payment: Payment)}
	<StatusBadge status={payment.status} />
{/snippet}

{#snippet paymentActionsCell(payment: Payment)}
	{#if payment.status !== 'Paid' && payment.status !== 'Waived'}
		<Button
			variant="outline"
			size="sm"
			onclick={(e) => { e.stopPropagation(); markPaidMutation.mutate(payment.id); }}
			disabled={markPaidMutation.isPending}
		>
			Mark Paid
		</Button>
	{/if}
{/snippet}

<svelte:head>
	<title>{lease ? `Lease ${lease.leaseNumber}` : 'Lease'} - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="lease-detail-page">
	{#if leaseQuery.isLoading}
		<div class="flex h-48 items-center justify-center" data-testid="lease-detail-loading">
			<div class="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
		</div>
	{:else if leaseQuery.isError || !lease}
		<div class="flex h-48 flex-col items-center justify-center gap-3 text-center" data-testid="lease-detail-not-found">
			<p class="text-sm font-medium">Lease not found.</p>
			<Button variant="outline" size="sm" onclick={() => goto('/leases')}>Back to Leases</Button>
		</div>
	{:else}
		<!-- Breadcrumb -->
		<div class="mb-4">
			<PageBreadcrumb
				crumbs={[
					{ label: 'Leases', href: '/leases' },
					{ label: lease.leaseNumber },
				]}
			/>
		</div>

		<!-- Header -->
		<div class="mb-6 flex flex-wrap items-start gap-3">
			<div class="flex-1">
				<div class="flex flex-wrap items-center gap-2">
					<h1 class="text-2xl font-bold" data-testid="lease-detail-number">{lease.leaseNumber}</h1>
					<StatusBadge status={lease.status} />
				</div>
				<p class="mt-1 text-sm text-muted-foreground">
					{#if lease.propertyName}{lease.propertyName}{/if}
					{#if lease.unitNumber} · Unit {lease.unitNumber}{/if}
					{#if lease.tenantName} · {lease.tenantName}{/if}
				</p>
			</div>
			<div class="flex flex-wrap items-center gap-2">
				{#if lease.status !== 'Active'}
					<Button data-testid="lease-set-active" variant="outline" size="sm" onclick={() => statusMutation.mutate('Active')} disabled={statusMutation.isPending}>
						Set Active
					</Button>
				{:else}
					<Button data-testid="lease-give-notice" variant="outline" size="sm" onclick={() => statusMutation.mutate('NoticeGiven')} disabled={statusMutation.isPending}>
						Give Notice
					</Button>
				{/if}
				<Button data-testid="lease-edit" variant="outline" size="sm" class="gap-1.5" onclick={openEdit}>
					<Pencil class="h-4 w-4" />
					Edit
				</Button>
				<Button data-testid="lease-delete" variant="outline" size="sm" class="gap-1.5 hover:text-destructive" onclick={() => (showDeleteConfirm = true)}>
					<Trash2 class="h-4 w-4" />
					Delete
				</Button>
			</div>
		</div>

		<!-- Info card -->
		<Card.Root class="mb-6">
			<Card.Header>
				<Card.Title class="text-base">Lease Details</Card.Title>
			</Card.Header>
			<Card.Content>
				<dl class="grid grid-cols-2 gap-x-6 gap-y-4 sm:grid-cols-3 lg:grid-cols-4">
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Start Date</dt>
						<dd class="mt-0.5 text-sm font-medium" data-testid="lease-detail-start">{formatDate(lease.startDate)}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">End Date</dt>
						<dd class="mt-0.5 text-sm font-medium" data-testid="lease-detail-end">{formatDate(lease.endDate)}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Monthly Rent</dt>
						<dd class="mt-0.5 font-mono text-sm font-semibold tabular-nums" data-testid="lease-detail-rent">{formatCurrency(lease.monthlyRent)}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Security Deposit</dt>
						<dd class="mt-0.5 font-mono text-sm tabular-nums" data-testid="lease-detail-deposit">{formatCurrency(lease.securityDeposit)}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Late Fee</dt>
						<dd class="mt-0.5 font-mono text-sm tabular-nums">{formatCurrency(lease.lateFeeAmount)}</dd>
					</div>
					<div>
						<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Rent Due Day</dt>
						<dd class="mt-0.5 text-sm">Day {lease.rentDueDay}</dd>
					</div>
					{#if lease.moveInDate}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Move-In</dt>
							<dd class="mt-0.5 text-sm">{formatDate(lease.moveInDate)}</dd>
						</div>
					{/if}
					{#if lease.moveOutDate}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Move-Out</dt>
							<dd class="mt-0.5 text-sm">{formatDate(lease.moveOutDate)}</dd>
						</div>
					{/if}
					{#if lease.notes}
						<div class="col-span-2 sm:col-span-3 lg:col-span-4">
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Notes</dt>
							<dd class="mt-0.5 text-sm text-muted-foreground">{lease.notes}</dd>
						</div>
					{/if}
				</dl>
			</Card.Content>
		</Card.Root>

		<!-- Payments section -->
		<div class="mb-2 flex items-center justify-between">
			<h2 class="text-lg font-semibold">Payments</h2>
			<a href="/deposits" class="text-xs text-muted-foreground underline-offset-4 hover:underline">View Deposits</a>
		</div>
		<DataGrid
			data={paymentsQuery.data ?? []}
			columns={paymentColumns}
			loading={paymentsQuery.isLoading}
			emptyMessage="No payments recorded for this lease."
			getRowKey={(p) => p.id}
			data-testid="lease-payments-grid"
		/>

		<!-- Documents section -->
		<div class="mt-6" data-testid="lease-detail-documents">
			<DocumentsPanel entityType="Lease" entityId={leaseId} />
		</div>
	{/if}
</div>

<!-- Edit dialog -->
<Dialog.Root
	open={showForm}
	onOpenChange={(v) => { if (!v) closeForm(); }}
>
	<Dialog.Content class="max-w-2xl">
		<Dialog.Header>
			<Dialog.Title>Edit Lease</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3 md:grid-cols-3" data-testid="lease-form">
			<Select.Root type="single" bind:value={form.propertyId}>
				<Select.Trigger class="w-full" data-testid="lease-property-input">
					{selectedPropertyLabel ? selectedPropertyLabel : 'Select property'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="Select property">Select property</Select.Item>
					{#each propertiesQuery.data || [] as property}
						<Select.Item value={String(property.id)} label={property.name}>{property.name}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<div>
				<Select.Root type="single" bind:value={form.unitId} disabled={!form.propertyId}>
					<Select.Trigger class="w-full" data-testid="lease-unit-input" disabled={!form.propertyId}>
						{selectedUnitLabel ? selectedUnitLabel : 'Select unit'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="Select unit">Select unit</Select.Item>
						{#each unitsForPropertyQuery.data || [] as unit}
							<Select.Item value={String(unit.id)} label="Unit {unit.unitNumber} ({unit.status})">Unit {unit.unitNumber} ({unit.status})</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				{#if formErrors.unitId}<p class="mt-1 text-xs text-destructive" data-testid="lease-unit-error">{formErrors.unitId}</p>{/if}
			</div>
			<div>
				<Select.Root type="single" bind:value={form.tenantId}>
					<Select.Trigger class="w-full" data-testid="lease-tenant-input">
						{selectedTenantLabel ? selectedTenantLabel : 'Select tenant'}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="" label="Select tenant">Select tenant</Select.Item>
						{#each tenantsQuery.data || [] as tenant}
							<Select.Item value={String(tenant.id)} label={tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}>{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
				{#if formErrors.tenantId}<p class="mt-1 text-xs text-destructive" data-testid="lease-tenant-error">{formErrors.tenantId}</p>{/if}
			</div>
			<div>
				<Input data-testid="lease-start-input" type="date" bind:value={form.startDate} />
				{#if formErrors.startDate}<p class="mt-1 text-xs text-destructive" data-testid="lease-start-error">{formErrors.startDate}</p>{/if}
			</div>
			<div>
				<Input data-testid="lease-end-input" type="date" bind:value={form.endDate} />
				{#if formErrors.endDate}<p class="mt-1 text-xs text-destructive" data-testid="lease-end-error">{formErrors.endDate}</p>{/if}
			</div>
			<Select.Root type="single" bind:value={form.status}>
				<Select.Trigger class="w-full" data-testid="lease-status-input">
					{form.status ? form.status : 'Select status'}
				</Select.Trigger>
				<Select.Content>
					{#each LEASE_STATUSES as s}
						<Select.Item value={s} label={s}>{s}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<div>
				<Input data-testid="lease-rent-input" bind:value={form.monthlyRent} placeholder="Monthly rent" />
				{#if formErrors.monthlyRent}<p class="mt-1 text-xs text-destructive" data-testid="lease-rent-error">{formErrors.monthlyRent}</p>{/if}
			</div>
			<div>
				<Input data-testid="lease-deposit-input" bind:value={form.securityDeposit} placeholder="Security deposit" />
				{#if formErrors.securityDeposit}<p class="mt-1 text-xs text-destructive" data-testid="lease-deposit-error">{formErrors.securityDeposit}</p>{/if}
			</div>
			<div class="grid grid-cols-2 gap-2">
				<Input data-testid="lease-late-fee-input" bind:value={form.lateFeeAmount} placeholder="Late fee" />
				<Input data-testid="lease-due-day-input" bind:value={form.rentDueDay} placeholder="Due day" />
			</div>
		</div>
		<Dialog.Footer>
			<Button data-testid="lease-form-cancel" variant="outline" onclick={closeForm}>Cancel</Button>
			<Button data-testid="lease-form-save" onclick={submit} disabled={saveMutation.isPending}>
				{saveMutation.isPending ? 'Saving…' : 'Save Lease'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={showDeleteConfirm}
	title="Delete lease"
	message={lease ? `Delete lease ${lease.leaseNumber}? This cannot be undone.` : ''}
	busy={deleteMutation.isPending}
	testid="lease-delete-confirm"
	onconfirm={() => deleteMutation.mutate()}
	oncancel={() => (showDeleteConfirm = false)}
/>
