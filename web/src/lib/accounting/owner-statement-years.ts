export const OWNER_STATEMENT_YEAR_COUNT = 5;

export function currentOwnerStatementYear(now: Date = new Date()): number {
	return now.getFullYear();
}

export function ownerStatementYearOptions(
	currentYear = currentOwnerStatementYear()
): number[] {
	return Array.from({ length: OWNER_STATEMENT_YEAR_COUNT }, (_, index) => currentYear - index);
}
