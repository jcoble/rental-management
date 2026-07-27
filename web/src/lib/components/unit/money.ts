/** Formats a number as USD currency (matches the DataGrid's currency formatter). */
export function money(value: number | null | undefined): string {
	if (value == null) return '—';
	return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value);
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

export const PAYMENT_CORRECTION_REASON = 'Correction of immutable payment receipt';

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
}): PaymentCorrectionContext {
	return {
		tenantAccountId: receipt.tenantAccountId,
		tenantLedgerEntryId: receipt.tenantLedgerEntryId,
		accountNumber: receipt.accountNumber,
		propertyName: receipt.propertyName,
		unitNumber: receipt.unitNumber,
		tenantName: receipt.tenantName?.trim() || 'Tenant not named',
		amount: receipt.amount,
		effectiveOn: new Date().toISOString().slice(0, 10),
		reason: `${PAYMENT_CORRECTION_REASON}: ${receipt.description}`,
		paymentMethodSummary: receipt.providerAttempt?.paymentMethodSummary?.trim() || '',
		externalReference: receipt.providerAttempt?.providerReference?.trim() || '',
		sourceStoredFileId: receipt.sourceStoredFileId ?? undefined
	};
}
