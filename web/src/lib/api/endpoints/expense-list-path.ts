import { buildListQuery, type ListParams } from '../list-params.ts';

export interface ExpenseListParams extends ListParams {
	propertyId?: number;
	unitId?: number;
	workOrderId?: number;
	workOrderLinkedOnly?: boolean;
}

export function buildExpenseListPagePath(portfolioId: number, params?: ExpenseListParams): string {
	const { propertyId, unitId, workOrderId, workOrderLinkedOnly, ...list } = params ?? {};
	return `/expenses/page${buildListQuery(list, {
		portfolioId,
		propertyId,
		unitId,
		workOrderId,
		workOrderLinkedOnly: workOrderLinkedOnly ? 'true' : undefined
	})}`;
}
