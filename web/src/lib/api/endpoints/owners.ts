import type { Owner } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const owners = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Owner[]>(`/owner-entities${buildListQuery(params, { portfolioId })}`),
	get: (id: number) => api.get<Owner>(`/owner-entities/${id}`),
	create: (data: Record<string, unknown>) => api.post<Owner>('/owner-entities', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Owner>(`/owner-entities/${id}`, data),
	delete: (id: number) => api.delete(`/owner-entities/${id}`),
};
