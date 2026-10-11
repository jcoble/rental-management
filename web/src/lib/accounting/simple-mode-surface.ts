/**
 * What the Money screens show at each detail level.
 *
 * Simple is the landlord view: the four tabs that answer "who paid me and what did I spend",
 * landing on Rent & payments. Advanced keeps every tab, including the general ledger.
 * Reports split the same way — three everyday reports up front, everything an accountant
 * asks for tucked behind a group the landlord can open when they need it.
 */
import type { MoneyTab } from './global-money-state.ts';

export type AccountingDetailLevel = 'simple' | 'advanced';

export interface AccountingTabOption {
	value: MoneyTab;
	label: string;
}

export const ACCOUNTING_TABS: readonly AccountingTabOption[] = [
	{ value: 'overview', label: 'Overview' },
	{ value: 'cash-flow', label: 'Cash flow' },
	{ value: 'activity', label: 'Activity' },
	{ value: 'rent-payments', label: 'Rent & payments' },
	{ value: 'general-ledger', label: 'General ledger' },
	{ value: 'reports', label: 'Reports' }
];

/** Simple order puts the tab you land on first. */
const SIMPLE_TABS: readonly MoneyTab[] = ['rent-payments', 'overview', 'activity', 'reports'];

export function visibleAccountingTabs(mode: AccountingDetailLevel): AccountingTabOption[] {
	if (mode === 'advanced') return [...ACCOUNTING_TABS];
	return SIMPLE_TABS.flatMap((value) => ACCOUNTING_TABS.filter((tab) => tab.value === value));
}

export function defaultTabForMode(mode: AccountingDetailLevel): MoneyTab {
	return mode === 'advanced' ? 'overview' : 'rent-payments';
}

/** Keep an old link to a hidden tab from selecting nothing: fall back to this level's landing tab. */
export function resolveAccountingTab(
	requested: MoneyTab | null | undefined,
	mode: AccountingDetailLevel
): MoneyTab {
	const visible = visibleAccountingTabs(mode);
	return requested && visible.some((tab) => tab.value === requested)
		? requested
		: defaultTabForMode(mode);
}

/** The three reports a landlord opens week to week. */
export const EVERYDAY_REPORT_KEYS = ['rent-roll', 'delinquency', 'income-expense-statement'];

const EVERYDAY_KEY_SET = new Set(EVERYDAY_REPORT_KEYS);

export function groupReports<T extends { key: string }>(
	catalog: { categories?: { reports?: T[] }[] } | null | undefined
): { everyday: T[]; accountant: T[] } {
	const all = (catalog?.categories ?? []).flatMap((category) => category.reports ?? []);
	return {
		everyday: EVERYDAY_REPORT_KEYS.flatMap((key) => all.filter((report) => report.key === key)),
		accountant: all.filter((report) => !EVERYDAY_KEY_SET.has(report.key))
	};
}
