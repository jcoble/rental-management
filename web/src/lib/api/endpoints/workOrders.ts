import type { WorkOrder } from '$lib/types';
import { api } from '../client';

export const workOrders = {
	list: (portfolioId: number, status?: string, priority?: string) => {
		const query = new URLSearchParams({ portfolioId: String(portfolioId) });
		if (status) query.set('status', status);
		if (priority) query.set('priority', priority);
		return api.get<WorkOrder[]>(`/work-orders?${query.toString()}`);
	},
	create: (data: Record<string, unknown>) => api.post<WorkOrder>('/work-orders', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<WorkOrder>(`/work-orders/${id}`, data),
	updateStatus: (id: number, status: string) => api.post<WorkOrder>(`/work-orders/${id}/status`, { status }),
	delete: (id: number) => api.delete(`/work-orders/${id}`),
};
