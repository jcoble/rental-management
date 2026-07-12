import { buildListQuery, type ListParams } from '../list-params.ts';

export interface PaymentListParams extends ListParams {
	tenantAccountId?: number;
	leaseManagementId?: number;
	paidFrom?: string;
	paidTo?: string;
}

export function buildPaymentListPath(portfolioId: number, params?: PaymentListParams): string {
	const { tenantAccountId, leaseManagementId, paidFrom, paidTo, ...list } = params ?? {};
	return `/payments${buildListQuery(list, { portfolioId, tenantAccountId, leaseManagementId, paidFrom, paidTo })}`;
}

export function buildPaymentListPagePath(portfolioId: number, params?: PaymentListParams): string {
	const { tenantAccountId, leaseManagementId, paidFrom, paidTo, ...list } = params ?? {};
	return `/payments/page${buildListQuery(list, { portfolioId, tenantAccountId, leaseManagementId, paidFrom, paidTo })}`;
}
