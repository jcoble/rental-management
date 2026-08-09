import { formatIsoToUsInput, parseLooseDate } from '../../utils/parse-date.ts';

export interface DatePickerTextCommit {
	text: string;
	value: string;
	invalid: boolean;
}

export interface DatePickerRangeOptions {
	min?: string;
	max?: string;
}

function isInRange(iso: string, { min, max }: DatePickerRangeOptions = {}): boolean {
	if (min && iso < min) return false;
	if (max && iso > max) return false;
	return true;
}

/** Apply the same commit contract used by DatePicker's blur/Enter handler. */
export function commitDatePickerText(
	text: string,
	currentValue: string,
	options: DatePickerRangeOptions = {}
): DatePickerTextCommit {
	const raw = text.trim();
	if (raw === '') return { text: '', value: '', invalid: false };

	const iso = parseLooseDate(raw);
	if (iso && isInRange(iso, options)) {
		return { text: formatIsoToUsInput(iso), value: iso, invalid: false };
	}

	return { text, value: currentValue, invalid: true };
}

/** Date fields use this contract to disable a submit action while text is invalid. */
export function datePickerSubmitDisabled(invalid: boolean, pending = false): boolean {
	return pending || invalid;
}

export interface DatePickerLayoutState {
	showTodayLabel: boolean;
	todayButtonWidthPx: number;
	calendarButtonSizePx: number;
	inputPaddingRightPx: number;
}

/** State-level representation of the narrow-field layout contract. */
export function datePickerLayoutForWidth(inlineSizePx: number): DatePickerLayoutState {
	if (inlineSizePx <= 128) {
		return {
			showTodayLabel: false,
			todayButtonWidthPx: 0,
			calendarButtonSizePx: 28,
			inputPaddingRightPx: 36
		};
	}
	if (inlineSizePx <= 192) {
		return {
			showTodayLabel: false,
			todayButtonWidthPx: 0,
			calendarButtonSizePx: 32,
			inputPaddingRightPx: 44
		};
	}
	return {
		showTodayLabel: true,
		todayButtonWidthPx: 72,
		calendarButtonSizePx: 36,
		inputPaddingRightPx: 104
	};
}

export interface RangeDatePickerValue {
	start: string;
	end: string;
	invalid: boolean;
}

/** Apply a calendar/preset range selection to the caller-controlled range state. */
export function applyRangeDatePickerValue(
	current: RangeDatePickerValue,
	next: Pick<RangeDatePickerValue, 'start' | 'end'>
): RangeDatePickerValue & { changed: boolean } {
	const changed = next.start !== current.start || next.end !== current.end;
	return { ...next, invalid: false, changed };
}
