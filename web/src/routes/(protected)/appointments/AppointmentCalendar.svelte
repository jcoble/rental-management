<!--
  AppointmentCalendar — month / week / list (agenda) calendar of appointments,
  built on @event-calendar/core (v5, Svelte 5 native).

  Renders in the landlord's LOCAL timezone (the lib works in JS Date / local time).
  Events are colored by appointment Type via the shared TYPE_COLORS map.

  Interactions (delegated to the parent so the existing mutations stay in one place):
    - click an event           -> onSelectAppointment(appointment)
    - click an empty day/slot   -> onCreateAt(localWallClockIso)
    - drag-reschedule an event  -> onReschedule(appointment, newStartUtc, newEndUtc)
-->
<script lang="ts">
	import { Calendar, DayGrid, TimeGrid, List, Interaction } from '@event-calendar/core';
	import '@event-calendar/core/index.css';
	import type { Appointment } from '$lib/types';
	import {
		colorForType,
		dateToLocalWallClock,
		DEFAULT_DURATION_MS
	} from './calendar-utils';

	let {
		appointments = [],
		initialDate,
		onSelectAppointment,
		onCreateAt,
		onReschedule
	}: {
		appointments?: Appointment[];
		initialDate?: Date;
		onSelectAppointment?: (a: Appointment) => void;
		onCreateAt?: (localWallClockIso: string) => void;
		onReschedule?: (a: Appointment, newStartUtcIso: string, newEndUtcIso: string) => void;
	} = $props();

	// Honour reduced-motion: disable drag-to-reschedule when the user prefers it,
	// so we never animate/move events out from under them.
	const prefersReducedMotion =
		typeof window !== 'undefined' &&
		window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
	const dragEnabled = !prefersReducedMotion;

	// Fast lookup from event id back to the source appointment.
	const byId = $derived(new Map(appointments.map((a) => [String(a.id), a])));

	// Map each appointment -> an event-calendar event object. start/end are JS Date
	// objects (local time); end defaults to +1h when ScheduledEnd is null.
	const events = $derived(
		appointments
			.filter((a) => a.scheduledStart)
			.map((a) => {
				const start = new Date(a.scheduledStart);
				const end = a.scheduledEnd
					? new Date(a.scheduledEnd)
					: new Date(start.getTime() + DEFAULT_DURATION_MS);
				const { color, textColor } = colorForType(a.type);
				return {
					id: String(a.id),
					title: a.title,
					start,
					end,
					backgroundColor: color,
					textColor,
					extendedProps: { type: a.type, status: a.status }
				};
			})
	);

	// event-calendar options. We keep `events` reactive by mutating this $state object;
	// updating `options.events` is the supported way to refresh the calendar in Svelte.
	let options = $state({
		view: 'dayGridMonth',
		date: new Date(),
		events: [] as typeof events,
		headerToolbar: {
			start: 'title',
			center: 'dayGridMonth,timeGridWeek,listMonth',
			end: 'today prev,next'
		},
		buttonText: {
			today: 'Today',
			dayGridMonth: 'Month',
			timeGridWeek: 'Week',
			listMonth: 'Agenda'
		},
		views: {
			timeGridWeek: { pointer: true },
			dayGridMonth: { pointer: true }
		},
		// Interaction plugin options
		selectable: true,
		editable: dragEnabled,
		eventStartEditable: dragEnabled,
		eventDurationEditable: false,
		nowIndicator: true,
		height: '100%',
		dayMaxEvents: true,
		eventClick(info: { event: { id: string } }) {
			const a = byId.get(info.event.id);
			if (a) onSelectAppointment?.(a);
		},
		// Clicking an empty date (month/list) or time slot (week).
		dateClick(info: { date: Date; allDay: boolean }) {
			// In all-day / month cells default the time to 9:00am; in week view honour
			// the clicked time slot.
			const d = new Date(info.date);
			if (info.allDay) d.setHours(9, 0, 0, 0);
			onCreateAt?.(dateToLocalWallClock(d));
		},
		// Drag selection of a range in week view -> create prefilled with the start.
		select(info: { start: Date; allDay: boolean }) {
			const d = new Date(info.start);
			if (info.allDay) d.setHours(9, 0, 0, 0);
			onCreateAt?.(dateToLocalWallClock(d));
		},
		// Drag-to-reschedule: persist via the parent's existing update mutation.
		eventDrop(info: {
			event: { id: string; start: Date; end: Date | null };
			revert: () => void;
		}) {
			const a = byId.get(info.event.id);
			if (!a || !onReschedule) {
				info.revert();
				return;
			}
			const startUtc = info.event.start.toISOString();
			const endUtc = (info.event.end ?? info.event.start).toISOString();
			onReschedule(a, startUtc, endUtc);
		}
	});

	// Keep the calendar's events in sync with the (filtered) appointments prop.
	$effect(() => {
		options.events = events;
	});

	$effect(() => {
		if (initialDate) options.date = initialDate;
	});

	const plugins = [DayGrid, TimeGrid, List, Interaction];
</script>

<div class="ec-app flex h-full min-h-[34rem] flex-col gap-3">
	<div class="min-h-0 flex-1" data-testid="appointments-calendar">
		<Calendar {plugins} {options} />
	</div>
</div>

<!--
  Styling: map event-calendar's CSS variables onto the app's tokens so the
  calendar matches the dark theme, and respect prefers-reduced-motion. The
  per-event background/text colors come from the event objects (TYPE_COLORS).
  Scoped to .ec-app so we never touch global styles.
-->
<style>
	.ec-app :global(.ec) {
		--ec-border-color: var(--border);
		--ec-bg-color: var(--card);
		--ec-day-bg-color: var(--card);
		--ec-text-color: var(--foreground);
		--ec-today-bg-color: color-mix(in oklab, var(--primary) 10%, transparent);
		--ec-highlight-color: color-mix(in oklab, var(--primary) 14%, transparent);
		--ec-button-bg-color: var(--card);
		--ec-button-text-color: var(--foreground);
		--ec-button-border-color: var(--border);
		--ec-button-active-bg-color: var(--primary);
		--ec-button-active-text-color: var(--primary-foreground);
		--ec-button-active-border-color: var(--primary);
		--ec-now-indicator-color: var(--destructive);
		--ec-popup-bg-color: var(--popover);
		color: var(--foreground);
		border-radius: var(--radius, 0.5rem);
		font-size: 0.875rem;
	}

	.ec-app :global(.ec-button) {
		border-radius: calc(var(--radius, 0.5rem) - 2px);
	}

	.ec-app :global(.ec-event) {
		border: none;
		border-radius: 0.375rem;
		font-weight: 500;
		cursor: pointer;
	}

	.ec-app :global(.ec-event:focus-visible) {
		outline: 2px solid var(--ring);
		outline-offset: 1px;
	}

	@media (prefers-reduced-motion: reduce) {
		.ec-app :global(.ec),
		.ec-app :global(.ec *) {
			transition: none !important;
			animation: none !important;
		}
	}
</style>
