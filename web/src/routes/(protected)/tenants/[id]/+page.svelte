<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/stores';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ArrowLeft, Pencil, Save, Trash2, X } from '@lucide/svelte';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { leases } from '$lib/api/endpoints/leases';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { tenantSchema, parseForm } from '$lib/schemas';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';

	const queryClient = useQueryClient();
	const tenantId = $derived(parseInt($page.params.id ?? '0', 10));
	const portfolioId = $derived(getCurrentPortfolioId());

	let editing = $state(false);
	let form = $state({
		firstName: '',
		lastName: '',
		email: '',
		phone: '',
		emergencyContact: ''
	});
	let formErrors = $state<Record<string, string>>({});

	const tenantQuery = createQuery(() => ({
		queryKey: ['tenant', tenantId],
		queryFn: () => tenants.get(tenantId),
		enabled: tenantId > 0
	}));
	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId, 'tenant', tenantId],
		queryFn: () => leases.list(portfolioId, { tenantId, take: 100 }),
		enabled: tenantId > 0
	}));

	const tenant = $derived(tenantQuery.data);
	const tenantName = $derived(tenant?.fullName || (tenant ? `${tenant.firstName} ${tenant.lastName}` : 'Tenant'));

	function startEditing() {
		if (!tenant) return;
		form = {
			firstName: tenant.firstName,
			lastName: tenant.lastName,
			email: tenant.email ?? '',
			phone: tenant.phone ?? '',
			emergencyContact: tenant.emergencyContact ?? ''
		};
		formErrors = {};
		editing = true;
	}

	function cancelEditing() {
		editing = false;
		formErrors = {};
	}

	const saveMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => tenants.update(tenantId, data),
		onSuccess: () => {
			showSuccess('Tenant updated.');
			editing = false;
			queryClient.invalidateQueries({ queryKey: ['tenant', tenantId] });
			queryClient.invalidateQueries({ queryKey: ['tenants', portfolioId] });
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function saveTenant() {
		const result = parseForm(tenantSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		saveMutation.mutate({ portfolioId, ...result.data });
	}

	const deleteMutation = createMutation(() => ({
		mutationFn: () => tenants.delete(tenantId),
		onSuccess: () => {
			showSuccess('Tenant deleted.');
			goto('/tenants');
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));
</script>

<svelte:head>
	<title>{tenantName} - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6" data-testid="tenant-detail-page">
	<div class="mb-5 flex flex-wrap items-start justify-between gap-3">
		<div class="min-w-0">
			<Button variant="ghost" href="/tenants" class="mb-2 -ml-3">
				<ArrowLeft class="h-4 w-4" />
				Tenants
			</Button>
			<h1 class="truncate text-2xl font-bold">{tenantName}</h1>
			<p class="text-sm text-muted-foreground">{tenant?.email ?? 'No email on file'}</p>
		</div>
		{#if tenant}
			<div class="flex gap-2">
				{#if editing}
					<Button variant="outline" onclick={cancelEditing} disabled={saveMutation.isPending}>
						<X class="h-4 w-4" />
						Cancel
					</Button>
					<Button onclick={saveTenant} disabled={saveMutation.isPending}>
						<Save class="h-4 w-4" />
						{saveMutation.isPending ? 'Saving...' : 'Save'}
					</Button>
				{:else}
					<Button variant="outline" onclick={startEditing}>
						<Pencil class="h-4 w-4" />
						Edit
					</Button>
					<Button variant="destructive" onclick={() => deleteMutation.mutate()} disabled={deleteMutation.isPending}>
						<Trash2 class="h-4 w-4" />
						Delete
					</Button>
				{/if}
			</div>
		{/if}
	</div>

	{#if tenantQuery.isLoading}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Loading tenant...</div>
	{:else if !tenant}
		<div class="rounded-lg border border-border bg-card p-6 text-sm text-muted-foreground">Tenant not found.</div>
	{:else}
		<div class="grid gap-5 xl:grid-cols-[minmax(0,1fr)_380px]">
			<Card.Root>
				<Card.Header>
					<Card.Title>Contact Details</Card.Title>
					<Card.Description>Inline edit mode keeps the whole resident record visible.</Card.Description>
				</Card.Header>
				<Card.Content>
					<div class="grid gap-4 md:grid-cols-2">
						<InlineField label="First name" bind:value={form.firstName} display={tenant.firstName} {editing} error={formErrors.firstName} testid="tenant-detail-first-name" />
						<InlineField label="Last name" bind:value={form.lastName} display={tenant.lastName} {editing} error={formErrors.lastName} testid="tenant-detail-last-name" />
						<InlineField label="Email" bind:value={form.email} display={tenant.email} {editing} type="email" error={formErrors.email} testid="tenant-detail-email" />
						<InlineField label="Phone" bind:value={form.phone} display={tenant.phone} {editing} type="tel" testid="tenant-detail-phone" />
						<InlineField label="Emergency contact" bind:value={form.emergencyContact} display={tenant.emergencyContact} {editing} testid="tenant-detail-emergency-contact" class="md:col-span-2" />
					</div>
				</Card.Content>
			</Card.Root>

			<Card.Root>
				<Card.Header>
					<Card.Title>Leases</Card.Title>
					<Card.Description>Current and historical lease participation.</Card.Description>
				</Card.Header>
				<Card.Content>
					<div class="space-y-2" data-testid="tenant-detail-leases-list">
						{#if leasesQuery.isLoading}
							<p class="text-sm text-muted-foreground">Loading leases...</p>
						{:else if !(leasesQuery.data?.length)}
							<p class="rounded border border-border bg-background p-3 text-sm text-muted-foreground">No leases linked.</p>
						{:else}
							{#each leasesQuery.data as lease}
								<a href={`/leases/${lease.id}`} class="block rounded border border-border bg-background p-3 text-sm hover:border-ring" data-testid="tenant-detail-lease-row">
									<div class="flex items-center justify-between gap-2">
										<p class="font-medium">{lease.leaseNumber}</p>
										<span class="text-xs text-muted-foreground">{lease.status}</span>
									</div>
									<p class="text-xs text-muted-foreground">{lease.propertyName} · Unit {lease.unitNumber} · ${lease.monthlyRent}/mo</p>
								</a>
							{/each}
						{/if}
					</div>
				</Card.Content>
			</Card.Root>
		</div>
	{/if}
</div>
