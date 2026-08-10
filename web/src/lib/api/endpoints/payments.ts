import { fetchApi } from '../client';

export interface RecordTenantReceiptRequest {
	amount: number;
	effectiveOn: string;
	description: string;
	paymentMethodSummary: string;
	externalReference?: string;
	payerName?: string;
	checkNumber?: string;
	bankName?: string;
	sourceStoredFileId?: number;
	targetChargeEntryId?: number | null;
	allocateOldestCharges?: boolean;
}

export interface PostTenantChargeRequest {
	amount: number;
	effectiveOn: string;
	dueOn: string;
	description: string;
	sourceStoredFileId?: number;
}

export interface ReverseTenantChargeRequest {
	effectiveOn: string;
	reason: string;
	sourceStoredFileId?: number;
}

export interface RefundTenantPaymentRequest {
	paymentEntryId: number;
	effectiveOn: string;
	reason: string;
	paymentMethodSummary?: string;
	externalReference?: string;
	sourceStoredFileId?: number;
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
	refundEntryId?: number | null;
	providerPaymentAttemptId?: number | null;
	amount: number;
	compensatedAllocationAmount: number;
	compensatedAllocationCount: number;
	error?: string | null;
}

export interface TenantPaymentRefundRequestSpec {
	path: string;
	options: RequestInit;
}

export interface TenantPaymentRefundConflict {
	outcome: 'AlreadyRefunded' | 'ExternalCorrectionUnavailable';
	error?: string | null;
}

export interface TenantMoneyCommandResponse<T> {
	value: T;
	replayed: boolean;
}

export function buildTenantPaymentRefundRequest(
	tenantAccountId: number,
	operationKey: string,
	body: RefundTenantPaymentRequest
): TenantPaymentRefundRequestSpec {
	return {
		path: `/tenant-accounts/${tenantAccountId}/refunds`,
		options: {
			method: 'POST',
			headers: { 'Content-Type': 'application/json', 'Idempotency-Key': operationKey },
			body: JSON.stringify(body)
		}
	};
}

export function linkedTenantPaymentRefund(
	response: TenantMoneyCommandResponse<TenantPaymentRefundResult>
): {
	refundEntryId: number;
	compensatedAllocationAmount: number;
	compensatedAllocationCount: number;
	replayed: boolean;
} | null {
	const result = response.value;
	if (!result.applied || result.outcome !== 'Refunded' || result.refundEntryId == null) return null;
	return {
		refundEntryId: result.refundEntryId,
		compensatedAllocationAmount: result.compensatedAllocationAmount,
		compensatedAllocationCount: result.compensatedAllocationCount,
		replayed: response.replayed
	};
}

export function isTenantPaymentRefundConflict(value: unknown): value is TenantPaymentRefundConflict {
	if (!value || typeof value !== 'object') return false;
	const outcome = (value as { outcome?: unknown }).outcome;
	return outcome === 'AlreadyRefunded' || outcome === 'ExternalCorrectionUnavailable';
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
	reversesEntryId?: number | null;
	amount: number;
	error?: string | null;
}

function append<T>(path: string, operationKey: string, body: unknown) {
	return fetchApi<T>(path, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json', 'Idempotency-Key': operationKey },
		body: JSON.stringify(body)
	});
}

export const payments = {
	recordReceipt: (tenantAccountId: number, operationKey: string, body: RecordTenantReceiptRequest) =>
		append<TenantMoneyCommandResponse<RecordTenantReceiptResult>>(
			`/tenant-accounts/${tenantAccountId}/receipts`, operationKey, body),
	postCharge: (tenantAccountId: number, operationKey: string, body: PostTenantChargeRequest) =>
		append<TenantMoneyCommandResponse<TenantChargeMutationResult>>(
			`/tenant-accounts/${tenantAccountId}/charges`, operationKey, body),
	reverseCharge: (tenantAccountId: number, chargeEntryId: number, operationKey: string, body: ReverseTenantChargeRequest) =>
		append<TenantMoneyCommandResponse<TenantChargeMutationResult>>(
			`/tenant-accounts/${tenantAccountId}/charges/${chargeEntryId}/reversals`, operationKey, body),
	refundPayment: (tenantAccountId: number, operationKey: string, body: RefundTenantPaymentRequest) => {
		const request = buildTenantPaymentRefundRequest(tenantAccountId, operationKey, body);
		return fetchApi<TenantMoneyCommandResponse<TenantPaymentRefundResult>>(request.path, request.options);
	}
};
