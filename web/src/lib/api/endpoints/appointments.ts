import type { Appointment } from '$lib/types';
import { api } from '../client';

export const appointments = {
	list: (portfolioId: number) => api.get<Appointment[]>(`/appointments?portfolioId=${portfolioId}`),
	create: (data: Record<string, unknown>) => api.post<Appointment>('/appointments', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Appointment>(`/appointments/${id}`, data),
	delete: (id: number) => api.delete(`/appointments/${id}`),
};
