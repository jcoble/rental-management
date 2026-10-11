import { buildListQuery, type ListParams } from '../list-params.ts';

export interface ExpenseListParams extends ListParams {
	operationalScope?: 'Portfolio' | 'Property' | 'Unit' | 'WorkOrder';
	propertyId?: number;
	unitId?: number;
	workOrderId?: number;
	workOrderLinkedOnly?: boolean;
	incurredFrom?: string;
	incurredTo?: string;
	dueFrom?: string;
	dueTo?: string;
	paidFrom?: string;
	paidTo?: string;
}

export function buildExpenseListPagePath(portfolioId: number, params?: ExpenseListParams): string {
	const {
		operationalScope,
		propertyId,
		unitId,
		workOrderId,
		workOrderLinkedOnly,
		incurredFrom,
		incurredTo,
		dueFrom,
		dueTo,
		paidFrom,
		paidTo,
		...list
	} = params ?? {};
	return `/expenses/page${buildListQuery(list, {
		portfolioId,
		operationalScope,
		propertyId,
		unitId,
		workOrderId,
		workOrderLinkedOnly: workOrderLinkedOnly ? 'true' : undefined,
		incurredFrom,
		incurredTo,
		dueFrom,
		dueTo,
		paidFrom,
		paidTo
	})}`;
}
