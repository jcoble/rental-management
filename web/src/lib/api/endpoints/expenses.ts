import type { Expense } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const expenses = {
	list: (portfolioId: number, params?: ListParams & { propertyId?: number }) => {
		const { propertyId, ...list } = params ?? {};
		return api.get<Expense[]>(`/expenses${buildListQuery(list, { portfolioId, propertyId })}`);
	},
	get: (id: number) => api.get<Expense>(`/expenses/${id}`),
	create: (data: Record<string, unknown>) => api.post<Expense>('/expenses', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Expense>(`/expenses/${id}`, data),
	delete: (id: number) => api.delete(`/expenses/${id}`),
	// Expense totals (by Schedule E category + grand total) live on the accounting summary;
	// use the `accounting` endpoint module rather than an expenses-only summary route.
};
