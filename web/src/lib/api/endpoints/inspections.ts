import type { Inspection } from '$lib/types';
import { api } from '../client';

export const inspections = {
	list: (portfolioId: number) => api.get<Inspection[]>(`/inspections?portfolioId=${portfolioId}`),
	create: (data: Record<string, unknown>) => api.post<Inspection>('/inspections', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Inspection>(`/inspections/${id}`, data),
	delete: (id: number) => api.delete(`/inspections/${id}`),
};
