<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { leases } from '$lib/api/endpoints/leases';
	import { properties } from '$lib/api/endpoints/properties';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Plus } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId],
		queryFn: () => leases.list(portfolioId),
	}));

	const propertiesQuery = createQuery(() => ({
		queryKey: ['properties', portfolioId],
		queryFn: () => properties.list(portfolioId),
	}));

	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId),
	}));

	let selectedPropertyId = $state('');
	const unitsForPropertyQuery = createQuery(() => ({
		queryKey: ['units-for-lease', selectedPropertyId],
		enabled: !!selectedPropertyId,
		queryFn: () => properties.listUnits(Number(selectedPropertyId)),
	}));

	let showCreate = $state(false);
	let form = $state({
		propertyId: '',
		unitId: '',
		tenantId: '',
		startDate: '',
		endDate: '',
		monthlyRent: '',
		securityDeposit: '',
		lateFeeAmount: '75',
		rentDueDay: '1',
		status: 'Draft',
	});

	const createLeaseMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => leases.create(data),
		onSuccess: () => {
			showCreate = false;
			form = { propertyId: '', unitId: '', tenantId: '', startDate: '', endDate: '', monthlyRent: '', securityDeposit: '', lateFeeAmount: '75', rentDueDay: '1', status: 'Draft' };
			selectedPropertyId = '';
			queryClient.invalidateQueries({ queryKey: ['leases', portfolioId] });
		},
	}));

	$effect(() => {
		if (form.propertyId !== selectedPropertyId) {
			selectedPropertyId = form.propertyId;
			form.unitId = '';
		}
	});

	function submit() {
		if (!form.propertyId || !form.unitId || !form.tenantId || !form.startDate || !form.endDate || !form.monthlyRent || !form.securityDeposit) return;
		createLeaseMutation.mutate({
			portfolioId,
			unitId: Number(form.unitId),
			tenantId: Number(form.tenantId),
			startDate: form.startDate,
			endDate: form.endDate,
			monthlyRent: Number(form.monthlyRent),
			securityDeposit: Number(form.securityDeposit),
			lateFeeAmount: Number(form.lateFeeAmount),
			rentDueDay: Number(form.rentDueDay),
			status: form.status,
		});
	}

	const statusMutation = createMutation(() => ({
		mutationFn: ({ id, status }: { id: number; status: string }) => leases.update(id, { status }),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['leases', portfolioId] }),
	}));
</script>

<svelte:head>
	<title>Leases - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-4 flex items-center justify-between">
		<div>
			<h1 class="text-2xl font-bold">Leases</h1>
			<p class="text-sm text-text-secondary">Lease lifecycle, rent terms, and status updates.</p>
		</div>
		<button class="inline-flex items-center gap-2 rounded-md bg-accent px-3 py-2 text-sm text-white" onclick={() => (showCreate = !showCreate)}>
			<Plus class="h-4 w-4" />
			New Lease
		</button>
	</div>

	{#if showCreate}
		<div class="mb-5 rounded-lg border border-border bg-surface p-4">
			<div class="grid gap-3 md:grid-cols-3">
				<select bind:value={form.propertyId} class="rounded border border-border bg-bg px-3 py-2 text-sm">
					<option value="">Select property</option>
					{#each propertiesQuery.data || [] as property}
						<option value={property.id}>{property.name}</option>
					{/each}
				</select>
				<select bind:value={form.unitId} class="rounded border border-border bg-bg px-3 py-2 text-sm" disabled={!form.propertyId}>
					<option value="">Select unit</option>
					{#each unitsForPropertyQuery.data || [] as unit}
						<option value={unit.id}>Unit {unit.unitNumber} ({unit.status})</option>
					{/each}
				</select>
				<select bind:value={form.tenantId} class="rounded border border-border bg-bg px-3 py-2 text-sm">
					<option value="">Select tenant</option>
					{#each tenantsQuery.data || [] as tenant}
						<option value={tenant.id}>{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}</option>
					{/each}
				</select>
				<input type="date" bind:value={form.startDate} class="rounded border border-border bg-bg px-3 py-2 text-sm" />
				<input type="date" bind:value={form.endDate} class="rounded border border-border bg-bg px-3 py-2 text-sm" />
				<select bind:value={form.status} class="rounded border border-border bg-bg px-3 py-2 text-sm">
					<option>Draft</option><option>Active</option><option>NoticeGiven</option><option>Expired</option><option>Terminated</option>
				</select>
				<input bind:value={form.monthlyRent} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Monthly rent" />
				<input bind:value={form.securityDeposit} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Security deposit" />
				<div class="grid grid-cols-2 gap-2">
					<input bind:value={form.lateFeeAmount} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Late fee" />
					<input bind:value={form.rentDueDay} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Due day" />
				</div>
			</div>
			<div class="mt-3">
				<button onclick={submit} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createLeaseMutation.isPending}>Save Lease</button>
			</div>
		</div>
	{/if}

	<div class="rounded-lg border border-border bg-surface">
		<div class="overflow-x-auto">
			<table class="min-w-full text-sm">
				<thead class="border-b border-border bg-bg text-left text-xs uppercase text-text-tertiary">
					<tr>
						<th class="px-3 py-2">Lease</th>
						<th class="px-3 py-2">Tenant</th>
						<th class="px-3 py-2">Unit</th>
						<th class="px-3 py-2">Rent</th>
						<th class="px-3 py-2">Term</th>
						<th class="px-3 py-2">Status</th>
						<th class="px-3 py-2">Action</th>
					</tr>
				</thead>
				<tbody>
					{#each leasesQuery.data || [] as lease}
						<tr class="border-b border-border/70">
							<td class="px-3 py-2 font-medium">{lease.leaseNumber}</td>
							<td class="px-3 py-2">{lease.tenantName || '—'}</td>
							<td class="px-3 py-2">{lease.propertyName} · {lease.unitNumber}</td>
							<td class="px-3 py-2">${lease.monthlyRent}</td>
							<td class="px-3 py-2 text-text-secondary">{new Date(lease.startDate).toLocaleDateString()} - {new Date(lease.endDate).toLocaleDateString()}</td>
							<td class="px-3 py-2">{lease.status}</td>
							<td class="px-3 py-2">
								{#if lease.status !== 'Active'}
									<button class="rounded border border-border px-2 py-1 text-xs" onclick={() => statusMutation.mutate({ id: lease.id, status: 'Active' })}>Set Active</button>
								{:else}
									<button class="rounded border border-border px-2 py-1 text-xs" onclick={() => statusMutation.mutate({ id: lease.id, status: 'NoticeGiven' })}>Give Notice</button>
								{/if}
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
</div>
