const TRAILING_PERIOD = /^(.*\S)\s+(\d{4})-(\d{2})$/;

/**
 * Converts only the legacy trailing ISO period used by early tenant-ledger rows.
 * This is a display concern: stored descriptions and business keys remain unchanged.
 */
export function normalizeTenantLedgerDescription(value: string | null | undefined): string {
	if (!value) return value ?? '';
	const match = TRAILING_PERIOD.exec(value);
	if (!match) return value;

	const year = Number(match[2]);
	const month = Number(match[3]);
	if (!Number.isInteger(year) || !Number.isInteger(month) || month < 1 || month > 12) {
		return value;
	}

	const monthName = new Intl.DateTimeFormat('en-US', {
		month: 'long',
		timeZone: 'UTC'
	}).format(new Date(Date.UTC(year, month - 1, 1)));
	return `${match[1]} ${monthName} ${year}`;
}
