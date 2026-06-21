import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

/** How often a recurring-expense template materializes into an expense row. */
export type RecurringExpenseFrequency = 'Monthly' | 'Quarterly' | 'Annual';

/**
 * A standing cost entered once (insurance, property tax, HOA, management fee) that the Engine
 * materializes into expense rows on a schedule so it flows into every report without re-entry.
 * `category` is an IRS Schedule E category name.
 */
export interface RecurringExpense {
	id: number;
	portfolioId: number;
	propertyId?: number | null;
	propertyName?: string | null;
	unitId?: number | null;
	category: string;
	description: string;
	amount: number;
	frequency: RecurringExpenseFrequency;
	startDate: string;
	nextRunDate: string;
	active: boolean;
	notes?: string | null;
	createdAt: string;
	updatedAt: string;
	/** Stable selector for tests, e.g. `recurring-expense-1`. */
	testId: string;
}

export const recurringExpenses = {
	list: (params?: ListParams & { propertyId?: number }) => {
		const { propertyId, ...list } = params ?? {};
		return api.get<RecurringExpense[]>(`/recurring-expenses${buildListQuery(list, { propertyId })}`);
	},
	get: (id: number) => api.get<RecurringExpense>(`/recurring-expenses/${id}`),
	create: (data: Record<string, unknown>) => api.post<RecurringExpense>('/recurring-expenses', data),
	update: (id: number, data: Record<string, unknown>) =>
		api.patch<RecurringExpense>(`/recurring-expenses/${id}`, data),
	remove: (id: number) => api.delete(`/recurring-expenses/${id}`)
};
