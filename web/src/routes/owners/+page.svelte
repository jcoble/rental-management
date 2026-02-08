<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { owners } from '$lib/api/endpoints/owners';
	import { vendors } from '$lib/api/endpoints/vendors';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId],
		queryFn: () => owners.list(portfolioId),
	}));
	const vendorsQuery = createQuery(() => ({
		queryKey: ['vendors', portfolioId],
		queryFn: () => vendors.list(portfolioId),
	}));

	let ownerForm = $state({ name: '', email: '', phone: '' });
	let vendorForm = $state({ name: '', serviceType: 'Plumbing', email: '', phone: '', is1099Eligible: true, w9OnFile: false, preferred: false });

	const createOwnerMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => owners.create(data),
		onSuccess: () => {
			ownerForm = { name: '', email: '', phone: '' };
			queryClient.invalidateQueries({ queryKey: ['owners', portfolioId] });
		},
	}));
	const createVendorMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => vendors.create(data),
		onSuccess: () => {
			vendorForm = { name: '', serviceType: 'Plumbing', email: '', phone: '', is1099Eligible: true, w9OnFile: false, preferred: false };
			queryClient.invalidateQueries({ queryKey: ['vendors', portfolioId] });
		},
	}));

	function submitOwner() {
		if (!ownerForm.name) return;
		createOwnerMutation.mutate({
			portfolioId,
			name: ownerForm.name,
			email: ownerForm.email || null,
			phone: ownerForm.phone || null,
		});
	}

	function submitVendor() {
		if (!vendorForm.name || !vendorForm.serviceType) return;
		createVendorMutation.mutate({
			portfolioId,
			name: vendorForm.name,
			serviceType: vendorForm.serviceType,
			email: vendorForm.email || null,
			phone: vendorForm.phone || null,
			is1099Eligible: vendorForm.is1099Eligible,
			w9OnFile: vendorForm.w9OnFile,
			preferred: vendorForm.preferred,
		});
	}
</script>

<svelte:head>
	<title>Owners & Vendors - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Owners & Vendors</h1>
		<p class="text-sm text-text-secondary">Manage ownership contacts and service provider compliance data.</p>
	</div>

	<div class="mb-5 grid gap-4 lg:grid-cols-2">
		<div class="rounded-lg border border-border bg-surface p-4">
			<h2 class="mb-3 font-semibold">Add Owner</h2>
			<div class="space-y-2">
				<input bind:value={ownerForm.name} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Owner name" />
				<input bind:value={ownerForm.email} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Owner email" />
				<input bind:value={ownerForm.phone} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Owner phone" />
				<button onclick={submitOwner} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createOwnerMutation.isPending}>Save Owner</button>
			</div>
		</div>
		<div class="rounded-lg border border-border bg-surface p-4">
			<h2 class="mb-3 font-semibold">Add Vendor</h2>
			<div class="space-y-2">
				<input bind:value={vendorForm.name} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Vendor name" />
				<input bind:value={vendorForm.serviceType} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Service type" />
				<input bind:value={vendorForm.email} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Vendor email" />
				<input bind:value={vendorForm.phone} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Vendor phone" />
				<div class="flex gap-4 text-sm">
					<label><input type="checkbox" bind:checked={vendorForm.is1099Eligible} /> 1099 eligible</label>
					<label><input type="checkbox" bind:checked={vendorForm.w9OnFile} /> W-9 on file</label>
					<label><input type="checkbox" bind:checked={vendorForm.preferred} /> Preferred</label>
				</div>
				<button onclick={submitVendor} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createVendorMutation.isPending}>Save Vendor</button>
			</div>
		</div>
	</div>

	<div class="grid gap-4 lg:grid-cols-2">
		<div class="rounded-lg border border-border bg-surface">
			<div class="border-b border-border px-4 py-3 font-semibold">Owners</div>
			<div class="p-3 space-y-2">
				{#each ownersQuery.data || [] as owner}
					<div class="rounded border border-border bg-bg p-3 text-sm">
						<p class="font-medium">{owner.name}</p>
						<p class="text-xs text-text-secondary">{owner.email || 'No email'} · {owner.phone || 'No phone'}</p>
						<p class="text-xs text-text-secondary">{owner.propertyCount || 0} properties</p>
					</div>
				{/each}
			</div>
		</div>
		<div class="rounded-lg border border-border bg-surface">
			<div class="border-b border-border px-4 py-3 font-semibold">Vendors</div>
			<div class="p-3 space-y-2">
				{#each vendorsQuery.data || [] as vendor}
					<div class="rounded border border-border bg-bg p-3 text-sm">
						<div class="flex items-center justify-between">
							<p class="font-medium">{vendor.name}</p>
							<span class="text-xs text-text-secondary">{vendor.serviceType}</span>
						</div>
						<p class="text-xs text-text-secondary">{vendor.email || 'No email'} · {vendor.phone || 'No phone'}</p>
						<p class="text-xs text-text-secondary">1099: {vendor.is1099Eligible ? 'Yes' : 'No'} · W-9: {vendor.w9OnFile ? 'On file' : 'Missing'}</p>
					</div>
				{/each}
			</div>
		</div>
	</div>
</div>
