// Ambient module declaration for @event-calendar/core (v5).
//
// The package ships its Svelte 5 source via the `svelte` export condition and has
// no bundled type declarations, so svelte-check can't resolve types for it. We use
// it through a thin local wrapper (AppointmentCalendar.svelte) with explicit option
// typing, so a permissive ambient declaration is sufficient here.
declare module '@event-calendar/core' {
	import type { Component } from 'svelte';

	// The Calendar component takes `plugins` and `options` props (see package README).
	export const Calendar: Component<{
		plugins?: unknown[];
		options?: Record<string, unknown>;
	}>;

	// View / interaction plugins.
	export const DayGrid: unknown;
	export const TimeGrid: unknown;
	export const List: unknown;
	export const ResourceTimeGrid: unknown;
	export const ResourceTimeline: unknown;
	export const Interaction: unknown;

	// Imperative API (not used by our wrapper, declared for completeness).
	export function createCalendar(
		el: HTMLElement,
		plugins: unknown[],
		options: Record<string, unknown>
	): unknown;
	export function destroyCalendar(instance: unknown): void;
}

declare module '@event-calendar/core/index.css';
