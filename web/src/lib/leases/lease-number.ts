export function defaultLeaseNumber(date: Date = new Date()): string {
	const fallback = new Date();
	const year = Number.isFinite(date.getTime()) ? date.getFullYear() : fallback.getFullYear();
	return `L-${year}-001`;
}
