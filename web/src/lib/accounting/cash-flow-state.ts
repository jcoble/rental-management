import type {
	CashFlowSummaryResponse,
	PropertyCashFlow
} from '$lib/api/endpoints/cash-flow';

export type CashFlowPeriodPreset = 'thisMonth' | 'lastMonth' | 'ytd' | 'trailing12' | 'custom';

export const CASH_FLOW_PERIOD_PRESETS = [
	{ key: 'thisMonth', label: 'This month' },
	{ key: 'lastMonth', label: 'Last month' },
	{ key: 'ytd', label: 'YTD' },
	{ key: 'trailing12', label: 'Trailing 12' },
	{ key: 'custom', label: 'Custom' }
] as const satisfies ReadonlyArray<{ key: CashFlowPeriodPreset; label: string }>;

export interface CashFlowRange {
	from: string;
	to: string;
}

export interface CashFlowMonthlyRange extends CashFlowRange {
	key: string;
	label: string;
}

export interface CashFlowCustomRange {
	from?: string;
	to?: string;
}

export type CashFlowPropertySortKey =
	| 'propertyName'
	| 'income'
	| 'operatingExpenses'
	| 'debtService'
	| 'cashFlow';

export type CashFlowSortDirection = 'asc' | 'desc';

export interface CashFlowPropertyDetail {
	key: 'operatingExpenses' | 'debtService';
	amount: number;
}

export interface CashFlowMonthlyResponse {
	range: CashFlowMonthlyRange;
	response: CashFlowSummaryResponse;
}

export interface CashFlowChartPoint {
	key: string;
	label: string;
	income: number;
	operatingExpenses: number;
	debtService: number;
	cashFlow: number;
}

export type CashFlowChartMetric =
	| 'income'
	| 'operatingExpenses'
	| 'debtService'
	| 'cashFlow';

const MONTH_LABELS = [
	'Jan',
	'Feb',
	'Mar',
	'Apr',
	'May',
	'Jun',
	'Jul',
	'Aug',
	'Sep',
	'Oct',
	'Nov',
	'Dec'
] as const;

interface CalendarDateParts {
	year: number;
	month: number;
	day: number;
}

function pad(value: number): string {
	return String(value).padStart(2, '0');
}

function toIsoDate(parts: CalendarDateParts): string {
	return `${parts.year}-${pad(parts.month)}-${pad(parts.day)}`;
}

function toMonthKey(year: number, month: number): string {
	return `${year}-${pad(month)}`;
}

function parseDateOnly(value: string): CalendarDateParts | null {
	const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
	if (!match) return null;

	const parts = {
		year: Number(match[1]),
		month: Number(match[2]),
		day: Number(match[3])
	};
	const date = new Date(Date.UTC(parts.year, parts.month - 1, parts.day));
	if (
		date.getUTCFullYear() !== parts.year ||
		date.getUTCMonth() + 1 !== parts.month ||
		date.getUTCDate() !== parts.day
	) {
		return null;
	}
	return parts;
}

function calendarDate(value: Date): CalendarDateParts {
	return {
		year: value.getFullYear(),
		month: value.getMonth() + 1,
		day: value.getDate()
	};
}

function shiftMonth(year: number, month: number, offset: number): { year: number; month: number } {
	const shifted = new Date(Date.UTC(year, month - 1 + offset, 1));
	return { year: shifted.getUTCFullYear(), month: shifted.getUTCMonth() + 1 };
}

function daysInMonth(year: number, month: number): number {
	return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

function isOrderedRange(range: CashFlowRange): boolean {
	return Boolean(
		range.from &&
		range.to &&
		parseDateOnly(range.from) &&
		parseDateOnly(range.to) &&
		range.from <= range.to
	);
}

/** Resolve a display preset into date-only API filters. This performs no money calculation. */
export function resolveCashFlowRange(
	preset: CashFlowPeriodPreset,
	now = new Date(),
	custom: CashFlowCustomRange = {}
): CashFlowRange | null {
	if (preset === 'custom') {
		const range = { from: custom.from ?? '', to: custom.to ?? '' };
		return isOrderedRange(range) ? range : null;
	}

	const today = calendarDate(now);
	const todayString = toIsoDate(today);

	if (preset === 'thisMonth') {
		return { from: toIsoDate({ ...today, day: 1 }), to: todayString };
	}

	if (preset === 'lastMonth') {
		const previous = shiftMonth(today.year, today.month, -1);
		return {
			from: toIsoDate({ year: previous.year, month: previous.month, day: 1 }),
			to: toIsoDate({
				year: previous.year,
				month: previous.month,
				day: daysInMonth(previous.year, previous.month)
			})
		};
	}

	if (preset === 'ytd') {
		return { from: `${today.year}-01-01`, to: todayString };
	}

	const trailingStart = shiftMonth(today.year, today.month, -11);
	return {
		from: toIsoDate({ year: trailingStart.year, month: trailingStart.month, day: 1 }),
		to: todayString
	};
}

/** Split a selected period into server-queryable calendar-month slices for the chart. */
export function buildCashFlowMonthlyRanges(range: CashFlowRange | null): CashFlowMonthlyRange[] {
	if (!range || !isOrderedRange(range)) return [];

	const from = parseDateOnly(range.from);
	const to = parseDateOnly(range.to);
	if (!from || !to) return [];

	const ranges: CashFlowMonthlyRange[] = [];
	let cursor = { year: from.year, month: from.month };
	while (
		cursor.year < to.year ||
		(cursor.year === to.year && cursor.month <= to.month)
	) {
		const key = toMonthKey(cursor.year, cursor.month);
		const monthStart = toIsoDate({ year: cursor.year, month: cursor.month, day: 1 });
		const monthEnd = toIsoDate({
			year: cursor.year,
			month: cursor.month,
			day: daysInMonth(cursor.year, cursor.month)
		});
		ranges.push({
			key,
			label: `${MONTH_LABELS[cursor.month - 1]} ${cursor.year}`,
			from: cursor.year === from.year && cursor.month === from.month ? range.from : monthStart,
			to: cursor.year === to.year && cursor.month === to.month ? range.to : monthEnd
		});
		cursor = shiftMonth(cursor.year, cursor.month, 1);
	}

	return ranges;
}

/** Sort only on server-provided property fields; the original response rows remain untouched. */
export function sortCashFlowProperties(
	properties: readonly PropertyCashFlow[],
	key: CashFlowPropertySortKey = 'cashFlow',
	direction: CashFlowSortDirection = 'desc'
): PropertyCashFlow[] {
	const sorted = [...properties];
	sorted.sort((left, right) => {
		if (key === 'propertyName') {
			return left.propertyName.localeCompare(right.propertyName);
		}

		const leftValue = left[key];
		const rightValue = right[key];
		if (leftValue === rightValue) return left.propertyName.localeCompare(right.propertyName);
		const comparison = leftValue < rightValue ? -1 : 1;
		return direction === 'asc' ? comparison : -comparison;
	});
	return sorted;
}

/** Map the endpoint's existing per-property fields into the two expandable detail rows. */
export function cashFlowPropertyDetails(property: PropertyCashFlow): CashFlowPropertyDetail[] {
	return [
		{ key: 'operatingExpenses', amount: property.operatingExpenses },
		{ key: 'debtService', amount: property.debtService }
	];
}

export function buildServerCashFlowChartPoints(response: CashFlowSummaryResponse): CashFlowChartPoint[] {
	return (response.months ?? []).map((month) => ({ key: month.month, label: month.month, income: month.income,
		operatingExpenses: month.operatingExpenses, debtService: month.debtService, cashFlow: month.cashFlow }));
}

/** Keep chart points as a direct projection of one server response per month. */
export function buildCashFlowChartPoints(
	monthlyResponses: readonly CashFlowMonthlyResponse[]
): CashFlowChartPoint[] {
	return monthlyResponses.map(({ range, response }) => ({
		key: range.key,
		label: range.label,
		income: response.totalIncome,
		operatingExpenses: response.totalOperatingExpenses,
		debtService: response.totalDebtService,
		cashFlow: response.totalCashFlow
	}));
}

/**
 * Visual-only chart scale. It changes bar geometry, not a displayed financial amount or total.
 */
export function cashFlowChartScale(points: readonly CashFlowChartPoint[]): number {
	let maximum = 0;
	for (const point of points) {
		for (const metric of ['income', 'operatingExpenses', 'debtService', 'cashFlow'] as const) {
			maximum = Math.max(maximum, Math.abs(point[metric]));
		}
	}
	return maximum || 1;
}

/** Return a bounded percentage used only for chart geometry. */
export function cashFlowBarPercent(value: number, scale: number): number {
	if (!Number.isFinite(value) || !Number.isFinite(scale) || scale <= 0) return 0;
	return Math.min(100, Math.max(0, (Math.abs(value) / scale) * 100));
}

/** Return a bounded vertical position for the net-flow marker around the zero baseline. */
export function cashFlowNetPositionPercent(value: number, scale: number): number {
	if (!Number.isFinite(value) || !Number.isFinite(scale) || scale <= 0) return 50;
	return Math.min(95, Math.max(5, 50 + (value / scale) * 45));
}
