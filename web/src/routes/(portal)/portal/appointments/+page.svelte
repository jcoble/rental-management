<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { portal } from '$lib/api/endpoints/portal';
	import type { Appointment } from '$lib/types';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { CalendarClock, Clock, MapPin } from '@lucide/svelte';

	const appointmentsQuery = createQuery(() => ({
		queryKey: ['portal-appointments'],
		queryFn: () => portal.appointments()
	}));

	const APPOINTMENT_TYPE_LABELS: Record<string, string> = {
		Showing: 'Showing',
		MoveIn: 'Move-in',
		MoveOut: 'Move-out',
		Inspection: 'Inspection',
		MaintenanceVisit: 'Maintenance',
		OwnerMeeting: 'Owner meeting'
	};

	function typeLabel(type: string | null | undefined): string {
		return type ? (APPOINTMENT_TYPE_LABELS[type] ?? formatStatusLabel(type)) : 'Appointment';
	}

	function dateTime(value: string | null | undefined): string {
		if (!value) return '';
		const d = new Date(value);
		if (isNaN(d.getTime())) return value;
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
		if (isNaN(d.getTime())) return '';
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

<svelte:head><title>Appointments - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-4 sm:p-6" data-testid="portal-appointments-page">
	<div class="mb-5 flex items-center gap-2">
		<CalendarClock class="h-5 w-5 text-primary" />
		<h1 class="text-2xl font-semibold">Appointments</h1>
	</div>

	<div class="space-y-3">
		{#if appointmentsQuery.isLoading}
			<div class="rounded-lg border border-border bg-card p-4 text-sm text-muted-foreground" data-testid="portal-appointments-loading">
				Loading appointments...
			</div>
		{:else if appointmentsQuery.isError}
			<div class="rounded-lg border border-destructive/40 bg-card p-4 text-sm text-destructive" data-testid="portal-appointments-error">
				Couldn't load appointments.
			</div>
		{:else}
			{#each appointmentsQuery.data ?? [] as appointment (appointment.id)}
				<article class="rounded-lg border border-border bg-card p-4" data-testid="portal-appointment-row">
					<div class="flex flex-wrap items-start justify-between gap-3">
						<div class="min-w-0">
							<p class="font-medium">{appointment.title}</p>
							<p class="mt-1 flex items-center gap-1.5 text-sm text-muted-foreground">
								<Clock class="h-3.5 w-3.5 shrink-0" />
								<span>{appointmentWindow(appointment)}</span>
							</p>
							<p class="mt-1 flex items-center gap-1.5 text-sm text-muted-foreground">
								<MapPin class="h-3.5 w-3.5 shrink-0" />
								<span>{locationLabel(appointment)}</span>
							</p>
						</div>
						<div class="flex shrink-0 flex-wrap items-center gap-2">
							<StatusBadge status={typeLabel(appointment.type)} />
							<StatusBadge status={formatStatusLabel(appointment.status)} />
						</div>
					</div>
					{#if appointment.notes}
						<p class="mt-3 whitespace-pre-line text-sm text-muted-foreground">{appointment.notes}</p>
					{/if}
				</article>
			{:else}
				<div class="rounded-lg border border-border bg-card p-4 text-sm text-muted-foreground" data-testid="portal-appointments-empty">
					No appointments scheduled.
				</div>
			{/each}
		{/if}
	</div>
</div>
