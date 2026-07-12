import type { PaymentReceipt } from '$lib/types';
import { api, fetchApi } from '../client';
import {
	buildPaymentListPagePath,
	buildPaymentListPath,
	type PaymentListParams
} from './payment-list-path';

export interface PaymentReceiptListResponse {
	items: PaymentReceipt[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface TenantAccountOption {
	tenantAccountId: number;
	leaseManagementId: number;
	propertyId: number;
	unitId: number;
	accountNumber: string;
	relationshipNumber: string;
	propertyName: string;
	unitNumber: string;
	primaryTenantName?: string | null;
}

export interface TenantAccountOptionListResponse {
	items: TenantAccountOption[];
	totalCount: number;
	skip: number;
	take: number;
}

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
	accountOptions: (params?: { skip?: number; take?: number; search?: string }) => {
		const query = new URLSearchParams();
		if (params?.skip != null) query.set('skip', String(params.skip));
		if (params?.take != null) query.set('take', String(params.take));
		if (params?.search) query.set('search', params.search);
		const suffix = query.size ? `?${query}` : '';
		return api.get<TenantAccountOptionListResponse>(`/payments/account-options${suffix}`);
	},
	list: (portfolioId: number, params?: PaymentListParams) =>
		api.get<PaymentReceipt[]>(buildPaymentListPath(portfolioId, params)),
	listPage: (portfolioId: number, params?: PaymentListParams) =>
		api.get<PaymentReceiptListResponse>(buildPaymentListPagePath(portfolioId, params)),
	get: (id: number) => api.get<PaymentReceipt>(`/payments/${id}`),
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
