import type { Appointment } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export interface AppointmentListParams extends ListParams {
	propertyId?: number;
	tenantId?: number;
	type?: string;
	status?: string;
}

export interface AppointmentListResponse {
	items: Appointment[];
	totalCount: number;
	skip: number;
	take: number;
}

export const appointments = {
	list: (portfolioId: number, params?: AppointmentListParams) => {
		const { propertyId, tenantId, type, status, ...list } = params ?? {};
		return api.get<Appointment[]>(
			`/appointments${buildListQuery(list, { portfolioId, propertyId, tenantId, type, status })}`
		);
	},
	listPage: (portfolioId: number, params?: AppointmentListParams) => {
		const { propertyId, tenantId, type, status, ...list } = params ?? {};
		return api.get<AppointmentListResponse>(
			`/appointments/page${buildListQuery(list, { portfolioId, propertyId, tenantId, type, status })}`
		);
	},
	get: (id: number) => api.get<Appointment>(`/appointments/${id}`),
	create: (data: Record<string, unknown>) => api.post<Appointment>('/appointments', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Appointment>(`/appointments/${id}`, data),
	delete: (id: number) => api.delete(`/appointments/${id}`),
};
