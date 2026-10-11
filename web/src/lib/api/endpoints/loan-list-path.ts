import { buildListQuery, type ListParams } from '../list-params.ts';

export interface LoanListParams extends ListParams {
	propertyId?: number;
}

export function buildLoanListPath(params?: LoanListParams): string {
	const { propertyId, ...list } = params ?? {};
	return `/loans${buildListQuery(list, { propertyId })}`;
}

export function buildLoanListPagePath(params?: LoanListParams): string {
	const { propertyId, ...list } = params ?? {};
	return `/loans/page${buildListQuery(list, { propertyId })}`;
}
