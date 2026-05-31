import type { Owner } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const owners = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Owner[]>(`/owners${buildListQuery(params, { portfolioId })}`),
	get: (id: number) => api.get<Owner>(`/owners/${id}`),
	create: (data: Record<string, unknown>) => api.post<Owner>('/owners', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Owner>(`/owners/${id}`, data),
	delete: (id: number) => api.delete(`/owners/${id}`),
};
