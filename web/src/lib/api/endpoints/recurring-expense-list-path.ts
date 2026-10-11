import { buildListQuery, type ListParams } from '../list-params.ts';

export interface RecurringExpenseListParams extends ListParams {
	propertyId?: number;
}

export function buildRecurringExpenseListPath(params?: RecurringExpenseListParams): string {
	const { propertyId, ...list } = params ?? {};
	return `/recurring-expenses${buildListQuery(list, { propertyId })}`;
}

export function buildRecurringExpenseListPagePath(params?: RecurringExpenseListParams): string {
	const { propertyId, ...list } = params ?? {};
	return `/recurring-expenses/page${buildListQuery(list, { propertyId })}`;
}
