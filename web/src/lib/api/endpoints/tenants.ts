import type { Tenant } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export interface TenantListResponse {
	items: Tenant[];
	totalCount: number;
	skip: number;
	take: number;
}

export const tenants = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Tenant[]>(`/tenants${buildListQuery(params, { portfolioId })}`),
	listPage: (portfolioId: number, params?: ListParams) =>
		api.get<TenantListResponse>(`/tenants/page${buildListQuery(params, { portfolioId })}`),
	get: (id: number) => api.get<Tenant>(`/tenants/${id}`),
	create: (data: Record<string, unknown>) => api.post<Tenant>('/tenants', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Tenant>(`/tenants/${id}`, data),
	delete: (id: number) => api.delete(`/tenants/${id}`),
};
