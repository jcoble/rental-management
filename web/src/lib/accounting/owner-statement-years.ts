export const OWNER_STATEMENT_YEAR_COUNT = 5;
const OWNER_STATEMENT_YEAR_WATCH_MS = 250;

interface OwnerStatementYearWatchOptions {
	intervalMs?: number;
	now?: () => Date;
	setInterval?: (handler: () => void, timeout: number) => number;
	clearInterval?: (handle: number) => void;
}

export function currentOwnerStatementYear(now: Date = new Date()): number {
	return now.getFullYear();
}

export function ownerStatementYearOptions(
	currentYear = currentOwnerStatementYear()
): number[] {
	return Array.from({ length: OWNER_STATEMENT_YEAR_COUNT }, (_, index) => currentYear - index);
}

export function watchOwnerStatementYear(
	onYear: (year: number) => void,
	options: OwnerStatementYearWatchOptions = {}
): () => void {
	const getNow = options.now ?? (() => new Date());
	const intervalMs = options.intervalMs ?? OWNER_STATEMENT_YEAR_WATCH_MS;
	const schedule = options.setInterval ?? globalThis.setInterval;
	const clear = options.clearInterval ?? globalThis.clearInterval;
	let lastYear: number | undefined;

	function checkYear() {
		const nextYear = currentOwnerStatementYear(getNow());
		if (nextYear !== lastYear) {
			lastYear = nextYear;
			onYear(nextYear);
		}
	}

	checkYear();
	const timer = schedule(checkYear, intervalMs);
	return () => clear(timer);
}
