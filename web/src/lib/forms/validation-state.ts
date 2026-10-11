export function isPositiveNumericInput(value: unknown): boolean {
	const numeric = typeof value === 'number' ? value : typeof value === 'string' ? Number(value.trim()) : Number.NaN;
	return Number.isFinite(numeric) && numeric > 0;
}
