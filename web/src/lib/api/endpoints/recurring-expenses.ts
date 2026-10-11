import { api } from '../client';
import { idempotentMutation } from '../idempotency';
import {
	buildRecurringExpenseListPagePath,
	buildRecurringExpenseListPath,
	type RecurringExpenseListParams
} from './recurring-expense-list-path';

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

export interface RecurringExpenseListResponse {
	items: RecurringExpense[];
	totalCount: number;
	skip: number;
	take: number;
}

export const recurringExpenses = {
	list: (params?: RecurringExpenseListParams) =>
		api.get<RecurringExpense[]>(buildRecurringExpenseListPath(params)),
	listPage: (params?: RecurringExpenseListParams) =>
		api.get<RecurringExpenseListResponse>(buildRecurringExpenseListPagePath(params)),
	get: (id: number) => api.get<RecurringExpense>(`/recurring-expenses/${id}`),
	create: (data: Record<string, unknown>) =>
		idempotentMutation(`recurring-expenses:create:${JSON.stringify(data)}`, (key) =>
			api.post<RecurringExpense>('/recurring-expenses', data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	update: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`recurring-expenses:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<RecurringExpense>(`/recurring-expenses/${id}`, data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	remove: (id: number) =>
		idempotentMutation(`recurring-expenses:delete:${id}`, (key) =>
			api.delete(`/recurring-expenses/${id}`, {
				headers: { 'Idempotency-Key': key }
			})
		)
};
