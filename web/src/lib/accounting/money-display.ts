const MONEY_ENTRY_LABELS: Record<string, string> = {
	ApplicationFee: 'Application fee',
	Bank: 'Bank activity',
	AddendumCharge: 'Lease/addendum charge',
	Charge: 'Charge',
	Credit: 'Credit',
	Deposit: 'Bank deposit',
	DepositCharge: 'Security deposit',
	Expense: 'Expense',
	LateFee: 'Late fee',
	LateFeeCharge: 'Late fee',
	Payment: 'Payment received',
	PaymentReceipt: 'Payment received',
	PaymentReversal: 'Payment correction',
	Receipt: 'Payment received',
	Refund: 'Refund',
	Rent: 'Rent',
	RentCharge: 'Rent charge',
	SecurityDeposit: 'Security deposit',
	TenantLedger: 'Rent or resident charge',
	Utility: 'Utilities',
	Withdrawal: 'Bank withdrawal',
};

const MONEY_CATEGORY_LABELS: Record<string, string> = {
	...MONEY_ENTRY_LABELS,
	AutoTravel: 'Auto & travel',
	CleaningMaintenance: 'Cleaning & maintenance',
	LegalProfessional: 'Legal & professional fees',
	ManagementFees: 'Management fees',
	ManualCharge: 'Manual/other charge',
	MortgageInterest: 'Mortgage interest',
	Repairs: 'Repairs & maintenance',
};

function sentenceCaseIdentifier(value: string): string {
	const words = value
		.replace(/[_-]+/g, ' ')
		.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
		.replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
		.trim();

	if (!words) return '';
	return words.charAt(0).toUpperCase() + words.slice(1).toLowerCase();
}

export function formatMoneyEntryLabel(value: string | undefined | null): string {
	if (!value) return '';
	return MONEY_ENTRY_LABELS[value] ?? sentenceCaseIdentifier(value);
}

export function formatMoneyCategoryLabel(value: string | undefined | null): string {
	if (!value) return '';
	return MONEY_CATEGORY_LABELS[value] ?? sentenceCaseIdentifier(value);
}

export function formatRentalLocation(input: {
	propertyName?: string | null;
	unitNumber?: string | number | null;
}): string {
	const property = input.propertyName?.trim() || 'Property';
	const unit = String(input.unitNumber ?? '').trim();
	return unit ? `${property} · Unit ${unit}` : property;
}

export function formatResidentName(value: string | undefined | null): string {
	return value?.trim() || 'Tenant not listed';
}
