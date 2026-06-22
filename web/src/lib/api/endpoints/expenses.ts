import type { Expense } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { buildExpenseListPagePath, type ExpenseListParams } from './expense-list-path';

export interface ExpenseListResponse {
	items: Expense[];
	totalCount: number;
	skip: number;
	take: number;
}

export const expenses = {
	list: (portfolioId: number, params?: ExpenseListParams) => {
		const { propertyId, unitId, workOrderId, ...list } = params ?? {};
		return api.get<Expense[]>(`/expenses${buildListQuery(list, { portfolioId, propertyId, unitId, workOrderId })}`);
	},
	listPage: (portfolioId: number, params?: ExpenseListParams) => {
		return api.get<ExpenseListResponse>(buildExpenseListPagePath(portfolioId, params));
	},
	get: (id: number) => api.get<Expense>(`/expenses/${id}`),
	create: (data: Record<string, unknown>) => api.post<Expense>('/expenses', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Expense>(`/expenses/${id}`, data),
	delete: (id: number) => api.delete(`/expenses/${id}`),
	// Expense totals (by Schedule E category + grand total) live on the accounting summary;
	// use the `accounting` endpoint module rather than an expenses-only summary route.
};
