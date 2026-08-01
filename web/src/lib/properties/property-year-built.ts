export function businessYearFromDateOnly(businessDate: string | null | undefined): number | null {
	if (!businessDate || !/^\d{4}-\d{2}-\d{2}/.test(businessDate)) return null;
	const year = Number(businessDate.slice(0, 4));
	return Number.isInteger(year) ? year : null;
}

export function propertyYearBuiltBusinessYearError(
	yearBuilt: number | null | undefined,
	businessDate: string | null | undefined
): string | null {
	if (yearBuilt === undefined || yearBuilt === null) return null;
	const businessYear = businessYearFromDateOnly(businessDate);
	if (businessYear === null || yearBuilt <= businessYear) return null;
	return `Year built cannot be later than the portfolio business year (${businessYear})`;
}
