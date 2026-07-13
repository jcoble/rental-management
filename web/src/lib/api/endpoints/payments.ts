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

export interface TenantMoneyCommandResponse<T> {
	value: T;
	replayed: boolean;
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
			`/tenant-accounts/${tenantAccountId}/charges/${chargeEntryId}/reversals`, operationKey, body)
};
