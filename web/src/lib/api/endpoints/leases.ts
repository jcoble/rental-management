import type { Lease } from '$lib/types';
import { api } from '../client';

export const leases = {
	list: (portfolioId: number, status?: string) => {
		const query = new URLSearchParams({ portfolioId: String(portfolioId) });
		if (status) query.set('status', status);
		return api.get<Lease[]>(`/leases?${query.toString()}`);
	},
	create: (data: Record<string, unknown>) => api.post<Lease>('/leases', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Lease>(`/leases/${id}`, data),
	delete: (id: number) => api.delete(`/leases/${id}`),
	ledger: (id: number) => api.get(`/leases/${id}/ledger`),
};
