import type { Appointment } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const appointments = {
	list: (portfolioId: number, params?: ListParams & { propertyId?: number; tenantId?: number }) => {
		const { propertyId, tenantId, ...list } = params ?? {};
		return api.get<Appointment[]>(`/appointments${buildListQuery(list, { portfolioId, propertyId, tenantId })}`);
	},
	get: (id: number) => api.get<Appointment>(`/appointments/${id}`),
	create: (data: Record<string, unknown>) => api.post<Appointment>('/appointments', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Appointment>(`/appointments/${id}`, data),
	delete: (id: number) => api.delete(`/appointments/${id}`),
};
