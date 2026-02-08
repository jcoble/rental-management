import type { Portfolio, Dashboard } from '$lib/types';
import { api } from '../client';

export const portfolios = {
	list: () => api.get<Portfolio[]>('/portfolios'),
	get: (id: number) => api.get<Portfolio>(`/portfolios/${id}`),
	create: (data: Partial<Portfolio>) => api.post<Portfolio>('/portfolios', data),
	update: (id: number, data: Partial<Portfolio>) => api.patch<Portfolio>(`/portfolios/${id}`, data),
	delete: (id: number) => api.delete(`/portfolios/${id}`),
	dashboard: (id: number) => api.get<Dashboard>(`/portfolios/${id}/dashboard`),
};
