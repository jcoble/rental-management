/** Formats a number as USD currency (matches the DataGrid's currency formatter). */
export function money(value: number | null | undefined): string {
	if (value == null) return '—';
	return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value);
}
