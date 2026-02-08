import type { Payment } from '$lib/types';
import { api } from '../client';

export const payments = {
	list: (portfolioId: number, status?: string) => {
		const query = new URLSearchParams({ portfolioId: String(portfolioId) });
		if (status) query.set('status', status);
		return api.get<Payment[]>(`/payments?${query.toString()}`);
	},
	create: (data: Record<string, unknown>) => api.post<Payment>('/payments', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Payment>(`/payments/${id}`, data),
	markPaid: (id: number, data: Record<string, unknown>) => api.post<Payment>(`/payments/${id}/mark-paid`, data),
	summary: (portfolioId: number) => api.get(`/payments/summary?portfolioId=${portfolioId}`),
};
