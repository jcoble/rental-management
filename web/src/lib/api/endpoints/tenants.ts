import type { Tenant } from '$lib/types';
import { api } from '../client';

export const tenants = {
	list: (portfolioId: number) => api.get<Tenant[]>(`/tenants?portfolioId=${portfolioId}`),
	create: (data: Record<string, unknown>) => api.post<Tenant>('/tenants', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Tenant>(`/tenants/${id}`, data),
	delete: (id: number) => api.delete(`/tenants/${id}`),
};
