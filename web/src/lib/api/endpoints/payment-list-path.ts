import { buildListQuery, type ListParams } from '../list-params.ts';

export interface PaymentListParams extends ListParams {
	leaseId?: number;
	applicationId?: number;
	dueFrom?: string;
	dueTo?: string;
	paidFrom?: string;
	paidTo?: string;
}

export function buildPaymentListPath(portfolioId: number, params?: PaymentListParams): string {
	const { leaseId, applicationId, dueFrom, dueTo, paidFrom, paidTo, ...list } = params ?? {};
	return `/payments${buildListQuery(list, { portfolioId, leaseId, applicationId, dueFrom, dueTo, paidFrom, paidTo })}`;
}

export function buildPaymentListPagePath(portfolioId: number, params?: PaymentListParams): string {
	const { leaseId, applicationId, dueFrom, dueTo, paidFrom, paidTo, ...list } = params ?? {};
	return `/payments/page${buildListQuery(list, { portfolioId, leaseId, applicationId, dueFrom, dueTo, paidFrom, paidTo })}`;
}
