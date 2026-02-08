<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { auth } from '$lib/api/endpoints/auth';
	import { portal } from '$lib/api/endpoints/portal';
	import { clearAuth, getCurrentUser, hasAnyRole, setCurrentUser } from '$lib/stores/auth.svelte';

	const queryClient = useQueryClient();

	const meQuery = createQuery(() => ({
		queryKey: ['auth-me'],
		queryFn: () => auth.me(),
	}));

	$effect(() => {
		if (meQuery.data) setCurrentUser(meQuery.data);
		if (meQuery.isError) {
			clearAuth();
			goto('/login');
		}
	});

	const overviewQuery = createQuery(() => ({
		queryKey: ['portal-overview'],
		enabled: !!getCurrentUser(),
		queryFn: () => portal.overview(),
	}));

	const messagesQuery = createQuery(() => ({
		queryKey: ['portal-messages'],
		enabled: !!getCurrentUser(),
		queryFn: () => portal.messages(),
	}));

	let messageForm = $state({ subject: '', body: '' });
	let workOrderForm = $state({ title: '', description: '', category: 'Resident Request', priority: 'Normal' });

	const createMessageMutation = createMutation(() => ({
		mutationFn: () => portal.createMessage(messageForm),
		onSuccess: () => {
			messageForm = { subject: '', body: '' };
			queryClient.invalidateQueries({ queryKey: ['portal-messages'] });
		},
	}));

	const createTenantWorkOrderMutation = createMutation(() => ({
		mutationFn: () => portal.createTenantWorkOrder(workOrderForm),
		onSuccess: () => {
			workOrderForm = { title: '', description: '', category: 'Resident Request', priority: 'Normal' };
			queryClient.invalidateQueries({ queryKey: ['portal-overview'] });
		},
	}));

	const updateMessageMutation = createMutation(() => ({
		mutationFn: ({ id, status, reply }: { id: number; status?: string; reply?: string }) =>
			portal.updateMessage(id, { status, reply }),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['portal-messages'] }),
	}));

	function submitMessage() {
		if (!messageForm.subject || !messageForm.body) return;
		createMessageMutation.mutate();
	}

	function submitTenantWorkOrder() {
		if (!workOrderForm.title || !workOrderForm.description) return;
		createTenantWorkOrderMutation.mutate();
	}
</script>

<svelte:head>
	<title>Portal - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<h1 class="mb-1 text-2xl font-bold">Role Portal</h1>
	<p class="mb-5 text-sm text-text-secondary">Role-aware view for admin, manager, agent, owner, and tenant accounts.</p>

	{#if overviewQuery.data}
		{@const data = overviewQuery.data as any}
		<div class="mb-5 rounded-lg border border-border bg-surface p-4">
			<p class="text-sm text-text-secondary">Signed in as</p>
			<p class="text-lg font-semibold">{data.user.displayName} <span class="text-sm font-normal text-text-secondary">({data.role})</span></p>
		</div>

		{#if data.role === 'Tenant'}
			<div class="mb-5 grid gap-4 lg:grid-cols-2">
				<div class="rounded-lg border border-border bg-surface p-4">
					<h2 class="mb-2 font-semibold">My Lease & Balance</h2>
					<p class="text-sm">Outstanding: ${data.ledger?.outstanding || 0}</p>
					<div class="mt-2 space-y-2 text-sm">
						{#each data.leases || [] as lease}
							<div class="rounded border border-border bg-bg px-3 py-2">{lease.leaseNumber} · {lease.property} Unit {lease.unit} · {lease.status}</div>
						{/each}
					</div>
				</div>
				<div class="rounded-lg border border-border bg-surface p-4">
					<h2 class="mb-2 font-semibold">Submit Maintenance Request</h2>
					<div class="space-y-2">
						<input bind:value={workOrderForm.title} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Issue title" />
						<textarea bind:value={workOrderForm.description} rows={3} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Describe the issue"></textarea>
						<div class="grid grid-cols-2 gap-2">
							<input bind:value={workOrderForm.category} class="rounded border border-border bg-bg px-3 py-2 text-sm" />
							<select bind:value={workOrderForm.priority} class="rounded border border-border bg-bg px-3 py-2 text-sm"><option>Low</option><option>Normal</option><option>High</option><option>Emergency</option></select>
						</div>
						<button onclick={submitTenantWorkOrder} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createTenantWorkOrderMutation.isPending}>Submit Request</button>
					</div>
				</div>
			</div>
		{/if}

		{#if data.role === 'Owner'}
			<div class="mb-5 grid gap-4 md:grid-cols-3">
				<div class="rounded-lg border border-border bg-surface p-4"><p class="text-xs text-text-secondary">Properties</p><p class="text-2xl font-bold">{data.portfolioSummary?.propertyCount || 0}</p></div>
				<div class="rounded-lg border border-border bg-surface p-4"><p class="text-xs text-text-secondary">Collected</p><p class="text-2xl font-bold">${data.portfolioSummary?.collected || 0}</p></div>
				<div class="rounded-lg border border-border bg-surface p-4"><p class="text-xs text-text-secondary">Net</p><p class="text-2xl font-bold">${data.portfolioSummary?.net || 0}</p></div>
			</div>
		{/if}

		{#if data.role === 'Agent'}
			<div class="mb-5 rounded-lg border border-border bg-surface p-4">
				<h2 class="mb-2 font-semibold">Agent Queue</h2>
				<div class="grid gap-3 lg:grid-cols-2">
					<div>
						<p class="mb-2 text-sm font-medium">Appointments</p>
						{#each data.assignments?.appointments || [] as appointment}
							<div class="mb-2 rounded border border-border bg-bg px-3 py-2 text-sm">{appointment.title} · {appointment.status}</div>
						{/each}
					</div>
					<div>
						<p class="mb-2 text-sm font-medium">Open Work Orders</p>
						{#each data.assignments?.workOrders || [] as wo}
							<div class="mb-2 rounded border border-border bg-bg px-3 py-2 text-sm">{wo.title} · {wo.priority}</div>
						{/each}
					</div>
				</div>
			</div>
		{/if}

		{#if hasAnyRole('Admin', 'Manager')}
			<div class="mb-5 grid gap-4 md:grid-cols-3">
				<div class="rounded-lg border border-border bg-surface p-4"><p class="text-xs text-text-secondary">Open Work Orders</p><p class="text-2xl font-bold">{data.operations?.openWorkOrders || 0}</p></div>
				<div class="rounded-lg border border-border bg-surface p-4"><p class="text-xs text-text-secondary">Overdue Balance</p><p class="text-2xl font-bold">${data.operations?.overdueBalance || 0}</p></div>
				<div class="rounded-lg border border-border bg-surface p-4"><p class="text-xs text-text-secondary">Portal Messages</p><p class="text-2xl font-bold">{data.operations?.pendingPortalMessages || 0}</p></div>
			</div>
		{/if}
	{/if}

	<div class="grid gap-4 lg:grid-cols-2">
		<div class="rounded-lg border border-border bg-surface p-4">
			<h2 class="mb-2 font-semibold">Send Message To Management</h2>
			<div class="space-y-2">
				<input bind:value={messageForm.subject} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Subject" />
				<textarea bind:value={messageForm.body} rows={4} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Message"></textarea>
				<button onclick={submitMessage} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={createMessageMutation.isPending}>Send</button>
			</div>
		</div>
		<div class="rounded-lg border border-border bg-surface p-4">
			<h2 class="mb-2 font-semibold">Portal Messages</h2>
			<div class="max-h-[45vh] overflow-y-auto space-y-2">
				{#each messagesQuery.data || [] as message}
					<div class="rounded border border-border bg-bg p-3 text-sm">
						<div class="flex items-center justify-between">
							<p class="font-medium">{message.subject}</p>
							<span>{message.status}</span>
						</div>
						<p class="text-xs text-text-secondary">{message.author || 'Unknown'} · {new Date(message.createdAt).toLocaleString()}</p>
						<p class="mt-1">{message.body}</p>
						{#if message.reply}
							<p class="mt-2 rounded border border-border px-2 py-1 text-xs text-text-secondary">Reply: {message.reply}</p>
						{/if}
						{#if hasAnyRole('Admin', 'Manager', 'Agent') && message.status !== 'Resolved' && message.status !== 'Closed'}
							<div class="mt-2 flex gap-2">
								<button class="rounded border border-border px-2 py-1 text-xs" onclick={() => updateMessageMutation.mutate({ id: message.id, status: 'InProgress' })}>In Progress</button>
								<button class="rounded border border-border px-2 py-1 text-xs" onclick={() => updateMessageMutation.mutate({ id: message.id, status: 'Resolved' })}>Resolve</button>
							</div>
						{/if}
					</div>
				{/each}
			</div>
		</div>
	</div>
</div>
