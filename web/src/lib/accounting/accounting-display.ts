import type {
	AccountType,
	JournalSourceType,
	NormalBalance
} from '$lib/api/endpoints/accounting-books';
import { sentenceCaseIdentifier } from './money-display.ts';

export type AccountingEntrySide = 'debit' | 'credit';
export type AccountingChangeKind = 'increase' | 'decrease';

export interface SimpleAccountGroupInput {
	accountType: AccountType;
	name?: string | null;
	code?: string | null;
	systemKey?: string | null;
}

export interface IncreaseDecreaseAmounts {
	increase: number;
	decrease: number;
}

const SOURCE_TYPE_LABELS: Record<JournalSourceType, string> = {
	TenantCharge: 'Tenant charge',
	TenantReceipt: 'Payment received',
	ProviderSettlement: 'Provider settlement',
	TenantConcession: 'Tenant credit',
	ReceivableWriteOff: 'Rent owed written off',
	SecurityDepositReceipt: 'Security deposit received',
	SecurityDepositRefund: 'Security deposit refund',
	SecurityDepositApplication: 'Security deposit applied',
	ExpensePayment: 'Expense paid',
	BillIncurred: 'Bill recorded',
	BillPayment: 'Bill paid',
	BankTransfer: 'Bank transfer',
	LoanPayment: 'Loan payment',
	CapitalPurchase: 'Capital purchase',
	Depreciation: 'Depreciation',
	OwnerContribution: 'Owner contribution',
	OwnerDistribution: 'Owner distribution',
	OpeningBalance: 'Opening balance'
};

function validDate(value: string | Date | null | undefined): Date | null {
	if (!value) return null;
	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? null : date;
}

function validCurrencyCode(value: string | null | undefined): string {
	const normalized = value?.trim().toUpperCase();
	return normalized && /^[A-Z]{3}$/.test(normalized) ? normalized : 'USD';
}

/**
 * Format a server-provided amount. This function never computes an amount.
 * Pass `wholeDollars` for the summary tiles that deliberately drop the cents.
 */
export function formatAccountingCurrency(
	value: number | null | undefined,
	currency = 'USD',
	wholeDollars = false
): string {
	if (value == null || !Number.isFinite(value)) return '—';
	const digits = wholeDollars ? 0 : 2;
	return new Intl.NumberFormat('en-US', {
		style: 'currency',
		currency: validCurrencyCode(currency),
		minimumFractionDigits: digits,
		maximumFractionDigits: digits
	}).format(value);
}

/** Format an accounting calendar date without shifting UTC-midnight date-only values. */
export function formatAccountingDate(value: string | Date | null | undefined): string {
	const date = validDate(value);
	if (!date) return '—';
	return date.toLocaleDateString('en-US', {
		month: 'short',
		day: 'numeric',
		year: 'numeric',
		timeZone: 'UTC'
	});
}

/** Format an accounting timestamp in the viewer's local time zone. */
export function formatAccountingDateTime(value: string | Date | null | undefined): string {
	const date = validDate(value);
	if (!date) return '—';
	const datePart = date.toLocaleDateString('en-US', {
		month: 'short',
		day: 'numeric',
		year: 'numeric'
	});
	const timePart = date.toLocaleTimeString('en-US', {
		hour: 'numeric',
		minute: '2-digit'
	});
	return `${datePart}, ${timePart}`;
}

export function formatSourceTypeLabel(
	value: JournalSourceType | string | null | undefined
): string {
	if (!value) return '—';
	return SOURCE_TYPE_LABELS[value as JournalSourceType] ?? (sentenceCaseIdentifier(value) || '—');
}

export function formatAccountTypeLabel(value: AccountType | null | undefined): string {
	if (!value) return '—';
	return value;
}

/** Group account types using the normal-user vocabulary from the accounting mockup. */
export function formatSimpleAccountGroupLabel(
	input: AccountType | SimpleAccountGroupInput | null | undefined
): string {
	if (!input) return '—';
	const account = typeof input === 'string' ? { accountType: input } : input;
	const searchable = `${account.name ?? ''} ${account.systemKey ?? ''} ${account.code ?? ''}`.toLowerCase();

	if (account.accountType === 'Income') return 'Income';
	if (account.accountType === 'Expense') return 'Expenses';
	if (account.accountType === 'Liability') return 'Money you owe';
	if (account.accountType === 'Equity') return 'Equity';
	if (account.accountType === 'Asset' && /receivable|rent owed|tenant account/.test(searchable)) {
		return 'Money owed to you';
	}
	if (account.accountType === 'Asset') return 'Cash & bank';
	return sentenceCaseIdentifier(String(account.accountType)) || '—';
}

export function formatAccountPickerLabel(
	account: { code: string; name: string; accountType: AccountType },
	advanced: boolean
): string {
	if (!advanced) return account.name;
	return `${account.code} — ${account.name} · ${formatAccountTypeLabel(account.accountType)}`;
}

export function formatSourceRecordLabel(
	sourceType: JournalSourceType | string | null | undefined,
	sourceId: number | null | undefined
): string {
	const label = formatSourceTypeLabel(sourceType);
	return sourceId == null ? label : `${label} #${sourceId}`;
}

export function formatJournalLineSide(side: AccountingEntrySide): string {
	return side === 'debit' ? 'Debit' : 'Credit';
}

export interface JournalLineDisplayInput {
	accountName: string;
	accountCode?: string;
	accountType?: AccountType;
	systemKey?: string | null;
	debitAmount: number;
	creditAmount: number;
	normalBalance?: NormalBalance;
	currency?: string;
	effectiveOn?: string | Date | null;
	sourceType?: JournalSourceType | string | null;
}

export function getJournalLineEntry(input: JournalLineDisplayInput): {
	side: AccountingEntrySide;
	amount: number;
} {
	if (input.debitAmount !== 0 || input.creditAmount === 0) {
		return { side: 'debit', amount: input.debitAmount };
	}
	return { side: 'credit', amount: input.creditAmount };
}

export function formatSimpleJournalLineLabel(input: JournalLineDisplayInput): string {
	const entry = getJournalLineEntry(input);
	const amount = formatLandlordAmount(entry.amount, input.currency);
	const isReceivable = input.systemKey === 'tenant-accounts-receivable';
	if (isReceivable) {
		return `Tenant now owes ${amount} ${entry.side === 'debit' ? 'more' : 'less'}`;
	}

	const incomeLabel = input.systemKey === 'rental-income'
		? 'rent earned'
		: input.systemKey === 'late-fee-income'
			? 'late fees earned'
			: input.systemKey === 'other-rental-income'
				? 'other rental income earned'
				: input.accountType === 'Income'
					? 'income earned'
					: null;
	if (incomeLabel) {
		const period = formatAccountingMonthYear(input.effectiveOn);
		return `Counted as ${incomeLabel}${period ? ` for ${period}` : ''}`;
	}

	if (input.accountType === 'Asset' && input.sourceType === 'TenantReceipt') {
		return `Money received: ${amount}`;
	}
	if (input.accountType === 'Liability') {
		return `Tenant-held balance changed by ${amount}`;
	}
	return `Accounting amount ${amount}`;
}

function formatLandlordAmount(value: number, currency = 'USD'): string {
	if (!Number.isFinite(value)) return '—';
	return new Intl.NumberFormat('en-US', {
		style: 'currency',
		currency: validCurrencyCode(currency),
		minimumFractionDigits: Number.isInteger(value) ? 0 : 2,
		maximumFractionDigits: 2
	}).format(value);
}

function formatAccountingMonthYear(value: string | Date | null | undefined): string | null {
	const date = validDate(value);
	if (!date) return null;
	return date.toLocaleDateString('en-US', {
		month: 'long',
		year: 'numeric',
		timeZone: 'UTC'
	});
}

export function accountingChangeForEntry(
	normalBalance: NormalBalance,
	side: AccountingEntrySide
): AccountingChangeKind {
	return normalBalance === (side === 'debit' ? 'Debit' : 'Credit') ? 'increase' : 'decrease';
}

export function formatChangeLabel(change: AccountingChangeKind): string {
	return change === 'increase' ? 'increased' : 'decreased';
}

/** Place server debit/credit fields into simple increase/decrease columns. */
export function getIncreaseDecreaseAmounts(input: {
	normalBalance: NormalBalance;
	debitAmount: number;
	creditAmount: number;
}): IncreaseDecreaseAmounts {
	if (input.normalBalance === 'Debit') {
		return { increase: input.debitAmount, decrease: input.creditAmount };
	}
	return { increase: input.creditAmount, decrease: input.debitAmount };
}

export function formatBalancedStatus(value: boolean | null | undefined): string {
	if (value == null) return '—';
	return value ? 'Yes' : 'No';
}

export function formatStatementTotalLabel(sectionLabel: string): string {
	return `Total ${sectionLabel.toLowerCase()}`;
}

export function formatStatementStatusLabel(balanced: boolean): string {
	return balanced ? 'Balanced' : 'Not balanced';
}

export function formatJournalPostingLabel(isReversal: boolean): string {
	return isReversal ? 'Correction' : 'Posted';
}

export function accountingAmountClass(value: number | null): string {
	return value != null && value < 0 ? 'text-destructive' : 'text-foreground';
}
