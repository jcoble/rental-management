import type { VendorDispatch, WorkOrder, WorkOrderDetail } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export interface WorkOrderListParams extends ListParams {
	propertyId?: number;
	unitId?: number;
	vendorId?: number;
	status?: string;
	priority?: string;
}

export interface WorkOrderListResponse {
	items: WorkOrder[];
	totalCount: number;
	skip: number;
	take: number;
}

export const workOrders = {
	list: (portfolioId: number, params?: WorkOrderListParams) => {
		const { propertyId, unitId, vendorId, ...list } = params ?? {};
		return api.get<WorkOrder[]>(`/work-orders${buildListQuery(list, { portfolioId, propertyId, unitId, vendorId })}`);
	},
	listPage: (portfolioId: number, params?: WorkOrderListParams) => {
		const { propertyId, unitId, vendorId, status, priority, ...list } = params ?? {};
		return api.get<WorkOrderListResponse>(
			`/work-orders/page${buildListQuery(list, { portfolioId, propertyId, unitId, vendorId, status, priority })}`
		);
	},
	// Detail includes a `timeline` of status-change events (oldest→newest).
	get: (id: number) => api.get<WorkOrderDetail>(`/work-orders/${id}`),
	create: (data: Record<string, unknown>) => api.post<WorkOrder>('/work-orders', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<WorkOrder>(`/work-orders/${id}`, data),
	// The API has no /status route; status is a normal field on the work order, so we PATCH it.
	// An optional statusNote (≤2000 chars) is recorded on the timeline when the status changes.
	updateStatus: (id: number, status: string, statusNote?: string) =>
		api.patch<WorkOrderDetail>(`/work-orders/${id}`, statusNote ? { status, statusNote } : { status }),
	// Assign + text a vendor the job. The vendor replies DONE to auto-close it.
	// Throws ApiError (400) when the chosen vendor has no phone number on file.
	dispatch: (id: number, data: { vendorId: number; note?: string }) =>
		api.post<VendorDispatch>(`/work-orders/${id}/dispatch`, data),
	delete: (id: number) => api.delete(`/work-orders/${id}`),
};
