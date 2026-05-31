<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { auth } from '$lib/api/endpoints/auth';
	import { owners } from '$lib/api/endpoints/owners';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { clearAuth, hasAnyRole, setCurrentUser } from '$lib/stores/auth.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';

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
			// clearAuth() handles navigation (to /logout, which clears cookies
			// then redirects to /login) — no extra goto needed.
			clearAuth();
		}
	});

	const usersQuery = createQuery(() => ({
		queryKey: ['auth-users', portfolioId],
		enabled: !!meQuery.data && hasAnyRole('Admin', 'Manager'),
		queryFn: () => auth.listUsers(portfolioId),
		retry: false,
	}));

	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId],
		enabled: !!meQuery.data && hasAnyRole('Admin', 'Manager'),
		queryFn: () => owners.list(portfolioId),
		retry: false,
	}));

	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId],
		enabled: !!meQuery.data && hasAnyRole('Admin', 'Manager'),
		queryFn: () => tenants.list(portfolioId),
		retry: false,
	}));

	let form = $state({ displayName: '', email: '', password: '', role: 'Tenant', ownerId: '', tenantId: '' });

	const selectedOwnerLabel = $derived(
		form.ownerId
			? (ownersQuery.data || []).find((o: any) => String(o.id) === form.ownerId)?.name ?? form.ownerId
			: null
	);

	const selectedTenantLabel = $derived(
		form.tenantId
			? (tenantsQuery.data || []).find((t: any) => String(t.id) === form.tenantId)?.fullName
				?? (() => {
					const t = (tenantsQuery.data || []).find((t: any) => String(t.id) === form.tenantId);
					return t ? `${t.firstName} ${t.lastName}` : form.tenantId;
				})()
			: null
	);

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
	{#if meQuery.isPending}
		<div class="flex h-40 items-center justify-center text-muted-foreground">Checking access...</div>
	{:else if hasAnyRole('Admin', 'Manager')}
		<h1 class="mb-1 text-2xl font-bold">User Access Control</h1>
		<p class="mb-5 text-sm text-muted-foreground">Role-based user management for admin/manager operations.</p>

		<Card.Root class="mb-5 gap-0 py-0">
			<Card.Content class="p-4">
				<h2 class="mb-2 font-semibold">Create User</h2>
				<div class="grid gap-3 md:grid-cols-3">
					<Input bind:value={form.displayName} placeholder="Display name" />
					<Input bind:value={form.email} placeholder="Email" />
					<Input bind:value={form.password} placeholder="Password" />
					<Select.Root type="single" bind:value={form.role}>
						<Select.Trigger class="w-full">
							{form.role || 'Select role'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="Admin" label="Admin">Admin</Select.Item>
							<Select.Item value="Manager" label="Manager">Manager</Select.Item>
							<Select.Item value="Agent" label="Agent">Agent</Select.Item>
							<Select.Item value="Owner" label="Owner">Owner</Select.Item>
							<Select.Item value="Tenant" label="Tenant">Tenant</Select.Item>
						</Select.Content>
					</Select.Root>
					<Select.Root type="single" bind:value={form.ownerId}>
						<Select.Trigger class="w-full">
							{selectedOwnerLabel ?? 'No owner link'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="No owner link">No owner link</Select.Item>
							{#each ownersQuery.data || [] as owner}
								<Select.Item value={String(owner.id)} label={owner.name}>{owner.name}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
					<Select.Root type="single" bind:value={form.tenantId}>
						<Select.Trigger class="w-full">
							{selectedTenantLabel ?? 'No tenant link'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="No tenant link">No tenant link</Select.Item>
							{#each tenantsQuery.data || [] as tenant}
								<Select.Item value={String(tenant.id)} label={tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}>{tenant.fullName || `${tenant.firstName} ${tenant.lastName}`}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
				<div class="mt-3">
					<Button onclick={submit} disabled={createUserMutation.isPending}>Create User</Button>
				</div>
			</Card.Content>
		</Card.Root>

		<Card.Root class="gap-0 py-0">
			<Card.Content class="p-0">
				<div class="overflow-x-auto">
					<table class="min-w-full text-sm">
						<thead class="border-b border-border bg-background text-left text-xs uppercase text-muted-foreground">
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
											<Button variant="outline" size="sm" onclick={() => updateUserMutation.mutate({ id: user.id, isActive: !user.isActive })}>{user.isActive ? 'Disable' : 'Enable'}</Button>
											{#if user.role !== 'Manager'}
												<Button variant="outline" size="sm" onclick={() => updateUserMutation.mutate({ id: user.id, role: user.role === 'Agent' ? 'Manager' : 'Agent' })}>Toggle Agent/Manager</Button>
											{/if}
										</div>
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			</Card.Content>
		</Card.Root>
	{/if}
</div>
