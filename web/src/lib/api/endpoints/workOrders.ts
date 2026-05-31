import type { WorkOrder } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const workOrders = {
	list: (portfolioId: number, params?: ListParams & { propertyId?: number; vendorId?: number }) => {
		const { propertyId, vendorId, ...list } = params ?? {};
		return api.get<WorkOrder[]>(`/work-orders${buildListQuery(list, { portfolioId, propertyId, vendorId })}`);
	},
	get: (id: number) => api.get<WorkOrder>(`/work-orders/${id}`),
	create: (data: Record<string, unknown>) => api.post<WorkOrder>('/work-orders', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<WorkOrder>(`/work-orders/${id}`, data),
	updateStatus: (id: number, status: string) => api.post<WorkOrder>(`/work-orders/${id}/status`, { status }),
	delete: (id: number) => api.delete(`/work-orders/${id}`),
};
