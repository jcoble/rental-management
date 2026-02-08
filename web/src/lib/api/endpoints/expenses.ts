import type { Expense } from '$lib/types';
import { api } from '../client';

export const expenses = {
	list: (portfolioId: number, status?: string) => {
		const query = new URLSearchParams({ portfolioId: String(portfolioId) });
		if (status) query.set('status', status);
		return api.get<Expense[]>(`/expenses?${query.toString()}`);
	},
	create: (data: Record<string, unknown>) => api.post<Expense>('/expenses', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Expense>(`/expenses/${id}`, data),
	summary: (portfolioId: number) => api.get(`/expenses/summary?portfolioId=${portfolioId}`),
};
