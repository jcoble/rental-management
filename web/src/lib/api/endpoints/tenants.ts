import type { Tenant } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const tenants = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Tenant[]>(`/tenants${buildListQuery(params, { portfolioId })}`),
	get: (id: number) => api.get<Tenant>(`/tenants/${id}`),
	create: (data: Record<string, unknown>) => api.post<Tenant>('/tenants', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Tenant>(`/tenants/${id}`, data),
	delete: (id: number) => api.delete(`/tenants/${id}`),
};
