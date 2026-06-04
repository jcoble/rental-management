// Helpers for the Appointments calendar view: timezone conversion (UTC <-> local
// wall-clock) and the Type -> color map used to color events + render the legend.
//
// TIMEZONE CONTRACT (see DateTimePicker.svelte):
//   - The API stores/returns timestamps in UTC (ISO with `Z`, timestamptz / Kind=Utc).
//   - The shared DateTimePicker binds a LOCAL WALL-CLOCK ISO `yyyy-MM-ddTHH:mm:ss`
//     (no offset) — the date+time the landlord literally sees and types.
//   - @event-calendar works with JS Date objects (local time), so the calendar is
//     naturally rendered in the landlord's local tz.
// So: convert UTC -> local for display/form, and local -> UTC before saving.

import type { AppointmentType } from '$lib/types';

// ── Timezone conversion ───────────────────────────────────────────────────────

/** Pad a number to 2 digits. */
function p2(n: number): string {
	return String(n).padStart(2, '0');
}

/**
 * Format a JS Date as a LOCAL wall-clock ISO string `yyyy-MM-ddTHH:mm:ss`
 * (no timezone suffix) — the shape the shared DateTimePicker expects.
 */
export function dateToLocalWallClock(d: Date): string {
	return (
		`${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())}` +
		`T${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}`
	);
}

/**
 * Convert a UTC ISO string (e.g. "2026-06-04T18:30:00Z") to a local wall-clock
 * ISO `yyyy-MM-ddTHH:mm:ss` for the DateTimePicker. Returns "" for empty/invalid.
 */
export function utcIsoToLocalWallClock(utcIso: string | null | undefined): string {
	if (!utcIso) return '';
	const d = new Date(utcIso);
	if (isNaN(d.getTime())) return '';
	return dateToLocalWallClock(d);
}

/**
 * Convert a LOCAL wall-clock ISO `yyyy-MM-ddTHH:mm:ss` (no offset, as produced by
 * the DateTimePicker) to a UTC ISO string for the API. Returns "" for empty/invalid.
 *
 * `new Date("2026-06-04T18:30:00")` (no `Z`) parses in LOCAL time, so `.toISOString()`
 * yields the correct UTC instant.
 */
export function localWallClockToUtcIso(wallClock: string | null | undefined): string {
	if (!wallClock) return '';
	const d = new Date(wallClock);
	if (isNaN(d.getTime())) return '';
	return d.toISOString();
}

// ── Type → color map ──────────────────────────────────────────────────────────

export type TypeColor = {
	/** CSS variable reference used for the event background + legend swatch. */
	color: string;
	/** Readable foreground for text drawn on the colored event. */
	textColor: string;
	/** Human label for the legend. */
	label: string;
};

/**
 * Color each appointment Type with the app's semantic / chart palette so the
 * landlord can scan the month at a glance:
 *   Showing          -> primary (blue)     "what showings do I have"
 *   Inspection       -> warning (amber)
 *   MoveIn           -> success (green)
 *   MoveOut          -> destructive (red)
 *   MaintenanceVisit -> chart-4 (purple)
 *   OwnerMeeting     -> muted-foreground (neutral)
 */
export const TYPE_COLORS: Record<AppointmentType, TypeColor> = {
	Showing: { color: 'var(--primary)', textColor: 'var(--primary-foreground)', label: 'Showing' },
	Inspection: { color: 'var(--warning)', textColor: 'var(--warning-foreground)', label: 'Inspection' },
	MoveIn: { color: 'var(--success)', textColor: 'var(--success-foreground)', label: 'Move-in' },
	MoveOut: { color: 'var(--destructive)', textColor: 'var(--destructive-foreground)', label: 'Move-out' },
	MaintenanceVisit: { color: 'var(--chart-4)', textColor: '#ffffff', label: 'Maintenance' },
	OwnerMeeting: { color: 'var(--muted-foreground)', textColor: 'var(--background)', label: 'Owner meeting' }
};

const FALLBACK_COLOR: TypeColor = {
	color: 'var(--muted-foreground)',
	textColor: 'var(--background)',
	label: 'Other'
};

export function colorForType(type: string): TypeColor {
	return TYPE_COLORS[type as AppointmentType] ?? FALLBACK_COLOR;
}

/** Legend entries in display order. */
export const TYPE_LEGEND: { type: AppointmentType; color: TypeColor }[] = (
	['Showing', 'Inspection', 'MoveIn', 'MoveOut', 'MaintenanceVisit', 'OwnerMeeting'] as AppointmentType[]
).map((type) => ({ type, color: TYPE_COLORS[type] }));

/** Default appointment duration when ScheduledEnd is null: +1 hour. */
export const DEFAULT_DURATION_MS = 60 * 60 * 1000;
