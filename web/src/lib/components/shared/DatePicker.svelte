<!--
  DatePicker — date-only picker (typeable input + shadcn Calendar in a Popover).

  A drop-in replacement for the native date input. Binds a plain ISO `yyyy-MM-dd`
  string (or "" for empty). NO time, NO timezone: a calendar date has no tz, so we
  never do tz math here — the API layer pins date-only fields to UTC on the wire.

  Two ways to set the value, both keyboard-first:
    1. TYPE a date in the text input (e.g. "3/15/1958" or "03/15/1958"). It is
       parsed leniently (see parse-date.ts) to the canonical `yyyy-MM-dd`. Invalid
       text shows an inline error and does not change the bound value.
    2. Click the calendar button to open the popover and pick a day. The calendar
       header has MONTH + YEAR dropdowns for fast jumps (no month-by-month clicking),
       reaching back to ~1900 so dates of birth are a couple of clicks away.

  The bound value handling is unchanged from the previous calendar-only version:
  the incoming `yyyy-MM-dd` is parsed to a `CalendarDate` for the bits-ui Calendar;
  on select we emit a plain `yyyy-MM-dd` string again. Pure string/CalendarDate
  conversions — no Date construction, so the UTC-pinned date-only contract holds.

  Props:
    value       string  (bindable)  ISO `yyyy-MM-dd`, or "" for empty. bind:value supported.
    onchange?   (iso: string) => void   fired on commit/clear (gets `yyyy-MM-dd` or "").
    placeholder string  text input placeholder when empty. Default "MM/DD/YYYY".
    disabled?   boolean
    min?        string  ISO `yyyy-MM-dd` — earliest selectable date (inclusive).
    max?        string  ISO `yyyy-MM-dd` — latest selectable date (inclusive).
    todayValue? string  ISO `yyyy-MM-dd` — override for the Today shortcut.
    showToday?  boolean show the Today shortcut. Default true.
    testid?     string  applied as data-testid on the text input for E2E.
    id?         string  id for the text input (label `for=` association).
    invalid?    boolean (bindable) reflects invalid typed or out-of-range text.
-->
<script lang="ts">
	import { Calendar } from '$lib/components/ui/calendar';
	import * as Popover from '$lib/components/ui/popover';
	import { buttonVariants } from '$lib/components/ui/button';
	import { cn } from '$lib/utils';
	import {
		formatIsoToUsInput,
		maskDateInput,
		parseCompleteLooseDate
	} from '$lib/utils/parse-date';
	import { commitDatePickerText } from './date-picker-state';
	import CalendarIcon from '@lucide/svelte/icons/calendar';
	import {
		CalendarDate,
		getLocalTimeZone,
		parseDate,
		today,
		type DateValue
	} from '@internationalized/date';

	let {
		value = $bindable(''),
		invalid = $bindable(false),
		onchange,
		placeholder = 'MM/DD/YYYY',
		disabled = false,
		min,
		max,
		todayValue,
		showToday = true,
		testid,
		id
	}: {
		value?: string;
		invalid?: boolean;
		onchange?: (iso: string) => void;
		placeholder?: string;
		disabled?: boolean;
		min?: string;
		max?: string;
		todayValue?: string;
		showToday?: boolean;
		testid?: string;
		id?: string;
	} = $props();

	let open = $state(false);

	// Text the user is currently typing. Re-synced from `value` whenever the bound
	// value changes from the outside (form load, calendar pick, clear).
	let text = $state(formatIsoToUsInput(value));
	let lastSyncedValue = $state(value);

	$effect(() => {
		// Only react to external value changes, not the ones we make from text.
		if (value !== lastSyncedValue) {
			lastSyncedValue = value;
			text = formatIsoToUsInput(value);
			invalid = false;
		}
	});

	// Parse an ISO `yyyy-MM-dd` string into a CalendarDate; return undefined on
	// empty/invalid input (never throws into the template).
	function toCalendarDate(iso: string | undefined): CalendarDate | undefined {
		if (!iso) return undefined;
		try {
			return parseDate(iso);
		} catch {
			return undefined;
		}
	}

	const selected = $derived(toCalendarDate(value));
	const minDate = $derived(toCalendarDate(min));
	const maxDate = $derived(toCalendarDate(max));

	// Year range for the year dropdown. Default reaches back to 1900 (so dates of
	// birth are reachable). When min/max are supplied, honor them as boundaries.
	const years = $derived.by(() => {
		const now = new Date().getUTCFullYear();
		const lo = minDate ? minDate.year : 1900;
		const hi = maxDate ? maxDate.year : now + 10;
		if (lo > hi) return [hi];
		return Array.from({ length: hi - lo + 1 }, (_, i) => lo + i);
	});

	function commit(iso: string) {
		if (iso === value) return;
		value = iso;
		lastSyncedValue = iso;
		onchange?.(iso);
	}

	function isInRange(iso: string): boolean {
		if (min && iso < min) return false;
		if (max && iso > max) return false;
		return true;
	}

	// ---- Text input ----------------------------------------------------------
	function commitText() {
		const next = commitDatePickerText(text, value, { min, max });
		invalid = next.invalid;
		text = next.text;
		commit(next.value);
	}

	function handleTextInput(e: Event) {
		const input = e.target as HTMLInputElement;
		const raw = input.value;
		// Clear the error as soon as the user resumes typing.
		if (invalid) invalid = false;
		if (raw.trim() === '') {
			text = '';
			commit('');
			return;
		}
		const completeIso = parseCompleteLooseDate(raw);
		if (completeIso && isInRange(completeIso)) {
			invalid = false;
			text = formatIsoToUsInput(completeIso);
			input.value = text;
			commit(completeIso);
			return;
		}

		text = maskDateInput(raw);
		if (text !== raw) input.value = text;
		const iso = parseCompleteLooseDate(text);
		if (iso && isInRange(iso)) {
			invalid = false;
			text = formatIsoToUsInput(iso);
			input.value = text;
			commit(iso);
		}
	}

	function handleKeydown(e: KeyboardEvent) {
		if (e.key === 'Enter') {
			e.preventDefault();
			commitText();
		}
	}

	// ---- Calendar ------------------------------------------------------------
	function handleValueChange(next: DateValue | undefined) {
		// CalendarDate.toString() is exactly `yyyy-MM-dd`.
		const iso = next ? next.toString() : '';
		text = formatIsoToUsInput(iso);
		invalid = false;
		commit(iso);
		if (iso) open = false;
	}

	function setToday() {
		const iso = todayValue?.trim() || today(getLocalTimeZone()).toString();
		if (!isInRange(iso)) {
			invalid = true;
			return;
		}
		text = formatIsoToUsInput(iso);
		invalid = false;
		commit(iso);
		open = false;
	}
</script>

<div class="date-picker-shell relative w-full min-w-0">
	<input
		{id}
		type="text"
		inputmode="numeric"
		autocomplete="off"
		data-testid={testid}
		bind:value={text}
		{disabled}
		{placeholder}
		aria-invalid={invalid}
		oninput={handleTextInput}
		onblur={commitText}
		onkeydown={handleKeydown}
		class={cn(
			'date-picker-input m3-field-surface h-11 w-full min-w-0 rounded-[var(--m3-shape-large)] bg-transparent py-2 pl-3 pr-[6.5rem] text-left text-sm font-normal text-foreground outline-none',
			'placeholder:text-muted-foreground disabled:cursor-not-allowed disabled:opacity-50',
			invalid && 'ring-2 ring-destructive'
		)}
	/>
	<div class="date-picker-actions absolute right-1 top-1/2 flex -translate-y-1/2 items-center gap-1">
		{#if showToday}
			<button
				type="button"
				{disabled}
				aria-label="Use today's date"
				data-testid={testid ? `${testid}-today` : undefined}
				onclick={setToday}
				class={cn(
					buttonVariants({ variant: 'ghost', size: 'sm' }),
					'date-picker-today h-8 whitespace-nowrap px-2 text-xs text-muted-foreground hover:bg-transparent'
				)}
			>
				<span class="date-picker-today-label">Today</span>
			</button>
		{/if}
		<Popover.Root bind:open>
			<Popover.Trigger
				{disabled}
				tabindex={-1}
				aria-label="Open calendar"
				data-testid={testid ? `${testid}-calendar-trigger` : undefined}
				class={cn(
					buttonVariants({ variant: 'ghost', size: 'icon' }),
					'date-picker-calendar-trigger size-9 shrink-0 text-muted-foreground hover:bg-transparent'
				)}
			>
				<CalendarIcon class="size-4 shrink-0 opacity-70" />
			</Popover.Trigger>
			<Popover.Content class="w-auto p-0" align="start">
				<Calendar
					type="single"
					value={selected}
					onValueChange={handleValueChange}
					minValue={minDate}
					maxValue={maxDate}
					captionLayout="dropdown"
					{years}
				/>
			</Popover.Content>
		</Popover.Root>
	</div>
</div>
{#if invalid}
	<p class="mt-1 text-xs text-destructive" data-testid={testid ? `${testid}-error` : undefined}>
		Enter a date as MM/DD/YYYY
	</p>
{/if}

<style>
	/* The text input owns the date text. Keep the actions in the field without
	 * stealing so much inline space that a narrow two-column form only shows a
	 * punctuation mark. */
	.date-picker-shell {
		container: date-picker / inline-size;
		min-inline-size: 0;
	}

	@container date-picker (max-width: 12rem) {
		.date-picker-input {
			/* Give the formatted date text the field's inline space first. */
			padding-left: 0.625rem;
			padding-right: 2.75rem !important;
			font-size: 0.8125rem;
		}

		.date-picker-today {
			display: none;
		}

		:global(.date-picker-calendar-trigger) {
			height: 2rem !important;
			width: 2rem !important;
		}

		.date-picker-actions {
			gap: 0;
			right: 0.25rem;
		}
	}

	@container date-picker (max-width: 8rem) {
		.date-picker-input {
			padding-left: 0.375rem;
			padding-right: 2.25rem !important;
			font-size: 0.75rem;
			letter-spacing: -0.01em;
		}

		:global(.date-picker-calendar-trigger) {
			height: 1.75rem !important;
			width: 1.75rem !important;
		}

		.date-picker-actions {
			right: 0;
		}
	}
</style>
