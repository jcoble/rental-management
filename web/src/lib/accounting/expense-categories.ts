export const EXPENSE_CATEGORIES = [
	'Advertising',
	'AutoTravel',
	'CleaningMaintenance',
	'Commissions',
	'Insurance',
	'LegalProfessional',
	'ManagementFees',
	'MortgageInterest',
	'Repairs',
	'Supplies',
	'Taxes',
	'Utilities',
	'Depreciation',
	'Other'
] as const;

export type ExpenseCategory = (typeof EXPENSE_CATEGORIES)[number];

const EXPENSE_CATEGORY_LABELS: Record<ExpenseCategory, string> = {
	Advertising: 'Advertising',
	AutoTravel: 'Auto & travel',
	CleaningMaintenance: 'Cleaning & maintenance',
	Commissions: 'Commissions',
	Insurance: 'Insurance',
	LegalProfessional: 'Legal & professional fees',
	ManagementFees: 'Management fees',
	MortgageInterest: 'Mortgage interest',
	Repairs: 'Repairs & maintenance',
	Supplies: 'Supplies',
	Taxes: 'Taxes',
	Utilities: 'Utilities',
	Depreciation: 'Depreciation',
	Other: 'Other'
};

export const EXPENSE_CATEGORY_OPTIONS = EXPENSE_CATEGORIES.map((value) => ({
	value,
	label: EXPENSE_CATEGORY_LABELS[value]
}));

export function formatExpenseCategory(value: string | undefined | null): string {
	if (!value) return '';
	return EXPENSE_CATEGORY_LABELS[value as ExpenseCategory] ?? value;
}
