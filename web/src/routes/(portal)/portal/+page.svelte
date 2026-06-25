<script lang="ts">
	import { goto } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { portal } from '$lib/api/endpoints/portal';
	import { notifications } from '$lib/api/endpoints/notifications';
	import type { NotificationItem } from '$lib/api/types/notification';
	import type { Appointment } from '$lib/types';
	import { getCurrentUser } from '$lib/stores/auth.svelte';
	import { notificationStore } from '$lib/stores/notifications.svelte';
	import { portalActionUrl } from '$lib/utils/portalLinks';
	import { paymentTypeLabel } from '$lib/utils/payment-labels';
	import { formatDateOnly, daysFromTodayUtc, isPastDueUtc } from '$lib/utils/date';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import * as Select from '$lib/components/ui/select';
	import {
		BellRing,
		CalendarClock,
		Clock,
		CreditCard,
		FileText,
		MapPin,
		MessageSquare,
		Wrench,
		AlertTriangle,
		ClipboardList
	} from '@lucide/svelte';

	const queryClient = useQueryClient();
	const currentUser = $derived(getCurrentUser());

	const leasesQuery = createQuery(() => ({
		queryKey: ['portal-leases'],
		enabled: !!currentUser,
		queryFn: () => portal.leases()
	}));

	const balanceQuery = createQuery(() => ({
		queryKey: ['portal-balance'],
		enabled: !!currentUser,
		queryFn: () => portal.balance()
	}));

	const paymentsQuery = createQuery(() => ({
		queryKey: ['portal-payments'],
		enabled: !!currentUser,
		queryFn: () => portal.payments()
	}));

	const appointmentsQuery = createQuery(() => ({
		queryKey: ['portal-appointments'],
		enabled: !!currentUser,
		queryFn: () => portal.appointments()
	}));

	const workOrdersQuery = createQuery(() => ({
		queryKey: ['portal-work-orders'],
		enabled: !!currentUser,
		queryFn: () => portal.workOrders()
	}));

	const conversationsQuery = createQuery(() => ({
		queryKey: ['portal-conversations'],
		enabled: !!currentUser,
		queryFn: () => portal.conversations.list()
	}));

	const notificationsQuery = createQuery(() => ({
		queryKey: ['notifications', 'tenant-dashboard'],
		enabled: !!currentUser,
		queryFn: () => notifications.list({ take: 10 })
	}));

	const unreadNotificationsQuery = createQuery(() => ({
		queryKey: ['notifications-unread-count'],
		enabled: !!currentUser,
		queryFn: () => notifications.unreadCount()
	}));

	const leases = $derived((leasesQuery.data ?? []) as any[]);
	const payments = $derived((paymentsQuery.data ?? []) as any[]);
	const appointments = $derived((appointmentsQuery.data ?? []) as Appointment[]);
	const workOrders = $derived((workOrdersQuery.data ?? []) as any[]);
	const conversations = $derived(conversationsQuery.data ?? []);
	const notificationItems = $derived(notificationsQuery.data ?? []);
	const balance = $derived((balanceQuery.data ?? {}) as any);

	const unreadMessages = $derived(conversations.reduce((sum, c) => sum + (c.unreadCount || 0), 0));
	const unreadNotifications = $derived(unreadNotificationsQuery.data?.count ?? 0);
	const openWorkOrders = $derived(
		workOrders.filter((w) => !['Completed', 'Cancelled', 'Archived'].includes(String(w.status)))
	);
	const overduePayments = $derived(
		payments.filter(
			(p) =>
				!['Paid', 'Waived', 'Refunded'].includes(String(p.status)) && isPastDueUtc(p.dueDate)
		)
	);
	const upcomingPayments = $derived(
		payments
			.filter((p) => !['Paid', 'Waived', 'Refunded'].includes(String(p.status)))
			.sort((a, b) => new Date(a.dueDate).getTime() - new Date(b.dueDate).getTime())
	);
	const nextPayment = $derived(
		upcomingPayments.find((p) => (daysFromTodayUtc(p.dueDate) ?? -1) >= 0) ?? null
	);
	const upcomingAppointments = $derived(appointments);
	const nextRentDays = $derived(nextPayment ? daysUntil(nextPayment.dueDate) : null);
	const activeLease = $derived(leases.find((l) => l.status === 'Active') ?? leases[0] ?? null);

	const APPOINTMENT_TYPE_LABELS: Record<string, string> = {
		Showing: 'Showing',
		MoveIn: 'Move-in',
		MoveOut: 'Move-out',
		Inspection: 'Inspection',
		MaintenanceVisit: 'Maintenance',
		OwnerMeeting: 'Owner meeting'
	};

	let workOrderForm = $state({
		title: '',
		description: '',
		category: 'Resident Request',
		priority: 'Normal'
	});

	const createWorkOrderMutation = createMutation(() => ({
		mutationFn: () => portal.createTenantWorkOrder(workOrderForm),
		onSuccess: () => {
			workOrderForm = {
				title: '',
				description: '',
				category: 'Resident Request',
				priority: 'Normal'
			};
			queryClient.invalidateQueries({ queryKey: ['portal-work-orders'] });
			showSuccess('Maintenance request submitted.');
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function submitWorkOrder() {
		if (!workOrderForm.title.trim() || !workOrderForm.description.trim()) return;
		createWorkOrderMutation.mutate();
	}

	async function openDashboardNotification(item: NotificationItem) {
		if (!item.isRead) {
			await notificationStore.markAsRead(item.id);
			await notificationStore.refresh();
			await Promise.all([
				queryClient.invalidateQueries({ queryKey: ['notifications', 'tenant-dashboard'] }),
				queryClient.invalidateQueries({ queryKey: ['notifications-unread-count'] })
			]);
		}
		await goto(portalActionUrl(item.actionUrl), { invalidateAll: true });
	}

	function money(value: number | string | null | undefined) {
		const amount = Number(value ?? 0);
		return amount.toLocaleString(undefined, { style: 'currency', currency: 'USD' });
	}

	function date(value: string | null | undefined) {
		if (!value) return '-';
		// dueDate / endDate are UTC-midnight calendar dates — format in UTC to avoid the off-by-one shift.
		return formatDateOnly(value);
	}

	function daysUntil(value: string) {
		// Whole-day count in UTC so a date due "today" reads 0, not -1, in behind-UTC zones.
		return daysFromTodayUtc(value) ?? 0;
	}

	function appointmentTypeLabel(type: string | null | undefined): string {
		return type ? (APPOINTMENT_TYPE_LABELS[type] ?? formatStatusLabel(type)) : 'Appointment';
	}

	function dateTime(value: string | null | undefined): string {
		if (!value) return '';
		const d = new Date(value);
		if (Number.isNaN(d.getTime())) return value;
		return d.toLocaleString(undefined, {
			weekday: 'short',
			month: 'short',
			day: 'numeric',
			hour: 'numeric',
			minute: '2-digit'
		});
	}

	function timeOnly(value: string | null | undefined): string {
		if (!value) return '';
		const d = new Date(value);
		if (Number.isNaN(d.getTime())) return '';
		return d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
	}

	function appointmentWindow(appointment: Appointment): string {
		const start = dateTime(appointment.scheduledStart);
		const end = timeOnly(appointment.scheduledEnd);
		return end ? `${start} - ${end}` : start;
	}

	function locationLabel(appointment: Appointment): string {
		const parts = [
			appointment.propertyName,
			appointment.unitNumber ? `Unit ${appointment.unitNumber}` : null
		].filter(Boolean);
		return parts.length > 0 ? parts.join(' ') : 'Location to be confirmed';
	}
</script>

<svelte:head>
	<title>Tenant Dashboard - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto bg-background" data-testid="tenant-dashboard">
	<div class="mx-auto max-w-6xl px-5 py-6 md:px-8">
		<header class="mb-6">
			<p class="text-sm text-muted-foreground">Tenant dashboard</p>
			<h1 class="text-2xl font-semibold tracking-normal text-foreground">
				{currentUser?.displayName || 'My home'}
			</h1>
		</header>

		<section id="notifications" class="mb-5 rounded-lg border border-border bg-card p-4">
			<div class="mb-3 flex items-center justify-between gap-3">
				<div class="flex items-center gap-2">
					<BellRing class="h-5 w-5 text-primary" />
					<h2 class="font-semibold">Notifications</h2>
				</div>
				<span class="rounded-full bg-primary/10 px-2.5 py-1 text-xs font-medium text-primary">
					{unreadNotifications} unread
				</span>
			</div>
			{#if notificationItems.length === 0}
				<p class="text-sm text-muted-foreground">No notifications yet.</p>
			{:else}
				<div class="divide-y divide-border">
					{#each notificationItems.slice(0, 3) as item}
						<button
							type="button"
							class="block w-full py-3 text-left first:pt-0 last:pb-0"
							onclick={() => openDashboardNotification(item)}
						>
							<p class="text-sm font-medium text-foreground">{item.title}</p>
							<p class="mt-1 line-clamp-2 text-sm text-muted-foreground">{item.message}</p>
						</button>
					{/each}
				</div>
			{/if}
		</section>

		<section class="mb-5 grid gap-4 md:grid-cols-2 xl:grid-cols-4">
			<a href="/portal/messages" class="rounded-lg border border-border bg-card p-4 transition-colors hover:bg-muted/40">
				<div class="mb-3 flex items-center gap-2 text-primary"><MessageSquare class="h-4 w-4" /><span class="text-sm font-medium">Messages</span></div>
				<p class="text-3xl font-semibold">{unreadMessages}</p>
				<p class="mt-1 text-sm text-muted-foreground">Unread from management</p>
			</a>
			<div class="rounded-lg border border-border bg-card p-4">
				<div class="mb-3 flex items-center gap-2 text-[var(--warning)]"><AlertTriangle class="h-4 w-4" /><span class="text-sm font-medium">Overdue</span></div>
				<p class="text-3xl font-semibold">{money(balance.overdue ?? 0)}</p>
				<p class="mt-1 text-sm text-muted-foreground">{overduePayments.length} overdue item{overduePayments.length === 1 ? '' : 's'}</p>
			</div>
			<div class="rounded-lg border border-border bg-card p-4">
				<div class="mb-3 flex items-center gap-2 text-[var(--success)]"><CreditCard class="h-4 w-4" /><span class="text-sm font-medium">Next Rent</span></div>
				<p class="text-3xl font-semibold">{nextRentDays === null ? '-' : nextRentDays}</p>
				<p class="mt-1 text-sm text-muted-foreground">
					{#if nextPayment}
						day{nextRentDays === 1 ? '' : 's'} until {money(nextPayment.amount)} is due
					{:else}
						No future rent scheduled
					{/if}
				</p>
			</div>
			<a href="/portal/maintenance" class="rounded-lg border border-border bg-card p-4 transition-colors hover:bg-muted/40">
				<div class="mb-3 flex items-center gap-2 text-[var(--m3c-error)]"><Wrench class="h-4 w-4" /><span class="text-sm font-medium">Maintenance</span></div>
				<p class="text-3xl font-semibold">{openWorkOrders.length}</p>
				<p class="mt-1 text-sm text-muted-foreground">Open requests</p>
			</a>
		</section>

		<div class="grid gap-5 xl:grid-cols-[1.2fr_0.8fr]">
			<section id="payments" class="rounded-lg border border-border bg-card p-4">
				<div class="mb-4 flex items-center gap-2">
					<CreditCard class="h-5 w-5 text-primary" />
					<h2 class="font-semibold">Payments</h2>
				</div>
				{#if upcomingPayments.length === 0}
					<p class="text-sm text-muted-foreground">No outstanding payments.</p>
				{:else}
					<div class="space-y-2">
						{#each upcomingPayments.slice(0, 5) as payment}
							<div class="flex items-center justify-between gap-3 rounded-md border border-border px-3 py-2">
								<div>
									<p class="text-sm font-medium">{paymentTypeLabel(payment.paymentType ?? payment.type)} · {money(payment.amount)}</p>
									<p class="text-xs text-muted-foreground">Due {date(payment.dueDate)} · {payment.status}</p>
								</div>
								<span class="text-xs text-muted-foreground">
									{daysUntil(payment.dueDate) < 0 ? `${Math.abs(daysUntil(payment.dueDate))} days late` : `${daysUntil(payment.dueDate)} days`}
								</span>
							</div>
						{/each}
					</div>
				{/if}
			</section>

			<section id="lease" class="rounded-lg border border-border bg-card p-4">
				<div class="mb-4 flex items-center gap-2">
					<FileText class="h-5 w-5 text-primary" />
					<h2 class="font-semibold">Lease</h2>
				</div>
				{#if activeLease}
					<div class="space-y-3 text-sm">
						<p class="font-medium">{activeLease.leaseNumber}</p>
						<p class="text-muted-foreground">{activeLease.propertyName ?? activeLease.property} {activeLease.unitNumber ? `Unit ${activeLease.unitNumber}` : ''}</p>
						<div class="grid grid-cols-2 gap-3">
							<div><p class="text-xs text-muted-foreground">Rent</p><p>{money(activeLease.monthlyRent)}</p></div>
							<div><p class="text-xs text-muted-foreground">Ends</p><p>{date(activeLease.endDate)}</p></div>
						</div>
					</div>
				{:else}
					<p class="text-sm text-muted-foreground">No lease is linked to this account.</p>
				{/if}
			</section>

			<section id="maintenance" class="rounded-lg border border-border bg-card p-4">
				<div class="mb-4 flex items-center gap-2">
					<Wrench class="h-5 w-5 text-primary" />
					<h2 class="font-semibold">Maintenance Requests</h2>
				</div>
				<div class="mb-5 space-y-2">
					{#if openWorkOrders.length === 0}
						<p class="text-sm text-muted-foreground">No open requests.</p>
					{:else}
						{#each openWorkOrders.slice(0, 5) as order}
							<div class="rounded-md border border-border px-3 py-2">
								<p class="text-sm font-medium">{order.title}</p>
								<p class="text-xs text-muted-foreground">{order.status} · {order.priority}</p>
							</div>
						{/each}
					{/if}
				</div>
				<form class="space-y-3" onsubmit={(e) => { e.preventDefault(); submitWorkOrder(); }}>
					<Input bind:value={workOrderForm.title} placeholder="Issue title" />
					<textarea bind:value={workOrderForm.description} rows={4} class="w-full rounded-md border border-border bg-background px-3 py-2 text-sm" placeholder="Describe the issue"></textarea>
					<div class="grid gap-3 sm:grid-cols-2">
						<Input bind:value={workOrderForm.category} placeholder="Category" />
						<Select.Root type="single" bind:value={workOrderForm.priority}>
							<Select.Trigger class="w-full">{workOrderForm.priority}</Select.Trigger>
							<Select.Content>
								<Select.Item value="Low" label="Low">Low</Select.Item>
								<Select.Item value="Normal" label="Normal">Normal</Select.Item>
								<Select.Item value="High" label="High">High</Select.Item>
								<Select.Item value="Emergency" label="Emergency">Emergency</Select.Item>
							</Select.Content>
						</Select.Root>
					</div>
					<Button type="submit" disabled={createWorkOrderMutation.isPending}>
						{createWorkOrderMutation.isPending ? 'Submitting...' : 'Submit Request'}
					</Button>
				</form>
			</section>

			<section id="appointments" class="rounded-lg border border-border bg-card p-4">
				<div class="mb-4 flex items-center gap-2">
					<CalendarClock class="h-5 w-5 text-primary" />
					<h2 class="font-semibold">Appointments</h2>
				</div>
				{#if appointmentsQuery.isLoading}
					<p class="text-sm text-muted-foreground">Loading appointments...</p>
				{:else if appointmentsQuery.isError}
					<p class="text-sm text-destructive">Couldn't load appointments.</p>
				{:else if upcomingAppointments.length === 0}
					<p class="text-sm text-muted-foreground">No appointments scheduled.</p>
				{:else}
					<div class="space-y-2">
						{#each upcomingAppointments.slice(0, 3) as appointment (appointment.id)}
							<a
								href="/portal/appointments"
								class="block rounded-md border border-border px-3 py-2 transition-colors hover:bg-muted/40"
								data-testid="tenant-dashboard-appointment"
							>
								<div class="flex flex-wrap items-start justify-between gap-2">
									<div class="min-w-0">
										<p class="text-sm font-medium text-foreground">{appointment.title}</p>
										<p class="mt-1 flex items-center gap-1.5 text-xs text-muted-foreground">
											<Clock class="h-3.5 w-3.5 shrink-0" />
											<span>{appointmentWindow(appointment)}</span>
										</p>
										<p class="mt-1 flex items-center gap-1.5 text-xs text-muted-foreground">
											<MapPin class="h-3.5 w-3.5 shrink-0" />
											<span>{locationLabel(appointment)}</span>
										</p>
									</div>
									<div class="flex shrink-0 flex-wrap items-center gap-1.5">
										<StatusBadge status={appointmentTypeLabel(appointment.type)} />
										<StatusBadge status={formatStatusLabel(appointment.status)} />
									</div>
								</div>
							</a>
						{/each}
					</div>
				{/if}
			</section>

			<section id="requests" class="rounded-lg border border-border bg-card p-4 xl:col-span-2">
				<div class="mb-4 flex items-center gap-2">
					<ClipboardList class="h-5 w-5 text-primary" />
					<h2 class="font-semibold">Latest Messages</h2>
				</div>
				{#if conversations.length === 0}
					<p class="text-sm text-muted-foreground">No conversations yet.</p>
				{:else}
					<div class="grid gap-2 md:grid-cols-2">
						{#each conversations.slice(0, 4) as conversation}
							<a href={`/portal/messages?conversation=${conversation.id}`} class="rounded-md border border-border px-3 py-2 hover:bg-muted/40">
								<p class="text-sm font-medium">{conversation.subject}</p>
								<p class="mt-1 line-clamp-2 text-xs text-muted-foreground">{conversation.lastMessagePreview}</p>
							</a>
						{/each}
					</div>
				{/if}
			</section>
		</div>
	</div>
</div>
