<!--
  RangeDatePicker — start/end date range picker (shadcn RangeCalendar in a Popover).

  For report ranges and list filters. Binds two plain ISO `yyyy-MM-dd` strings
  (`start`/`end`), each "" when unset. Date-only, NO timezone (the API pins date-
  only fields to UTC on the wire) — no tz math here.

  Optional quick presets (This year / Year to date / Last 30 days / Month to date /
  Last month) are shown beside the calendar when `presets` is true.

  Props:
    start      string  (bindable)  ISO `yyyy-MM-dd` start, or "". bind:start supported.
    end        string  (bindable)  ISO `yyyy-MM-dd` end, or "". bind:end supported.
    onchange?  (range: { start: string; end: string }) => void   fired when the range changes.
    presets?   boolean  show quick-range preset buttons. Default false.
    placeholder string  trigger text when empty. Default "Pick a date range".
    disabled?  boolean
    align?     'start' | 'center' | 'end'   popover alignment. Default 'start'.
    testid?    string  applied as data-testid on the trigger button for E2E.
    id?        string  id for the trigger (label `for=` association).
-->
<script lang="ts">
	import { RangeCalendar } from '$lib/components/ui/range-calendar';
	import * as Popover from '$lib/components/ui/popover';
	import { Button, buttonVariants } from '$lib/components/ui/button';
	import { cn } from '$lib/utils';
	import CalendarIcon from '@lucide/svelte/icons/calendar';
	import {
		CalendarDate,
		parseDate,
		DateFormatter,
		today,
		getLocalTimeZone,
		type DateValue
	} from '@internationalized/date';

	let {
		start = $bindable(''),
		end = $bindable(''),
		onchange,
		presets = false,
		placeholder = 'Pick a date range',
		disabled = false,
		align = 'start',
		testid,
		id
	}: {
		start?: string;
		end?: string;
		onchange?: (range: { start: string; end: string }) => void;
		presets?: boolean;
		placeholder?: string;
		disabled?: boolean;
		align?: 'start' | 'center' | 'end';
		testid?: string;
		id?: string;
	} = $props();

	let open = $state(false);

	function toCalendarDate(iso: string | undefined): CalendarDate | undefined {
		if (!iso) return undefined;
		try {
			return parseDate(iso);
		} catch {
			return undefined;
		}
	}

	// UTC-pinned formatter so labels never slip a day in a behind-UTC tz.
	const formatter = new DateFormatter('en-US', {
		month: 'short',
		day: 'numeric',
		year: 'numeric',
		timeZone: 'UTC'
	});

	// bits-ui RangeCalendar value is `{ start?: DateValue; end?: DateValue }`.
	const rangeValue = $derived({
		start: toCalendarDate(start),
		end: toCalendarDate(end)
	});

	const label = $derived.by(() => {
		const s = toCalendarDate(start);
		const e = toCalendarDate(end);
		if (s && e) return `${formatter.format(s.toDate('UTC'))} – ${formatter.format(e.toDate('UTC'))}`;
		if (s) return `${formatter.format(s.toDate('UTC'))} – …`;
		return placeholder;
	});

	const hasValue = $derived(Boolean(start));

	function apply(nextStart: string, nextEnd: string, close = false) {
		if (nextStart === start && nextEnd === end) return;
		start = nextStart;
		end = nextEnd;
		onchange?.({ start: nextStart, end: nextEnd });
		if (close) open = false;
	}

	function handleValueChange(next: { start?: DateValue; end?: DateValue } | undefined) {
		const s = next?.start ? next.start.toString() : '';
		const e = next?.end ? next.end.toString() : '';
		// Close once a full range is picked (both ends chosen).
		apply(s, e, Boolean(s && e));
	}

	// ---- Presets (optional) ---------------------------------------------------
	type Preset = { label: string; range: () => { start: string; end: string } };

	const tz = getLocalTimeZone();
	const presetList: Preset[] = [
		{
			label: 'Year to date',
			range: () => {
				const now = today(tz);
				return { start: new CalendarDate(now.year, 1, 1).toString(), end: now.toString() };
			}
		},
		{
			label: 'This year',
			range: () => {
				const now = today(tz);
				return {
					start: new CalendarDate(now.year, 1, 1).toString(),
					end: new CalendarDate(now.year, 12, 31).toString()
				};
			}
		},
		{
			label: 'Last 30 days',
			range: () => {
				const now = today(tz);
				return { start: now.subtract({ days: 29 }).toString(), end: now.toString() };
			}
		},
		{
			label: 'Month to date',
			range: () => {
				const now = today(tz);
				return { start: new CalendarDate(now.year, now.month, 1).toString(), end: now.toString() };
			}
		},
		{
			label: 'Last month',
			range: () => {
				const now = today(tz);
				const firstThis = new CalendarDate(now.year, now.month, 1);
				const lastMonthEnd = firstThis.subtract({ days: 1 });
				const lastMonthStart = new CalendarDate(lastMonthEnd.year, lastMonthEnd.month, 1);
				return { start: lastMonthStart.toString(), end: lastMonthEnd.toString() };
			}
		}
	];

	function applyPreset(p: Preset) {
		const r = p.range();
		apply(r.start, r.end, true);
	}
</script>

<Popover.Root bind:open>
	<Popover.Trigger
		{id}
		data-testid={testid}
		{disabled}
		class={cn(
			buttonVariants({ variant: 'outline' }),
			'h-10 w-full justify-start text-left font-normal',
			!hasValue && 'text-muted-foreground'
		)}
	>
		<CalendarIcon class="size-4 shrink-0 opacity-70" />
		{label}
	</Popover.Trigger>
	<Popover.Content class="w-auto p-0" {align}>
		<div class="flex flex-col sm:flex-row">
			{#if presets}
				<div
					class="flex flex-row flex-wrap gap-1 border-b p-2 sm:w-40 sm:flex-col sm:flex-nowrap sm:border-b-0 sm:border-r"
				>
					{#each presetList as p (p.label)}
						<Button
							variant="ghost"
							size="sm"
							class="justify-start"
							onclick={() => applyPreset(p)}
							data-testid={testid ? `${testid}-preset-${p.label.toLowerCase().replace(/\s+/g, '-')}` : undefined}
						>
							{p.label}
						</Button>
					{/each}
				</div>
			{/if}
			<RangeCalendar
				value={rangeValue}
				onValueChange={handleValueChange}
				numberOfMonths={2}
				captionLayout="dropdown"
			/>
		</div>
	</Popover.Content>
</Popover.Root>
