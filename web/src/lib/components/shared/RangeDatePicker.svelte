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
    invalid?   boolean (bindable) caller-controlled invalid state for submit guards.
    align?     'start' | 'center' | 'end'   popover alignment. Default 'start'.
    testid?    string  applied as data-testid on the trigger button for E2E.
    id?        string  id for the trigger (label `for=` association).
-->
<script lang="ts">
	import { RangeCalendar } from '$lib/components/ui/range-calendar';
	import * as Popover from '$lib/components/ui/popover';
	import { Button, buttonVariants } from '$lib/components/ui/button';
	import { cn } from '$lib/utils';
	import {
		applyRangeDatePickerValue,
		commitDatePickerText,
		datePickerYearOptions
	} from './date-picker-state';
	import { formatIsoToUsInput, maskDateInput, parseCompleteLooseDate } from '$lib/utils/parse-date';
	import {
		calendarPlaceholderForStart,
		servicePeriodPreset,
		servicePeriodPresetState
	} from './range-date-picker-state';
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
		invalid = $bindable(false),
		onchange,
		presets = false,
		presetMode = 'standard',
		placeholder = 'Pick a date range',
		disabled = false,
		align = 'start',
		testid,
		id
	}: {
		start?: string;
		end?: string;
		invalid?: boolean;
		onchange?: (range: { start: string; end: string }) => void;
		presets?: boolean;
		presetMode?: 'standard' | 'service-period';
		placeholder?: string;
		disabled?: boolean;
		align?: 'start' | 'center' | 'end';
		testid?: string;
		id?: string;
	} = $props();

	let open = $state(false);
	let calendarMonths = $state(2);
	const timezone = getLocalTimeZone();
	let calendarMonth = $state<DateValue | undefined>(toCalendarDate(calendarPlaceholderForStart(start)) ?? today(timezone));
	let startText = $state(formatIsoToUsInput(start));
	let endText = $state(formatIsoToUsInput(end));
	let lastSyncedStart = $state(start);
	let lastSyncedEnd = $state(end);

	$effect(() => {
		const media = window.matchMedia('(max-width: 639px)');
		const updateMonths = () => {
			calendarMonths = media.matches ? 1 : 2;
		};
		updateMonths();
		media.addEventListener('change', updateMonths);
		return () => media.removeEventListener('change', updateMonths);
	});

	$effect(() => {
		if (start !== lastSyncedStart) {
			lastSyncedStart = start;
			startText = formatIsoToUsInput(start);
		}
		if (end !== lastSyncedEnd) {
			lastSyncedEnd = end;
			endText = formatIsoToUsInput(end);
		}
	});

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
	const years = $derived(datePickerYearOptions());

	const label = $derived.by(() => {
		const s = toCalendarDate(start);
		const e = toCalendarDate(end);
		if (s && e) return `${formatter.format(s.toDate('UTC'))} – ${formatter.format(e.toDate('UTC'))}`;
		if (s) return `${formatter.format(s.toDate('UTC'))} – …`;
		return placeholder;
	});

	const hasValue = $derived(Boolean(start));

	function apply(nextStart: string, nextEnd: string, close = false) {
		const next = applyRangeDatePickerValue(
			{ start, end, invalid },
			{ start: nextStart, end: nextEnd }
		);
		invalid = next.invalid;
		if (!next.changed) return;
		start = next.start;
		end = next.end;
		onchange?.({ start: next.start, end: next.end });
		if (close) open = false;
	}

	function commitTypedDate(field: 'start' | 'end') {
		const typed = field === 'start' ? startText : endText;
		const current = field === 'start' ? start : end;
		const next = commitDatePickerText(typed, current);
		invalid = next.invalid;
		if (field === 'start') startText = next.text;
		else endText = next.text;
		if (next.value === current) return;
		if (field === 'start') apply(next.value, end, false);
		else apply(start, next.value, false);
	}

	function handleTypedDateInput(field: 'start' | 'end', event: Event) {
		const input = event.target as HTMLInputElement;
		const raw = input.value;
		if (invalid) invalid = false;
		if (field === 'start') startText = raw;
		else endText = raw;
		if (raw.trim() === '') {
			if (field === 'start') apply('', end, false);
			else apply(start, '', false);
			return;
		}

		const completeIso = parseCompleteLooseDate(raw);
		if (completeIso) {
			if (field === 'start') startText = formatIsoToUsInput(completeIso);
			else endText = formatIsoToUsInput(completeIso);
			input.value = formatIsoToUsInput(completeIso);
			if (field === 'start') apply(completeIso, end, false);
			else apply(start, completeIso, false);
			return;
		}

		const masked = maskDateInput(raw);
		if (field === 'start') startText = masked;
		else endText = masked;
		if (masked !== raw) input.value = masked;
	}

	function handleValueChange(next: { start?: DateValue; end?: DateValue } | undefined) {
		const s = next?.start ? next.start.toString() : '';
		const e = next?.end ? next.end.toString() : '';
		if (s) calendarMonth = toCalendarDate(calendarPlaceholderForStart(s));
		// Close once a full range is picked (both ends chosen).
		apply(s, e, Boolean(s && e));
	}

	$effect(() => {
		if (open && start && !end) calendarMonth = toCalendarDate(calendarPlaceholderForStart(start));
	});

	// ---- Presets (optional) ---------------------------------------------------
	type Preset = { label: string; range: () => { start: string; end: string } };

	const tz = timezone;
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

	const servicePresetList: Preset[] = [
		{ label: 'This month', range: () => servicePeriodPreset(0, today(tz).toString()) },
		{ label: 'Last month', range: () => servicePeriodPreset(-1, today(tz).toString()) },
		{ label: 'Custom', range: () => ({ start: '', end: '' }) }
	];
	const activePreset = $derived.by(() => {
		if (presetMode !== 'service-period') return null;
		return servicePeriodPresetState(start, end, today(tz).toString());
	});
	const visiblePresets = $derived(presetMode === 'service-period' ? servicePresetList : presetList);

	function applyPreset(p: Preset) {
		const r = p.range();
		if (p.label === 'Custom') {
			apply(r.start, r.end, false);
			calendarMonth = today(tz);
			return;
		}
		calendarMonth = toCalendarDate(calendarPlaceholderForStart(r.start));
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
			!hasValue && 'text-muted-foreground',
			invalid && 'ring-2 ring-destructive'
		)}
		aria-invalid={invalid}
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
					{#each visiblePresets as p (p.label)}
						<Button
							variant={activePreset === p.label ? 'secondary' : 'ghost'}
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
			<div class="flex flex-col">
				<div class="hidden grid-cols-2 gap-2 border-b p-3 sm:grid">
					<label class="grid gap-1 text-xs font-medium text-muted-foreground" for={id ? `${id}-start` : undefined}>
						From
						<input
							id={id ? `${id}-start` : undefined}
							data-testid={testid ? `${testid}-start-input` : undefined}
							aria-label="Start date"
							inputmode="numeric"
							autocomplete="off"
							placeholder="MM/DD/YYYY"
							bind:value={startText}
							oninput={(event) => handleTypedDateInput('start', event)}
							onblur={() => commitTypedDate('start')}
							onkeydown={(event) => event.key === 'Enter' && commitTypedDate('start')}
							class={cn(
								'm3-field-surface h-9 w-full min-w-0 rounded-[var(--m3-shape-medium)] px-2 text-sm text-foreground outline-none',
								invalid && 'ring-2 ring-destructive'
							)}
						/>
					</label>
					<label class="grid gap-1 text-xs font-medium text-muted-foreground" for={id ? `${id}-end` : undefined}>
						To
						<input
							id={id ? `${id}-end` : undefined}
							data-testid={testid ? `${testid}-end-input` : undefined}
							aria-label="End date"
							inputmode="numeric"
							autocomplete="off"
							placeholder="MM/DD/YYYY"
							bind:value={endText}
							oninput={(event) => handleTypedDateInput('end', event)}
							onblur={() => commitTypedDate('end')}
							onkeydown={(event) => event.key === 'Enter' && commitTypedDate('end')}
							class={cn(
								'm3-field-surface h-9 w-full min-w-0 rounded-[var(--m3-shape-medium)] px-2 text-sm text-foreground outline-none',
								invalid && 'ring-2 ring-destructive'
							)}
						/>
					</label>
				</div>
				<RangeCalendar
					bind:placeholder={calendarMonth}
					value={rangeValue}
					onValueChange={handleValueChange}
					numberOfMonths={calendarMonths}
					captionLayout="dropdown"
					{years}
				/>
			</div>
		</div>
	</Popover.Content>
</Popover.Root>
