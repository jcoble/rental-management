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
