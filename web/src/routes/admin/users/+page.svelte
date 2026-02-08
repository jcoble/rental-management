<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { auth } from '$lib/api/endpoints/auth';
	import { owners } from '$lib/api/endpoints/owners';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { clearAuth, hasAnyRole, setCurrentUser } from '$lib/stores/auth.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const meQuery = createQuery(() => ({
		queryKey: ['auth-me'],
		queryFn: () => auth.me(),
		retry: false,
	}));

	$effect(() => {
		if (meQuery.data) {
			setCurrentUser(meQuery.data);
			if (!hasAnyRole('Admin', 'Manager')) goto('/portal');
		}
		if (meQuery.isError) {
			clearAuth();
			goto('/login');
		}
	});

	const usersQuery = createQuery(() => ({
		queryKey: ['auth-users', portfolioId],
		enabled: hasAnyRole('Admin', 'Manager'),
		queryFn: () => auth.listUsers(portfolioId),
	}));

	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId],
		queryFn: () => owners.list(portfolioId),
	}));

	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		queryFn: () => tenants.list(portfolioId),
	}));

	let form = $state({ displayName: '', email: '', password: '', role: 'Tenant', ownerId: '', tenantId: '' });

	const createUserMutation = createMutation(() => ({
		mutationFn: () => auth.createUser({
			portfolioId,
			displayName: form.displayName,
			email: form.email,
			password: form.password,
			role: form.role,
			ownerId: form.ownerId ? Number(form.ownerId) : null,
			tenantId: form.tenantId ? Number(form.tenantId) : null,
		}),
		onSuccess: () => {
			form = { displayName: '', email: '', password: '', role: 'Tenant', ownerId: '', tenantId: '' };
			queryClient.invalidateQueries({ queryKey: ['auth-users', portfolioId] });
		},
	}));

	const updateUserMutation = createMutation(() => ({
		mutationFn: ({ id, role, isActive }: { id: number; role?: string; isActive?: boolean }) =>
			auth.updateUser(id, { role, isActive }),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['auth-users', portfolioId] }),
	}));

	function submit() {
		if (!form.displayName || !form.email || !form.password) return;
		createUserMutation.mutate();
	}
</script>

<svelte:head>
	<title>User Access - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<h1 class="mb-1 text-2xl font-bold">User Access Control</h1>
	<p class="mb-5 text-sm text-text-secondary">Role-based user management for admin/manager operations.</p>

	<div class="mb-5 rounded-lg border border-border bg-surface p-4">
		<h2 class="mb-2 font-semibold">Create User</h2>
		<div class="grid gap-3 md:grid-cols-3">
			<input bind:value={form.displayName} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Display name" />
			<input bind:value={form.email} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Email" />
			<input bind:value={form.password} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Password" />
			<select bind:value={form.role} class="rounded border border-border bg-bg px-3 py-2 text-sm">
				<option>Admin</option><option>Manager</option><option>Agent</option><option>Owner</option><option>Tenant</option>
			</select>
			<select bind:value={form.ownerId} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option value="">No owner link</option>{#each ownersQuery.data || [] as owner}<option value={owner.id}>{owner.name}</option>{/each}</select>
			<select bind:value={form.tenantId} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option value="">No tenant link</option>{#each tenantsQuery.data || [] as tenant}<option value={tenant.id}>{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}</option>{/each}</select>
		</div>
		<div class="mt-3">
			<button onclick={submit} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createUserMutation.isPending}>Create User</button>
		</div>
	</div>

	<div class="rounded-lg border border-border bg-surface">
		<div class="overflow-x-auto">
			<table class="min-w-full text-sm">
				<thead class="border-b border-border bg-bg text-left text-xs uppercase text-text-tertiary">
					<tr>
						<th class="px-3 py-2">Name</th>
						<th class="px-3 py-2">Email</th>
						<th class="px-3 py-2">Role</th>
						<th class="px-3 py-2">Status</th>
						<th class="px-3 py-2">Actions</th>
					</tr>
				</thead>
				<tbody>
					{#each (usersQuery.data as any[]) || [] as user}
						<tr class="border-b border-border/70">
							<td class="px-3 py-2 font-medium">{user.displayName}</td>
							<td class="px-3 py-2">{user.email}</td>
							<td class="px-3 py-2">{user.role}</td>
							<td class="px-3 py-2">{user.isActive ? 'Active' : 'Disabled'}</td>
							<td class="px-3 py-2">
								<div class="flex gap-2">
									<button class="rounded border border-border px-2 py-1 text-xs" onclick={() => updateUserMutation.mutate({ id: user.id, isActive: !user.isActive })}>{user.isActive ? 'Disable' : 'Enable'}</button>
									{#if user.role !== 'Manager'}
										<button class="rounded border border-border px-2 py-1 text-xs" onclick={() => updateUserMutation.mutate({ id: user.id, role: user.role === 'Agent' ? 'Manager' : 'Agent' })}>Toggle Agent/Manager</button>
									{/if}
								</div>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
</div>
