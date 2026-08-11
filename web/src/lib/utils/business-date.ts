/** Return the current calendar date in the browser's local timezone. */
export function localIsoDate(now = new Date()): string {
	const pad = (value: number) => String(value).padStart(2, '0');
	return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/**
 * Normalize a server portfolio business date for a date-only input.
 * Missing or invalid values use the browser's local calendar date.
 */
export function businessDateOrToday(
	businessDate: string | null | undefined,
	now = new Date()
): string {
	const normalized = businessDate?.trim().slice(0, 10);
	return normalized && /^\d{4}-\d{2}-\d{2}$/.test(normalized)
		? normalized
		: localIsoDate(now);
}
