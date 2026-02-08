import type { Vendor } from '$lib/types';
import { api } from '../client';

export const vendors = {
	list: (portfolioId: number) => api.get<Vendor[]>(`/vendors?portfolioId=${portfolioId}`),
	create: (data: Record<string, unknown>) => api.post<Vendor>('/vendors', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Vendor>(`/vendors/${id}`, data),
	delete: (id: number) => api.delete(`/vendors/${id}`),
};
