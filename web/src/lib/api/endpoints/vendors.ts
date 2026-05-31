import type { Vendor } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const vendors = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Vendor[]>(`/vendors${buildListQuery(params, { portfolioId })}`),
	get: (id: number) => api.get<Vendor>(`/vendors/${id}`),
	create: (data: Record<string, unknown>) => api.post<Vendor>('/vendors', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Vendor>(`/vendors/${id}`, data),
	delete: (id: number) => api.delete(`/vendors/${id}`),
};
