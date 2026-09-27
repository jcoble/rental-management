/**
 * Format a *date-only* value (lease start/end, move-in/out, payment due/paid,
 * expense incurred/due, DOB, etc.) for display.
 *
 * These fields are calendar dates with no time-of-day. The API stores and returns
 * them as UTC-midnight timestamps (e.g. `2024-12-15T00:00:00Z`). Formatting such a
 * value with the default (local) timezone shifts it back a day in any behind-UTC
 * timezone — UTC midnight Dec 15 is Dec 14 6pm in US Central — which is the classic
 * "off-by-one" date bug. We pin the formatter to UTC so the displayed day always
 * matches the day the user picked. (The DatePicker is already UTC-pinned on input.)
 *
 * Accepts either a bare `yyyy-MM-dd` string or a full ISO timestamp; both render
 * the same UTC calendar day.
 */
export function formatDateOnly(date: string | Date | null | undefined): string {
	if (!date) return '';
	const d = new Date(date);
	if (isNaN(d.getTime())) return typeof date === 'string' ? date : '';
	return d.toLocaleDateString('en-US', {
		month: 'short',
		day: 'numeric',
		year: 'numeric',
		timeZone: 'UTC'
	});
}

/**
 * Whole-day signed difference between a *date-only* value and today, counted in **UTC**
 * calendar days. Positive = in the future, 0 = today, negative = in the past.
 *
 * Date-only fields arrive as UTC-midnight timestamps. Doing the math in local time
 * (`setHours(0,0,0,0)` on a UTC-midnight Date) shifts the day in any behind-UTC zone — the
 * same off-by-one that bites display formatting. Floor both sides to their UTC day index
 * (ms since epoch / 86_400_000) so "due today" stays today regardless of the viewer's zone.
 *
 * Returns `null` for empty/invalid input.
 */
export function daysFromTodayUtc(date: string | Date | null | undefined): number | null {
	if (!date) return null;
	const d = new Date(date);
	if (isNaN(d.getTime())) return null;
	const MS_PER_DAY = 86_400_000;
	const targetDay = Math.floor(d.getTime() / MS_PER_DAY);
	const todayDay = Math.floor(Date.now() / MS_PER_DAY);
	return targetDay - todayDay;
}

/**
 * True when a *date-only* due date is strictly in the past in UTC day terms (i.e. its UTC
 * calendar day is before today's UTC calendar day). A value due *today* is NOT overdue.
 */
export function isPastDueUtc(date: string | Date | null | undefined): boolean {
	const days = daysFromTodayUtc(date);
	return days !== null && days < 0;
}

/**
 * Convert a `datetime-local` input value (`yyyy-MM-ddTHH:mm`, wall-clock with NO zone) into a full
 * ISO-8601 string that carries the BROWSER'S LOCAL UTC OFFSET, e.g.
 * `2026-06-15T14:00` → `2026-06-15T14:00:00-04:00`.
 *
 * Why the offset and not a bare `Z` instant: a `datetime-local` value is the landlord's wall-clock
 * time. Sending it un-zoned lands on the server as `DateTimeKind.Unspecified`, which the API relabels
 * as UTC (storing 2pm as 14:00Z and texting the tenant the wrong "2pm (UTC)"). Emitting the offset
 * makes System.Text.Json bind it as `DateTimeKind.Local` and `.ToUtc()` convert to the true instant —
 * the FROZEN contract the work-order schedule fields use. Returns null for empty/invalid input so the
 * caller can omit the field (PATCH treats a missing value as "leave unchanged").
 */
export function localInputToOffsetIso(value: string | null | undefined): string | null {
	if (!value) return null;
	// `new Date("yyyy-MM-ddTHH:mm")` (no zone) parses as LOCAL time.
	const d = new Date(value);
	if (isNaN(d.getTime())) return null;
	const pad = (n: number) => String(n).padStart(2, '0');
	// getTimezoneOffset() is minutes BEHIND UTC (positive when behind, e.g. +240 for US-Eastern DST),
	// so the printed sign is inverted: behind-UTC → "-04:00".
	const offsetMinutes = d.getTimezoneOffset();
	const sign = offsetMinutes <= 0 ? '+' : '-';
	const abs = Math.abs(offsetMinutes);
	const offset = `${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`;
	return (
		`${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}` +
		`T${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}${offset}`
	);
}

export function formatDate(date: string | Date | null | undefined): string {
	if (!date) return '';
	const d = new Date(date);
	return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

export function formatRelative(date: string | Date | null | undefined): string {
	if (!date) return '';
	const d = new Date(date);
	const now = new Date();
	const diff = now.getTime() - d.getTime();
	const minutes = Math.floor(diff / 60000);
	if (minutes < 1) return 'just now';
	if (minutes < 60) return `${minutes}m ago`;
	const hours = Math.floor(minutes / 60);
	if (hours < 24) return `${hours}h ago`;
	const days = Math.floor(hours / 24);
	if (days < 7) return `${days}d ago`;
	return formatDate(date);
}
