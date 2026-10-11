export const MONEY_TABS = ['overview', 'cash-flow', 'activity', 'rent-payments', 'general-ledger', 'reports'] as const;

export type MoneyTab = (typeof MONEY_TABS)[number];

export const GENERAL_LEDGER_DEFAULT_SORT = '-effectiveOn,-postedAtUtc,-id';

export const GENERAL_LEDGER_SORTS = [
	'effectiveOn',
	'-effectiveOn',
	'postedAtUtc',
	'-postedAtUtc',
	'accountCode',
	'-accountCode'
] as const;

export type GeneralLedgerSort = (typeof GENERAL_LEDGER_SORTS)[number] | typeof GENERAL_LEDGER_DEFAULT_SORT;

const TAB_ALIASES: Record<string, MoneyTab> = {
	activity: 'activity',
	'cash-flow': 'cash-flow',
	'general-ledger': 'general-ledger',
	'general-leger': 'general-ledger',
	ledger: 'activity',
	'rent-payments': 'rent-payments',
	overview: 'overview',
	reports: 'reports',
	// Keep the previous visible-tab names useful for old saved links.
	history: 'activity',
	summary: 'overview'
};

/** Resolve a route tab without ever letting an unknown value select a hidden view. */
export function normalizeMoneyTab(value: string | null | undefined): MoneyTab {
	return TAB_ALIASES[value?.trim().toLowerCase() ?? ''] ?? 'overview';
}

/** Read a text filter while keeping URL state helpers independent of SvelteKit. */
export function readMoneyText(params: URLSearchParams, key: string): string {
	return params.get(key)?.trim() ?? '';
}

/** Read a positive integer filter; malformed values are treated as absent. */
export function readMoneyId(params: URLSearchParams, key: string): number | null {
	const value = Number(params.get(key));
	return Number.isInteger(value) && value > 0 ? value : null;
}

/** Read a one-based page number; malformed values return the supplied default. */
export function readMoneyPage(params: URLSearchParams, key = 'page', fallback = 1): number {
	const value = Number(params.get(key));
	return Number.isInteger(value) && value >= 1 ? value : fallback;
}

function isLedgerSort(value: string): value is GeneralLedgerSort {
	return value === GENERAL_LEDGER_DEFAULT_SORT || GENERAL_LEDGER_SORTS.includes(value as (typeof GENERAL_LEDGER_SORTS)[number]);
}

/** Keep interactive GL sorting within the server allowlist, with deterministic newest-first defaulting. */
export function readGeneralLedgerSort(params: URLSearchParams): GeneralLedgerSort {
	const value = readMoneyText(params, 'sort');
	return isLedgerSort(value) ? value : GENERAL_LEDGER_DEFAULT_SORT;
}

export interface GeneralLedgerUrlState {
	accountId: number | null;
	propertyId: number | null;
	unitId: number | null;
	sourceType: string;
	effectiveFrom: string;
	effectiveTo: string;
	search: string;
	page: number;
	sort: GeneralLedgerSort;
}

export function readGeneralLedgerUrlState(params: URLSearchParams): GeneralLedgerUrlState {
	return {
		accountId: readMoneyId(params, 'account'),
		propertyId: readMoneyId(params, 'property'),
		unitId: readMoneyId(params, 'unit'),
		sourceType: readMoneyText(params, 'source'),
		effectiveFrom: readMoneyText(params, 'from'),
		effectiveTo: readMoneyText(params, 'to'),
		search: readMoneyText(params, 'q'),
		page: readMoneyPage(params),
		sort: readGeneralLedgerSort(params)
	};
}

/** Build URL state while preserving unrelated route parameters such as coach marks. */
export function buildMoneyUrlQuery(
	values: Record<string, string | number | null | undefined>,
	defaults: Record<string, string | number> = {},
	base: URLSearchParams = new URLSearchParams()
): string {
	const params = new URLSearchParams(base);
	for (const [key, value] of Object.entries(values)) {
		const omit =
			value == null ||
			value === '' ||
			(Object.prototype.hasOwnProperty.call(defaults, key) && defaults[key] === value);
		if (omit) params.delete(key);
		else params.set(key, String(value));
	}
	return params.toString();
}

export function generalLedgerUrlValues(state: GeneralLedgerUrlState): Record<string, string | number | null> {
	return {
		account: state.accountId,
		property: state.propertyId,
		unit: state.unitId,
		source: state.sourceType,
		from: state.effectiveFrom,
		to: state.effectiveTo,
		q: state.search,
		page: state.page,
		sort: state.sort
	};
}
