<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, Pencil, Save, Trash2, X } from '@lucide/svelte';
	import { leases } from '$lib/api/endpoints/leases';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { leaseSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';

	const queryClient = useQueryClient();
	const leaseId = $derived(parseInt($page.params.id ?? '0', 10));
	const portfolioId = $derived(getCurrentPortfolioId());
	const STATUSES = ['Draft', 'Active', 'NoticeGiven', 'Expired', 'Terminated'];

	let editing = $state(false);
	let form = $state({
		leaseNumber: '',
		propertyId: '',
		unitId: '',
		tenantId: '',
		startDate: '',
		endDate: '',
		moveInDate: '',
		moveOutDate: '',
		monthlyRent: '',
		securityDeposit: '',
		lateFeeAmount: '',
		rentDueDay: '',
		status: 'Draft',
		notes: ''
	});
	let formErrors = $state<Record<string, string>>({});

	const leaseQuery = createQuery(() => ({ queryKey: ['lease', leaseId], queryFn: () => leases.get(leaseId), enabled: leaseId > 0 }));
	const propertiesQuery = createQuery(() => ({ queryKey: ['properties', portfolioId], queryFn: () => properties.list(portfolioId, { take: 200 }) }));
	const tenantsQuery = createQuery(() => ({ queryKey: ['tenants', portfolioId], queryFn: () => tenants.list(portfolioId, { take: 200 }) }));
	const unitsQuery = createQuery(() => ({
		queryKey: ['units', Number(form.propertyId || 0)],
		queryFn: () => properties.listUnits(Number(form.propertyId)),
		enabled: editing && Boolean(form.propertyId)
	}));

	const lease = $derived(leaseQuery.data);
	const statusOptions = $derived(STATUSES.map((value) => ({ value, label: value })));
	const propertyOptions = $derived([{ value: '', label: 'Select property' }, ...(propertiesQuery.data ?? []).map((p) => ({ value: String(p.id), label: p.name }))]);
	const tenantOptions = $derived([{ value: '', label: 'Select tenant' }, ...(tenantsQuery.data ?? []).map((t) => ({ value: String(t.id), label: t.fullName || `${t.firstName} ${t.lastName}` }))]);
	const unitOptions = $derived([{ value: '', label: 'Select unit' }, ...(unitsQuery.data ?? []).map((u) => ({ value: String(u.id), label: `Unit ${u.unitNumber} (${u.status})` }))]);

	function startEditing() {
		if (!lease) return;
		form = {
			leaseNumber: lease.leaseNumber,
			propertyId: String(lease.propertyId),
			unitId: String(lease.unitId),
			tenantId: String(lease.tenantId),
			startDate: lease.startDate?.slice(0, 10) ?? '',
			endDate: lease.endDate?.slice(0, 10) ?? '',
			moveInDate: lease.moveInDate?.slice(0, 10) ?? '',
			moveOutDate: lease.moveOutDate?.slice(0, 10) ?? '',
			monthlyRent: String(lease.monthlyRent),
			securityDeposit: String(lease.securityDeposit),
			lateFeeAmount: String(lease.lateFeeAmount),
			rentDueDay: String(lease.rentDueDay),
			status: lease.status,
			notes: lease.notes ?? ''
		};
		formErrors = {};
		editing = true;
	}

	function cancelEditing() {
		editing = false;
		formErrors = {};
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => leases.update(leaseId, data),
		onSuccess: () => {
			showSuccess('Lease updated.');
			editing = false;
			queryClient.invalidateQueries({ queryKey: ['lease', leaseId] });
			queryClient.invalidateQueries({ queryKey: ['leases', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function saveLease() {
		const result = parseForm(leaseSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data });
	}

	const deleteMutation = createMutation(() => ({
		mutationFn: () => leases.delete(leaseId),
		onSuccess: () => {
			showSuccess('Lease deleted.');
			goto('/leases');
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));
</script>

<svelte:head>
	<title>{lease?.leaseNumber ?? 'Lease'} - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="lease-detail-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-3">
		<div class="min-w-0">
			<Button variant="ghost" href="/leases" class="mb-2 -ml-3"><ArrowLeft class="h-4 w-4" />Leases</Button>
			<h1 class="truncate text-2xl font-bold">{lease?.leaseNumber ?? 'Lease'}</h1>
			<p class="text-sm text-muted-foreground">{lease?.tenantName ?? ''}{#if lease?.propertyName} · {lease.propertyName}{/if}</p>
		</div>
		{#if lease}
			<div class="flex gap-2">
				{#if editing}
					<Button variant="outline" onclick={cancelEditing} disabled={saveMutation.isPending}><X class="h-4 w-4" />Cancel</Button>
					<Button onclick={saveLease} disabled={saveMutation.isPending}><Save class="h-4 w-4" />{saveMutation.isPending ? 'Saving...' : 'Save'}</Button>
				{:else}
					<Button variant="outline" onclick={startEditing}><Pencil class="h-4 w-4" />Edit</Button>
					<Button variant="destructive" onclick={() => deleteMutation.mutate()} disabled={deleteMutation.isPending}><Trash2 class="h-4 w-4" />Delete</Button>
				{/if}
			</div>
		{/if}
	</div>

	{#if leaseQuery.isLoading}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Loading lease...</div>
	{:else if !lease}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Lease not found.</div>
	{:else}
		<Card.Root>
			<Card.Header>
				<Card.Title>Lease Details</Card.Title>
				<Card.Description>Resident, unit, dates, and rent terms.</Card.Description>
			</Card.Header>
			<Card.Content>
				<div class="grid gap-4 md:grid-cols-3">
					<InlineField label="Lease number" bind:value={form.leaseNumber} display={lease.leaseNumber} {editing} error={formErrors.leaseNumber} testid="lease-detail-number" />
					<InlineField label="Status" bind:value={form.status} display={lease.status} {editing} type="select" options={statusOptions} testid="lease-detail-status" />
					<InlineField label="Tenant" bind:value={form.tenantId} display={lease.tenantName} {editing} type="select" options={tenantOptions} error={formErrors.tenantId} testid="lease-detail-tenant" />
					<InlineField label="Property" bind:value={form.propertyId} display={lease.propertyName} {editing} type="select" options={propertyOptions} error={formErrors.propertyId} testid="lease-detail-property" />
					<InlineField label="Unit" bind:value={form.unitId} display={lease.unitNumber ? `Unit ${lease.unitNumber}` : ''} {editing} type="select" options={unitOptions} error={formErrors.unitId} testid="lease-detail-unit" />
					<InlineField label="Monthly rent" bind:value={form.monthlyRent} display={`$${lease.monthlyRent}`} {editing} error={formErrors.monthlyRent} testid="lease-detail-rent" />
					<InlineField label="Start date" bind:value={form.startDate} display={new Date(lease.startDate).toLocaleDateString()} {editing} type="date" error={formErrors.startDate} testid="lease-detail-start" />
					<InlineField label="End date" bind:value={form.endDate} display={new Date(lease.endDate).toLocaleDateString()} {editing} type="date" error={formErrors.endDate} testid="lease-detail-end" />
					<InlineField label="Move-in date" bind:value={form.moveInDate} display={lease.moveInDate ? new Date(lease.moveInDate).toLocaleDateString() : ''} {editing} type="date" testid="lease-detail-move-in" />
					<InlineField label="Move-out date" bind:value={form.moveOutDate} display={lease.moveOutDate ? new Date(lease.moveOutDate).toLocaleDateString() : ''} {editing} type="date" testid="lease-detail-move-out" />
					<InlineField label="Security deposit" bind:value={form.securityDeposit} display={`$${lease.securityDeposit}`} {editing} error={formErrors.securityDeposit} testid="lease-detail-deposit" />
					<InlineField label="Late fee" bind:value={form.lateFeeAmount} display={`$${lease.lateFeeAmount}`} {editing} error={formErrors.lateFeeAmount} testid="lease-detail-late-fee" />
					<InlineField label="Rent due day" bind:value={form.rentDueDay} display={lease.rentDueDay} {editing} error={formErrors.rentDueDay} testid="lease-detail-due-day" />
					<InlineField label="Notes" bind:value={form.notes} display={lease.notes} {editing} type="textarea" testid="lease-detail-notes" class="md:col-span-3" />
				</div>
			</Card.Content>
		</Card.Root>
	{/if}
</div>
