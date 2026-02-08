import type { Owner } from '$lib/types';
import { api } from '../client';

export const owners = {
	list: (portfolioId: number) => api.get<Owner[]>(`/owners?portfolioId=${portfolioId}`),
	create: (data: Record<string, unknown>) => api.post<Owner>('/owners', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Owner>(`/owners/${id}`, data),
	delete: (id: number) => api.delete(`/owners/${id}`),
};
