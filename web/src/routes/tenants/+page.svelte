<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Plus } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId),
	}));

	let showCreate = $state(false);
	let form = $state({ firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' });

	const createTenantMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => tenants.create(data),
		onSuccess: () => {
			showCreate = false;
			form = { firstName: '', lastName: '', email: '', phone: '', emergencyContact: '' };
			queryClient.invalidateQueries({ queryKey: ['tenants', portfolioId] });
		},
	}));

	function submit() {
		if (!form.firstName || !form.lastName) return;
		createTenantMutation.mutate({
			portfolioId,
			firstName: form.firstName,
			lastName: form.lastName,
			email: form.email || null,
			phone: form.phone || null,
			emergencyContact: form.emergencyContact || null,
		});
	}
</script>

<svelte:head>
	<title>Tenants - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-4 flex items-center justify-between">
		<div>
			<h1 class="text-2xl font-bold">Tenants</h1>
			<p class="text-sm text-text-secondary">Resident contacts and lease participation.</p>
		</div>
		<button class="inline-flex items-center gap-2 rounded-md bg-accent px-3 py-2 text-sm text-white" onclick={() => (showCreate = !showCreate)}>
			<Plus class="h-4 w-4" />
			New Tenant
		</button>
	</div>

	{#if showCreate}
		<div class="mb-5 rounded-lg border border-border bg-surface p-4">
			<div class="grid gap-3 md:grid-cols-2">
				<input bind:value={form.firstName} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="First name" />
				<input bind:value={form.lastName} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Last name" />
				<input bind:value={form.email} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Email" />
				<input bind:value={form.phone} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Phone" />
				<input bind:value={form.emergencyContact} class="rounded border border-border bg-bg px-3 py-2 text-sm md:col-span-2" placeholder="Emergency contact" />
			</div>
			<div class="mt-3">
				<button onclick={submit} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createTenantMutation.isPending}>Save Tenant</button>
			</div>
		</div>
	{/if}

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
					</tr>
				</thead>
				<tbody>
					{#each tenantsQuery.data || [] as tenant}
						<tr class="border-b border-border/70">
							<td class="px-3 py-2 font-medium">{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}</td>
							<td class="px-3 py-2 text-text-secondary">{tenant.email || '—'}</td>
							<td class="px-3 py-2 text-text-secondary">{tenant.phone || '—'}</td>
							<td class="px-3 py-2 text-text-secondary">{tenant.emergencyContact || '—'}</td>
							<td class="px-3 py-2">{tenant.activeLeaseCount || 0}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
</div>
