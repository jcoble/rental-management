export interface StatementDateRangeState {
	from: string;
	to: string;
}

export interface StatementAsOfState {
	to: string;
}

function padDatePart(value: number): string {
	return String(value).padStart(2, '0');
}

/** Format a date-only value in UTC so SSR and the API use the same calendar day. */
export function getTodayIsoDate(now = new Date()): string {
	return [now.getUTCFullYear(), padDatePart(now.getUTCMonth() + 1), padDatePart(now.getUTCDate())].join('-');
}

/** Return the first and last date of the current UTC calendar month. */
export function getCurrentMonthDateRange(now = new Date()): StatementDateRangeState {
	const year = now.getUTCFullYear();
	const month = now.getUTCMonth();
	const lastDay = new Date(Date.UTC(year, month + 1, 0)).getUTCDate();

	return {
		from: `${year}-${padDatePart(month + 1)}-01`,
		to: `${year}-${padDatePart(month + 1)}-${padDatePart(lastDay)}`
	};
}

/** Accept only a real yyyy-MM-dd calendar date; date-only values never undergo local-time parsing. */
export function isIsoDate(value: string | null | undefined): value is string {
	if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;

	const [yearText, monthText, dayText] = value.split('-');
	const year = Number(yearText);
	const month = Number(monthText);
	const day = Number(dayText);
	const date = new Date(Date.UTC(year, month - 1, day));

	return (
		year >= 1 &&
		date.getUTCFullYear() === year &&
		date.getUTCMonth() === month - 1 &&
		date.getUTCDate() === day
	);
}

function validParam(params: Pick<URLSearchParams, 'get'>, key: string, fallback: string): string {
	const value = params.get(key);
	return isIsoDate(value) ? value : fallback;
}

export function readDateRangeState(
	params: Pick<URLSearchParams, 'get'>,
	now = new Date()
): StatementDateRangeState {
	const fallback = getCurrentMonthDateRange(now);
	const from = validParam(params, 'from', fallback.from);
	const to = validParam(params, 'to', fallback.to);

	return from <= to ? { from, to } : fallback;
}

export function readAsOfState(
	params: Pick<URLSearchParams, 'get'>,
	now = new Date()
): StatementAsOfState {
	return { to: validParam(params, 'to', getTodayIsoDate(now)) };
}

export function buildDateRangeQueryString(
	state: StatementDateRangeState,
	base?: URLSearchParams
): string {
	const params = new URLSearchParams(base);
	params.set('from', state.from);
	params.set('to', state.to);
	return params.toString();
}

export function buildAsOfQueryString(state: StatementAsOfState, base?: URLSearchParams): string {
	const params = new URLSearchParams(base);
	params.delete('from');
	params.set('to', state.to);
	return params.toString();
}
