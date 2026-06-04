import type { WorkOrder, WorkOrderDetail } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const workOrders = {
	list: (portfolioId: number, params?: ListParams & { propertyId?: number; vendorId?: number }) => {
		const { propertyId, vendorId, ...list } = params ?? {};
		return api.get<WorkOrder[]>(`/work-orders${buildListQuery(list, { portfolioId, propertyId, vendorId })}`);
	},
	// Detail includes a `timeline` of status-change events (oldest→newest).
	get: (id: number) => api.get<WorkOrderDetail>(`/work-orders/${id}`),
	create: (data: Record<string, unknown>) => api.post<WorkOrder>('/work-orders', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<WorkOrder>(`/work-orders/${id}`, data),
	// The API has no /status route; status is a normal field on the work order, so we PATCH it.
	// An optional statusNote (≤2000 chars) is recorded on the timeline when the status changes.
	updateStatus: (id: number, status: string, statusNote?: string) =>
		api.patch<WorkOrderDetail>(`/work-orders/${id}`, statusNote ? { status, statusNote } : { status }),
	delete: (id: number) => api.delete(`/work-orders/${id}`),
};
