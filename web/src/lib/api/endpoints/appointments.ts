import type { Appointment } from '$lib/types';
import { api } from '../client';
import { idempotentMutation } from '../idempotency';
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

export interface AppointmentScheduleSummary {
	nextSevenDaysCount: number;
	windowStartUtc: string;
	windowEndUtc: string;
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
	scheduleSummary: () =>
		api.get<AppointmentScheduleSummary>('/appointments/schedule-summary'),
	get: (id: number) => api.get<Appointment>(`/appointments/${id}`),
	create: (data: Record<string, unknown>) =>
		idempotentMutation(`appointment:create:${JSON.stringify(data)}`, (key) =>
			api.post<Appointment>('/appointments', data, { headers: { 'Idempotency-Key': key } })
		),
	update: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`appointment:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<Appointment>(`/appointments/${id}`, data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	delete: (id: number, propertyId?: number) =>
		idempotentMutation(`appointment:delete:${id}:${propertyId ?? 'none'}`, (key) =>
			api.delete(
				`/appointments/${id}${propertyId == null ? '' : `?expectedPropertyId=${propertyId}`}`,
				{ headers: { 'Idempotency-Key': key } }
			)
		),
};
