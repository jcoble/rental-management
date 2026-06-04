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
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Pencil, Save, Trash2, X } from '@lucide/svelte';
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
		leaseNumber: '', propertyId: '', unitId: '', tenantId: '', startDate: '', endDate: '',
		monthlyRent: '', securityDeposit: '', lateFeeAmount: '75', rentDueDay: '1', status: 'Draft', notes: '',
	};
	let editing = $state(false);
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
			editing = false;
			formErrors = {};
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

	let leaseQuestion = $state('');
	let leaseAnswer = $state('');
	let leaseAnswerSources = $state<string[]>([]);

	const askLeaseMutation = createMutation(() => ({
		mutationFn: (question: string) => leases.ask(leaseId, question),
		onSuccess: (result) => {
			leaseAnswer = result.answer;
			leaseAnswerSources = result.sources;
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function askLease() {
		const question = leaseQuestion.trim();
		if (!question) return;
		askLeaseMutation.mutate(question);
	}

	function startEditing() {
		if (!lease) return;
		form = {
			leaseNumber: lease.leaseNumber,
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
			notes: lease.notes ?? '',
		};
		formPropertyId = String(lease.propertyId);
		formErrors = {};
		editing = true;
	}
	function cancelEditing() {
		editing = false;
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

	// Select options for inline FK/enum fields
	const statusOptions = $derived(LEASE_STATUSES.map((value) => ({ value, label: value })));
	const propertyOptions = $derived([
		{ value: '', label: 'Select property' },
		...(propertiesQuery.data ?? []).map((p) => ({ value: String(p.id), label: p.name })),
	]);
	const unitOptions = $derived([
		{ value: '', label: 'Select unit' },
		...(unitsForPropertyQuery.data ?? []).map((u) => ({
			value: String(u.id),
			label: `Unit ${u.unitNumber} (${u.status})`,
		})),
	]);
	const tenantOptions = $derived([
		{ value: '', label: 'Select tenant' },
		...(tenantsQuery.data ?? []).map((t) => ({
			value: String(t.id),
			label: t.fullName || `${t.firstName} ${t.lastName}`,
		})),
	]);

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

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="lease-detail-page">
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
				{#if editing}
					<Button data-testid="lease-detail-cancel" variant="outline" size="sm" class="gap-1.5" onclick={cancelEditing} disabled={saveMutation.isPending}>
						<X class="h-4 w-4" />
						Cancel
					</Button>
					<Button data-testid="lease-detail-save" size="sm" class="gap-1.5" onclick={submit} disabled={saveMutation.isPending}>
						<Save class="h-4 w-4" />
						{saveMutation.isPending ? 'Saving…' : 'Save'}
					</Button>
				{:else}
					{#if lease.status !== 'Active'}
						<Button data-testid="lease-set-active" variant="outline" size="sm" onclick={() => statusMutation.mutate('Active')} disabled={statusMutation.isPending}>
							Set Active
						</Button>
					{:else}
						<Button data-testid="lease-give-notice" variant="outline" size="sm" onclick={() => statusMutation.mutate('NoticeGiven')} disabled={statusMutation.isPending}>
							Give Notice
						</Button>
					{/if}
					<Button data-testid="lease-edit" variant="outline" size="sm" class="gap-1.5" onclick={startEditing}>
						<Pencil class="h-4 w-4" />
						Edit
					</Button>
					<Button data-testid="lease-delete" variant="outline" size="sm" class="gap-1.5 hover:text-destructive" onclick={() => (showDeleteConfirm = true)}>
						<Trash2 class="h-4 w-4" />
						Delete
					</Button>
				{/if}
			</div>
		</div>

		<!-- Info card -->
		<Card.Root class="mb-6">
			<Card.Header>
				<Card.Title class="text-base">Lease Details</Card.Title>
			</Card.Header>
			<Card.Content>
				<div class="grid grid-cols-2 gap-x-6 gap-y-4 sm:grid-cols-3 lg:grid-cols-4">
					<InlineField label="Lease Number" bind:value={form.leaseNumber} display={lease.leaseNumber} {editing} onedit={startEditing} error={formErrors.leaseNumber} testid="lease-detail-number-field" />
					<InlineField label="Status" bind:value={form.status} display={lease.status} {editing} onedit={startEditing} type="select" options={statusOptions} error={formErrors.status} testid="lease-detail-status" />
					<InlineField label="Property" bind:value={form.propertyId} display={lease.propertyName} {editing} onedit={startEditing} type="select" options={propertyOptions} testid="lease-detail-property" />
					<InlineField label="Unit" bind:value={form.unitId} display={lease.unitNumber ? `Unit ${lease.unitNumber}` : ''} {editing} onedit={startEditing} type="select" options={unitOptions} error={formErrors.unitId} testid="lease-detail-unit" />
					<InlineField label="Tenant" bind:value={form.tenantId} display={lease.tenantName} {editing} onedit={startEditing} type="select" options={tenantOptions} error={formErrors.tenantId} testid="lease-detail-tenant" />
					<InlineField label="Start Date" bind:value={form.startDate} display={formatDate(lease.startDate)} {editing} onedit={startEditing} type="date" error={formErrors.startDate} testid="lease-detail-start" />
					<InlineField label="End Date" bind:value={form.endDate} display={formatDate(lease.endDate)} {editing} onedit={startEditing} type="date" error={formErrors.endDate} testid="lease-detail-end" />
					<InlineField label="Monthly Rent" bind:value={form.monthlyRent} display={formatCurrency(lease.monthlyRent)} {editing} onedit={startEditing} type="number" error={formErrors.monthlyRent} testid="lease-detail-rent" />
					<InlineField label="Security Deposit" bind:value={form.securityDeposit} display={formatCurrency(lease.securityDeposit)} {editing} onedit={startEditing} type="number" error={formErrors.securityDeposit} testid="lease-detail-deposit" />
					<InlineField label="Late Fee" bind:value={form.lateFeeAmount} display={formatCurrency(lease.lateFeeAmount)} {editing} onedit={startEditing} type="number" error={formErrors.lateFeeAmount} testid="lease-detail-late-fee" />
					<InlineField label="Rent Due Day" bind:value={form.rentDueDay} display={`Day ${lease.rentDueDay}`} {editing} onedit={startEditing} type="number" error={formErrors.rentDueDay} testid="lease-detail-due-day" />
					{#if !editing && lease.moveInDate}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Move-In</dt>
							<dd class="mt-0.5 text-sm">{formatDate(lease.moveInDate)}</dd>
						</div>
					{/if}
					{#if !editing && lease.moveOutDate}
						<div>
							<dt class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Move-Out</dt>
							<dd class="mt-0.5 text-sm">{formatDate(lease.moveOutDate)}</dd>
						</div>
					{/if}
					<InlineField label="Notes" bind:value={form.notes} display={lease.notes} {editing} onedit={startEditing} type="textarea" error={formErrors.notes} testid="lease-detail-notes" class="col-span-2 sm:col-span-3 lg:col-span-4" />
				</div>
			</Card.Content>
		</Card.Root>

		<Card.Root class="mb-6" data-testid="lease-qa-card">
			<Card.Header>
				<Card.Title class="text-base">Ask This Lease</Card.Title>
				<Card.Description>Answers are grounded in stored lease dates, rent, fees, deposit, and notes.</Card.Description>
			</Card.Header>
			<Card.Content class="space-y-3">
				<div class="flex flex-col gap-2 sm:flex-row">
					<Input
						data-testid="lease-question-input"
						bind:value={leaseQuestion}
						placeholder="Can I have a dog? When is rent due?"
						onkeydown={(e) => { if (e.key === 'Enter') askLease(); }}
					/>
					<Button data-testid="lease-question-submit" onclick={askLease} disabled={askLeaseMutation.isPending || !leaseQuestion.trim()}>
						{askLeaseMutation.isPending ? 'Answering...' : 'Ask'}
					</Button>
				</div>
				{#if leaseAnswer}
					<div class="rounded-md border border-border bg-muted/30 p-3" data-testid="lease-question-answer">
						<p class="text-sm leading-6">{leaseAnswer}</p>
						{#if leaseAnswerSources.length > 0}
							<details class="mt-2 text-xs text-muted-foreground">
								<summary>Lease facts used</summary>
								<ul class="mt-2 list-disc space-y-1 pl-5">
									{#each leaseAnswerSources as source}
										<li>{source}</li>
									{/each}
								</ul>
							</details>
						{/if}
					</div>
				{/if}
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

<ConfirmDialog
	open={showDeleteConfirm}
	title="Delete lease"
	message={lease ? `Delete lease ${lease.leaseNumber}? This cannot be undone.` : ''}
	busy={deleteMutation.isPending}
	testid="lease-delete-confirm"
	onconfirm={() => deleteMutation.mutate()}
	oncancel={() => (showDeleteConfirm = false)}
/>
