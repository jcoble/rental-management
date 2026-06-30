/**
 * Lenient parsing + formatting for the typeable date pickers.
 *
 * These helpers convert between a human-typed string (e.g. "3/15/1958",
 * "03-15-1958", "1958-03-15") and the canonical date-only ISO `yyyy-MM-dd`
 * string the pickers bind. They are PURE STRING + integer math — they never
 * construct a `Date` and never touch the timezone, so they cannot reintroduce
 * the off-by-one drift that the UTC-pinned date-only contract guards against.
 *
 * The display format is US `MM/DD/YYYY` (the format the ticket calls for and
 * the one the masked input shows while typing).
 */

const MONTH_DAYS = [31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

function isLeapYear(year: number): boolean {
	return (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
}

/** Days in a given month (1-12) for a given year, leap-aware. */
function daysInMonth(year: number, month: number): number {
	if (month === 2 && !isLeapYear(year)) return 28;
	return MONTH_DAYS[month - 1];
}

function pad(n: number, width: number): string {
	return String(n).padStart(width, '0');
}

function parseIsoDateParts(input: string): { year: number; month: number; day: number } | null {
	const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(input.trim());
	if (!m) return null;

	const year = Number(m[1]);
	const month = Number(m[2]);
	const day = Number(m[3]);
	if (!Number.isInteger(year) || !Number.isInteger(month) || !Number.isInteger(day)) return null;
	if (year < 1 || year > 9999) return null;
	if (month < 1 || month > 12) return null;
	if (day < 1 || day > daysInMonth(year, month)) return null;

	return { year, month, day };
}

/**
 * Parse a loosely-typed date string into a canonical `yyyy-MM-dd` string.
 * Returns `null` when the input can't be understood as a real calendar date.
 *
 * Accepted shapes (separators may be `/`, `-`, `.`, or whitespace):
 *   - `M/D/YYYY`, `MM/DD/YYYY`            (US month-first — the default)
 *   - `YYYY-MM-DD`, `YYYY/M/D`            (ISO / year-first)
 *   - 2-digit years are expanded: 00-69 → 2000-2069, 70-99 → 1970-1999.
 *
 * The value is validated as a real date (correct day-of-month, leap years),
 * so "02/30/2020" or "13/01/2020" return `null`.
 */
export function parseLooseDate(input: string): string | null {
	if (!input) return null;
	const trimmed = input.trim();
	if (!trimmed) return null;

	// Split on any common separator run.
	const parts = trimmed.split(/[\s/.\-]+/).filter((p) => p.length > 0);
	if (parts.length !== 3) return null;
	if (parts.some((p) => !/^\d+$/.test(p))) return null;

	let year: number;
	let month: number;
	let day: number;

	if (parts[0].length === 4) {
		// Year-first: YYYY-MM-DD
		year = Number(parts[0]);
		month = Number(parts[1]);
		day = Number(parts[2]);
	} else if (parts[2].length === 4) {
		// Month-first (US): MM/DD/YYYY
		month = Number(parts[0]);
		day = Number(parts[1]);
		year = Number(parts[2]);
	} else {
		// Ambiguous 2-digit year — treat as US month-first and expand the year.
		month = Number(parts[0]);
		day = Number(parts[1]);
		const yy = Number(parts[2]);
		year = yy <= 69 ? 2000 + yy : 1900 + yy;
	}

	if (!Number.isInteger(year) || !Number.isInteger(month) || !Number.isInteger(day)) return null;
	if (year < 1 || year > 9999) return null;
	if (month < 1 || month > 12) return null;
	if (day < 1 || day > daysInMonth(year, month)) return null;

	return `${pad(year, 4)}-${pad(month, 2)}-${pad(day, 2)}`;
}

/**
 * Parse only a date string that looks complete enough to auto-commit while the
 * user is still focused in the field. This avoids normalizing partial input like
 * `2027-07-3` to July 3 while the user is still typing July 31.
 */
export function parseCompleteLooseDate(input: string): string | null {
	if (!input) return null;
	const trimmed = input.trim();
	if (!trimmed) return null;

	const parts = trimmed.split(/[\s/.\-]+/).filter((p) => p.length > 0);
	if (parts.length !== 3) return null;
	if (parts.some((p) => !/^\d+$/.test(p))) return null;

	const yearFirst = parts[0].length === 4;
	if (yearFirst) {
		if (parts[1].length !== 2 || parts[2].length !== 2) return null;
		return parseLooseDate(trimmed);
	}

	// Do not auto-commit two-digit years while the user is still typing a masked
	// MM/DD/YYYY value. Otherwise `08012026` normalizes after `08/01/20`.
	const yearLast = parts[2].length === 4;
	return yearLast ? parseLooseDate(trimmed) : null;
}

/**
 * Format a canonical `yyyy-MM-dd` string as `MM/DD/YYYY` for the text input.
 * Returns "" for empty/invalid input. Pure string math — no Date, no tz.
 */
export function formatIsoToUsInput(iso: string | undefined | null): string {
	if (!iso) return '';
	const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso.trim());
	if (!m) return '';
	return `${m[2]}/${m[3]}/${m[1]}`;
}

/**
 * Keep a date text field in a visible US mask while the user types.
 * Non-digits are ignored and the value is capped at MM/DD/YYYY.
 */
export function maskDateInput(input: string): string {
	const digits = input.replace(/\D/g, '').slice(0, 8);
	if (digits.length <= 2) return digits;
	if (digits.length <= 4) return `${digits.slice(0, 2)}/${digits.slice(2)}`;
	return `${digits.slice(0, 2)}/${digits.slice(2, 4)}/${digits.slice(4)}`;
}

/**
 * Add one calendar year to an ISO date without constructing a JS Date.
 * Leap day clamps to Feb 28 in non-leap years.
 */
export function addCalendarYear(iso: string): string | null {
	const parts = parseIsoDateParts(iso);
	if (!parts) return null;

	const nextYear = parts.year + 1;
	if (nextYear > 9999) return null;

	const nextDay = Math.min(parts.day, daysInMonth(nextYear, parts.month));
	return `${pad(nextYear, 4)}-${pad(parts.month, 2)}-${pad(nextDay, 2)}`;
}
