import { api } from '../client';
import type { TenantLedgerDirection } from './tenant-ledgers';

export interface TenantMoneyCommandResponse<T> {
	value: T;
	replayed: boolean;
}

export interface RecordTenantReceiptRequest {
	amount: number;
	effectiveOn: string;
	description: string;
	paymentMethodSummary: string;
	externalReference?: string | null;
	payerName?: string | null;
	checkNumber?: string | null;
	bankName?: string | null;
	sourceStoredFileId?: number | null;
	targetChargeEntryId?: number | null;
	allocateOldestCharges?: boolean;
}

export interface PostTenantChargeRequest {
	amount: number;
	effectiveOn: string;
	dueOn: string;
	description: string;
	sourceStoredFileId?: number | null;
	incomeLedgerAccountId?: number | null;
	servicePeriodStartOn?: string | null;
	servicePeriodEndOn?: string | null;
}

export interface ReverseTenantChargeRequest {
	effectiveOn: string;
	reason: string;
	sourceStoredFileId?: number | null;
}

export interface PostTenantCreditRequest {
	amount: number;
	effectiveOn: string;
	description: string;
	sourceStoredFileId?: number | null;
	allocateOldestCharges?: boolean;
	targetChargeEntryId?: number | null;
	incomeLedgerAccountId?: number | null;
}

export interface PostTenantAdjustmentRequest {
	direction: TenantLedgerDirection;
	amount: number;
	effectiveOn: string;
	description: string;
	sourceStoredFileId?: number | null;
}

export interface ReverseTenantLedgerEntryRequest {
	reversesEntryId: number;
	effectiveOn: string;
	reason: string;
	sourceStoredFileId?: number | null;
}

export interface RefundTenantPaymentRequest {
	paymentEntryId: number;
	effectiveOn: string;
	reason: string;
	paymentMethodSummary?: string | null;
	externalReference?: string | null;
	sourceStoredFileId?: number | null;
}

export interface RecordTenantReceiptResult {
	found: boolean;
	tenantAccountId: number;
	ledgerEntryId: number;
	paymentAttemptId: number;
	amount: number;
	allocatedAmount: number;
	allocationCount: number;
}

export interface TenantChargeMutationResult {
	found: boolean;
	applied: boolean;
	tenantAccountId: number;
	ledgerEntryId: number;
	reversesEntryId: number | null;
	amount: number;
	error: string | null;
}

export interface TenantLedgerMutationResult {
	found: boolean;
	applied: boolean;
	tenantAccountId: number;
	ledgerEntryId: number;
	reversesEntryId: number | null;
	entryType: string;
	direction: TenantLedgerDirection;
	amount: number;
	allocatedAmount: number;
	allocationCount: number;
	error: string | null;
}

export type TenantPaymentRefundOutcome =
	| 'Refunded'
	| 'AlreadyRefunded'
	| 'ExternalCorrectionUnavailable';

export interface TenantPaymentRefundResult {
	found: boolean;
	applied: boolean;
	outcome: TenantPaymentRefundOutcome;
	tenantAccountId: number;
	paymentEntryId: number;
	refundEntryId: number | null;
	providerPaymentAttemptId: number | null;
	amount: number;
	compensatedAllocationAmount: number;
	compensatedAllocationCount: number;
	error: string | null;
}

function mutationOptions(operationKey: string): RequestInit {
	return { headers: { 'Idempotency-Key': operationKey } };
}

export const tenantMoney = {
	recordReceipt: (
		tenantAccountId: number,
		operationKey: string,
		body: RecordTenantReceiptRequest
	) =>
		api.post<TenantMoneyCommandResponse<RecordTenantReceiptResult>>(
			`/tenant-accounts/${tenantAccountId}/receipts`,
			body,
			mutationOptions(operationKey)
		),
	postCharge: (
		tenantAccountId: number,
		operationKey: string,
		body: PostTenantChargeRequest
	) =>
		api.post<TenantMoneyCommandResponse<TenantChargeMutationResult>>(
			`/tenant-accounts/${tenantAccountId}/charges`,
			body,
			mutationOptions(operationKey)
		),
	reverseCharge: (
		tenantAccountId: number,
		chargeEntryId: number,
		operationKey: string,
		body: ReverseTenantChargeRequest
	) =>
		api.post<TenantMoneyCommandResponse<TenantChargeMutationResult>>(
			`/tenant-accounts/${tenantAccountId}/charges/${chargeEntryId}/reversals`,
			body,
			mutationOptions(operationKey)
		),
	postCredit: (
		tenantAccountId: number,
		operationKey: string,
		body: PostTenantCreditRequest
	) =>
		api.post<TenantMoneyCommandResponse<TenantLedgerMutationResult>>(
			`/tenant-accounts/${tenantAccountId}/credits`,
			body,
			mutationOptions(operationKey)
		),
	postAdjustment: (
		tenantAccountId: number,
		operationKey: string,
		body: PostTenantAdjustmentRequest
	) =>
		api.post<TenantMoneyCommandResponse<TenantLedgerMutationResult>>(
			`/tenant-accounts/${tenantAccountId}/adjustments`,
			body,
			mutationOptions(operationKey)
		),
	reverseLedgerEntry: (
		tenantAccountId: number,
		operationKey: string,
		body: ReverseTenantLedgerEntryRequest
	) =>
		api.post<TenantMoneyCommandResponse<TenantLedgerMutationResult>>(
			`/tenant-accounts/${tenantAccountId}/reversals`,
			body,
			mutationOptions(operationKey)
		),
	refundPayment: (
		tenantAccountId: number,
		operationKey: string,
		body: RefundTenantPaymentRequest
	) =>
		api.post<TenantMoneyCommandResponse<TenantPaymentRefundResult>>(
			`/tenant-accounts/${tenantAccountId}/refunds`,
			body,
			mutationOptions(operationKey)
		)
};
