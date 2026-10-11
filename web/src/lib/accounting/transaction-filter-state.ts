import { EXPENSE_CATEGORY_OPTIONS } from './expense-categories.ts';

export type TransactionCategoryOption = { value: string; label: string };

export const TRANSACTION_KIND_OPTIONS = [
	{ value: 'Payment', label: 'Payments' },
	{ value: 'Expense', label: 'Expenses' },
] as const;

// Payment rows are projected from PaymentReceipt ledger entries, whose database invariant fixes
// Direction/Status to Credit. Do not offer lifecycle labels that the transaction SQL never emits.
export const PAYMENT_STATUSES = ['Credit'] as const;
export const EXPENSE_STATUSES = ['Pending', 'Approved', 'Paid'] as const;

export const TENANT_MONEY_CATEGORY_OPTIONS: TransactionCategoryOption[] = [
	{ value: 'RentCharge', label: 'Rent' },
	{ value: 'DepositCharge', label: 'Security deposit' },
	{ value: 'LateFeeCharge', label: 'Late fee' },
	{ value: 'AddendumCharge', label: 'Lease/addendum charge' },
	{ value: 'ManualCharge', label: 'Manual/other charge' }
];

function uniqueOptions(options: TransactionCategoryOption[]): TransactionCategoryOption[] {
	const seen = new Set<string>();
	return options.filter((option) => {
		if (seen.has(option.value)) return false;
		seen.add(option.value);
		return true;
	});
}

const ALL_CATEGORY_OPTIONS = uniqueOptions([
	...TENANT_MONEY_CATEGORY_OPTIONS,
	...EXPENSE_CATEGORY_OPTIONS
]);

export function normalizeTransactionKind(kind: string): string {
	return TRANSACTION_KIND_OPTIONS.some((option) => option.value === kind) ? kind : '';
}

export function transactionStatusesForKind(kind: string): readonly string[] {
	if (kind === 'Payment') return PAYMENT_STATUSES;
	if (kind === 'Expense') return EXPENSE_STATUSES;
	if (kind === '') return uniqueStrings([...PAYMENT_STATUSES, ...EXPENSE_STATUSES]);
	return [];
}

function uniqueStrings(values: string[]): string[] {
	return [...new Set(values)];
}

export function transactionCategoriesForKind(kind: string): readonly TransactionCategoryOption[] {
	if (kind === 'Payment') return TENANT_MONEY_CATEGORY_OPTIONS;
	if (kind === 'Expense') return EXPENSE_CATEGORY_OPTIONS;
	if (kind === '') return ALL_CATEGORY_OPTIONS;
	return [];
}

export function normalizeTransactionFilters(
	kind: string,
	status: string,
	category: string
): { status: string; category: string } {
	const statuses = transactionStatusesForKind(kind);
	const categories = transactionCategoriesForKind(kind);
	return {
		status: status && statuses.includes(status) ? status : '',
		category: category && categories.some((option) => option.value === category) ? category : ''
	};
}
