import { businessDateOrToday } from '../../utils/business-date.ts';

export const TENANT_CHARGE_TYPES = [
	{ value: 'Rent', label: 'Rent', systemKey: 'rental-income' },
	{ value: 'Late fee', label: 'Late fee', systemKey: 'late-fee-income' },
	{ value: 'Utility', label: 'Utility', systemKey: 'utility-reimbursement-income' },
	{ value: 'Damage', label: 'Damage', systemKey: 'other-rental-income' },
	{ value: 'Pet', label: 'Pet', systemKey: 'pet-income' },
	{ value: 'Parking', label: 'Parking', systemKey: 'parking-income' },
	{ value: 'Storage', label: 'Storage', systemKey: 'other-rental-income' },
	{ value: 'Cleaning', label: 'Cleaning', systemKey: 'other-rental-income' },
	{ value: 'Returned payment', label: 'Returned payment', systemKey: 'late-fee-income' },
	{ value: 'Other', label: 'Other', systemKey: null }
] as const;

export type TenantChargeType = (typeof TENANT_CHARGE_TYPES)[number]['value'];

export function tenantChargeType(value: string): (typeof TENANT_CHARGE_TYPES)[number] {
	return TENANT_CHARGE_TYPES.find((option) => option.value === value) ?? TENANT_CHARGE_TYPES[0];
}

export function tenantLedgerPeriodRange(
	months: 3 | 6 | 9 | 12,
	anchor = new Date()
): { from: string; to: string } {
	const year = anchor.getUTCFullYear();
	const month = anchor.getUTCMonth();
	const from = new Date(Date.UTC(year, month - months + 1, 1));
	return {
		from: from.toISOString().slice(0, 10),
		to: new Date(Date.UTC(year, month + 1, 0)).toISOString().slice(0, 10)
	};
}

export interface UnitMoneyIdentity {
	tenantAccountId: number | null;
	leaseManagementId: number | null;
}

export function unitMoneyIdentity(source: {
	tenantAccountId?: number | null;
	leaseManagementId?: number | null;
}): UnitMoneyIdentity {
	return {
		tenantAccountId: source.tenantAccountId ?? null,
		leaseManagementId: source.leaseManagementId ?? null
	};
}

export function unitMoneySectionGates(source: {
	rentalStructure?: 'SingleRental' | 'MultiRental' | null;
	propertyId?: number | null;
	tenantAccountId?: number | null;
}): {
	tenantAccount: boolean;
	propertyExpenses: boolean;
	financing: boolean;
} {
	const persistedSingleRental = source.rentalStructure === 'SingleRental' && (source.propertyId ?? 0) > 0;
	return {
		tenantAccount: (source.tenantAccountId ?? 0) > 0,
		propertyExpenses: persistedSingleRental,
		financing: persistedSingleRental
	};
}

export function canCorrectPayment(
	capabilities: ReadonlySet<string>,
	entryType: string | null | undefined
): boolean {
	return entryType === 'PaymentReceipt' && capabilities.has('money.payments.manage');
}

export function canReverseTenantLedgerEntry(entry: {
	entryType?: string | null;
	reversesEntryId?: number | null;
	hasReversal?: boolean;
	providerPaymentAttemptId?: number | null;
}): boolean {
	if (!entry.entryType) return false;
	if (entry.reversesEntryId != null || entry.hasReversal || entry.providerPaymentAttemptId != null) {
		return false;
	}
	return !['PaymentReceipt', 'Reversal', 'Refund', 'TransferIn', 'TransferOut'].includes(entry.entryType);
}

export function tenantLedgerReversalReason(entry: {
	entryType?: string | null;
	description?: string | null;
}): string {
	const type = entry.entryType?.trim() || 'ledger entry';
	const description = entry.description?.trim();
	const reason = description ? `Reverse ${type}: ${description}` : `Reverse ${type}`;
	return reason.length > 500 ? reason.slice(0, 500) : reason;
}

export const PAYMENT_CORRECTION_REASON = 'Correction of original payment receipt';

export interface PaymentCorrectionContext {
	tenantAccountId: number;
	tenantLedgerEntryId: number;
	accountNumber: string;
	propertyName: string;
	unitNumber: string;
	tenantName: string;
	amount: number;
	effectiveOn: string;
	reason: string;
	paymentMethodSummary: string;
	externalReference: string;
	sourceStoredFileId?: number;
}

export function paymentCorrectionContext(receipt: {
	tenantAccountId: number;
	tenantLedgerEntryId: number;
	accountNumber: string;
	propertyName: string;
	unitNumber: string;
	tenantName?: string | null;
	amount: number;
	effectiveOn: string;
	description: string;
	providerAttempt?: {
		paymentMethodSummary?: string | null;
		providerReference?: string | null;
	} | null;
	sourceStoredFileId?: number | null;
}, businessDate?: string | null): PaymentCorrectionContext {
	return {
		tenantAccountId: receipt.tenantAccountId,
		tenantLedgerEntryId: receipt.tenantLedgerEntryId,
		accountNumber: receipt.accountNumber,
		propertyName: receipt.propertyName,
		unitNumber: receipt.unitNumber,
		tenantName: receipt.tenantName?.trim() || 'Tenant not named',
		amount: receipt.amount,
		effectiveOn: businessDateOrToday(businessDate),
		reason: `${PAYMENT_CORRECTION_REASON}: ${receipt.description}`,
		paymentMethodSummary: receipt.providerAttempt?.paymentMethodSummary?.trim() || '',
		externalReference: receipt.providerAttempt?.providerReference?.trim() || '',
		sourceStoredFileId: receipt.sourceStoredFileId ?? undefined
	};
}
