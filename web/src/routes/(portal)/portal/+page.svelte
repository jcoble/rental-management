<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { auth } from '$lib/api/endpoints/auth';
	import { portal } from '$lib/api/endpoints/portal';
	import { payments as paymentsApi } from '$lib/api/endpoints/payments';
	import { ApiError } from '$lib/api/client';
	import { clearAuth, getCurrentUser, hasAnyRole, setCurrentUser } from '$lib/stores/auth.svelte';
	import { showSuccess, showError, showWarning } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';
	import type { Payment } from '$lib/types';

	/** Payment IDs currently being submitted to create-intent. */
	let payingIds = $state<Set<number>>(new Set());

	/** Per-payment inline notice shown when Stripe is not configured (503). */
	let unavailableIds = $state<Set<number>>(new Set());

	async function payNow(payment: Payment) {
		payingIds = new Set([...payingIds, payment.id]);
		unavailableIds = new Set([...unavailableIds].filter((id) => id !== payment.id));

		try {
			const result = await paymentsApi.createIntent(payment.id);
			// Stripe Elements is not wired yet — acknowledge the round-trip and stub.
			console.log('[pay-now] intent created, transactionId:', result.transactionId);
			showSuccess('Secure card payment is coming soon.');
		} catch (err) {
			if (err instanceof ApiError && err.status === 503) {
				unavailableIds = new Set([...unavailableIds, payment.id]);
				showWarning("Online payments aren't available yet — please pay by your usual method.");
			} else {
				showError(
					err instanceof ApiError && err.message
						? err.message
						: 'Something went wrong. Please try again.'
				);
			}
		} finally {
			payingIds = new Set([...payingIds].filter((id) => id !== payment.id));
		}
	}

	const queryClient = useQueryClient();

	const meQuery = createQuery(() => ({
		queryKey: ['auth-me'],
		queryFn: () => auth.me(),
		retry: false,
	}));

	$effect(() => {
		if (meQuery.data) setCurrentUser(meQuery.data);
		if (meQuery.isError) {
			// clearAuth() handles navigation (to /logout, which clears cookies
			// then redirects to /login) — no extra goto needed.
			clearAuth();
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
	<p class="mb-5 text-sm text-muted-foreground">Role-aware view for admin, manager, agent, owner, and tenant accounts.</p>

	{#if overviewQuery.data}
		{@const data = overviewQuery.data as any}
		<Card.Root class="mb-5 gap-0 py-0">
			<Card.Content class="p-4">
				<p class="text-sm text-muted-foreground">Signed in as</p>
				<p class="text-lg font-semibold">{data.user.displayName} <span class="text-sm font-normal text-muted-foreground">({data.role})</span></p>
			</Card.Content>
		</Card.Root>

		{#if data.role === 'Tenant'}
			<div class="mb-5 grid gap-4 lg:grid-cols-2">
				<Card.Root class="gap-0 py-0">
					<Card.Content class="p-4">
						<h2 class="mb-2 font-semibold">My Lease & Balance</h2>
						<p class="text-sm">Outstanding: ${data.ledger?.outstanding || 0}</p>
						<div class="mt-2 space-y-2 text-sm">
							{#each data.leases || [] as lease}
								<div class="rounded border border-border bg-background px-3 py-2">{lease.leaseNumber} · {lease.property} Unit {lease.unit} · {lease.status}</div>
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
				<Card.Root class="gap-0 py-0">
					<Card.Content class="p-4">
						<h2 class="mb-2 font-semibold">Submit Maintenance Request</h2>
						<div class="space-y-2">
							<Input bind:value={workOrderForm.title} placeholder="Issue title" />
							<textarea bind:value={workOrderForm.description} rows={3} class="w-full rounded border border-border bg-background px-3 py-2 text-sm" placeholder="Describe the issue"></textarea>
							<div class="grid grid-cols-2 gap-2">
								<Input bind:value={workOrderForm.category} />
								<Select.Root type="single" bind:value={workOrderForm.priority}>
									<Select.Trigger class="w-full">
										{workOrderForm.priority || 'Select priority'}
									</Select.Trigger>
									<Select.Content>
										<Select.Item value="Low" label="Low">Low</Select.Item>
										<Select.Item value="Normal" label="Normal">Normal</Select.Item>
										<Select.Item value="High" label="High">High</Select.Item>
										<Select.Item value="Emergency" label="Emergency">Emergency</Select.Item>
									</Select.Content>
								</Select.Root>
							</div>
							<Button onclick={submitTenantWorkOrder} disabled={createTenantWorkOrderMutation.isPending}>Submit Request</Button>
						</div>
					</Card.Content>
				</Card.Root>
			</div>

			<!-- Payments: show owed/scheduled rent with online Pay now buttons. -->
			{#if (data.ledger?.upcomingPayments || []).length > 0}
				<Card.Root class="mb-5 gap-0 py-0">
					<Card.Content class="p-4">
						<h2 class="mb-3 font-semibold">Upcoming & Owed Payments</h2>
						<div class="space-y-3">
							{#each data.ledger.upcomingPayments as payment (payment.id)}
								<div class="flex items-center justify-between rounded border border-border bg-background px-3 py-2 text-sm">
									<div>
										<p class="font-medium">{payment.type} — ${payment.amount}</p>
										<p class="text-xs text-muted-foreground">
											Due {new Date(payment.dueDate).toLocaleDateString()} · {payment.status}
											{#if payment.leaseNumber}· {payment.leaseNumber}{/if}
										</p>
										{#if unavailableIds.has(payment.id)}
											<p class="mt-1 text-xs text-amber-600">
												Online payments aren't available yet — please pay by your usual method.
											</p>
										{/if}
									</div>
									<Button
										size="sm"
										variant={unavailableIds.has(payment.id) ? 'outline' : 'default'}
										disabled={payingIds.has(payment.id)}
										onclick={() => payNow(payment)}
									>
										{payingIds.has(payment.id) ? 'Processing…' : 'Pay now'}
									</Button>
								</div>
							{/each}
						</div>
					</Card.Content>
				</Card.Root>
			{/if}
		{/if}

		{#if data.role === 'Owner'}
			<div class="mb-5 grid gap-4 md:grid-cols-3">
				<Card.Root class="gap-0 py-0"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Properties</p><p class="text-2xl font-bold">{data.portfolioSummary?.propertyCount || 0}</p></Card.Content></Card.Root>
				<Card.Root class="gap-0 py-0"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Collected</p><p class="text-2xl font-bold">${data.portfolioSummary?.collected || 0}</p></Card.Content></Card.Root>
				<Card.Root class="gap-0 py-0"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Net</p><p class="text-2xl font-bold">${data.portfolioSummary?.net || 0}</p></Card.Content></Card.Root>
			</div>
		{/if}

		{#if data.role === 'Agent'}
			<Card.Root class="mb-5 gap-0 py-0">
				<Card.Content class="p-4">
					<h2 class="mb-2 font-semibold">Agent Queue</h2>
					<div class="grid gap-3 lg:grid-cols-2">
						<div>
							<p class="mb-2 text-sm font-medium">Appointments</p>
							{#each data.assignments?.appointments || [] as appointment}
								<div class="mb-2 rounded border border-border bg-background px-3 py-2 text-sm">{appointment.title} · {appointment.status}</div>
							{/each}
						</div>
						<div>
							<p class="mb-2 text-sm font-medium">Open Work Orders</p>
							{#each data.assignments?.workOrders || [] as wo}
								<div class="mb-2 rounded border border-border bg-background px-3 py-2 text-sm">{wo.title} · {wo.priority}</div>
							{/each}
						</div>
					</div>
				</Card.Content>
			</Card.Root>
		{/if}

		{#if hasAnyRole('Admin', 'Manager')}
			<div class="mb-5 grid gap-4 md:grid-cols-3">
				<Card.Root class="gap-0 py-0"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Open Work Orders</p><p class="text-2xl font-bold">{data.operations?.openWorkOrders || 0}</p></Card.Content></Card.Root>
				<Card.Root class="gap-0 py-0"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Overdue Balance</p><p class="text-2xl font-bold">${data.operations?.overdueBalance || 0}</p></Card.Content></Card.Root>
				<Card.Root class="gap-0 py-0"><Card.Content class="p-4"><p class="text-xs text-muted-foreground">Portal Messages</p><p class="text-2xl font-bold">{data.operations?.pendingPortalMessages || 0}</p></Card.Content></Card.Root>
			</div>
		{/if}
	{/if}

	<div class="grid gap-4 lg:grid-cols-2">
		<Card.Root class="gap-0 py-0">
			<Card.Content class="p-4">
				<h2 class="mb-2 font-semibold">Send Message To Management</h2>
				<div class="space-y-2">
					<Input bind:value={messageForm.subject} placeholder="Subject" />
					<textarea bind:value={messageForm.body} rows={4} class="w-full rounded border border-border bg-background px-3 py-2 text-sm" placeholder="Message"></textarea>
					<Button onclick={submitMessage} disabled={createMessageMutation.isPending}>Send</Button>
				</div>
			</Card.Content>
		</Card.Root>
		<Card.Root class="gap-0 py-0">
			<Card.Content class="p-4">
				<h2 class="mb-2 font-semibold">Portal Messages</h2>
				<div class="max-h-[45vh] overflow-y-auto space-y-2">
					{#each messagesQuery.data || [] as message}
						<div class="rounded border border-border bg-background p-3 text-sm">
							<div class="flex items-center justify-between">
								<p class="font-medium">{message.subject}</p>
								<span>{message.status}</span>
							</div>
							<p class="text-xs text-muted-foreground">{message.author || 'Unknown'} · {new Date(message.createdAt).toLocaleString()}</p>
							<p class="mt-1">{message.body}</p>
							{#if message.reply}
								<p class="mt-2 rounded border border-border px-2 py-1 text-xs text-muted-foreground">Reply: {message.reply}</p>
							{/if}
							{#if hasAnyRole('Admin', 'Manager', 'Agent') && message.status !== 'Resolved' && message.status !== 'Closed'}
								<div class="mt-2 flex gap-2">
									<Button variant="outline" size="sm" onclick={() => updateMessageMutation.mutate({ id: message.id, status: 'InProgress' })}>In Progress</Button>
									<Button variant="outline" size="sm" onclick={() => updateMessageMutation.mutate({ id: message.id, status: 'Resolved' })}>Resolve</Button>
								</div>
							{/if}
						</div>
					{/each}
				</div>
			</Card.Content>
		</Card.Root>
	</div>
</div>
