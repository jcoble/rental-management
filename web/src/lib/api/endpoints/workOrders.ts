import type { VendorDispatch, WorkOrder, WorkOrderDetail } from '$lib/types';
import { api } from '../client';
import { idempotentMutation } from '../idempotency';
import {
	buildWorkOrderListPagePath,
	buildWorkOrderListPath,
	type WorkOrderListParams
} from './work-order-list-path';

export type { WorkOrderListParams } from './work-order-list-path';

export interface WorkOrderListResponse {
	items: WorkOrder[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface WorkOrderResponsibility {
	id: string;
	workOrderId: number;
	workspaceMembershipId: number | null;
	membershipRoleAssignmentId: number | null;
	accessContextId: number | null;
	memberDisplayName: string;
	roleProfileName: string;
	kind: 'Primary' | 'Supporting';
	effectiveFromUtc: string;
	effectiveToUtc: string | null;
	canManage: boolean;
}

export interface WorkOrderResponsibilityCandidate {
	workspaceMembershipId: number;
	membershipRoleAssignmentId: number;
	accessContextId: number;
	accessRevision: number;
	memberDisplayName: string;
	roleProfileName: string;
}

export const workOrders = {
	list: (portfolioId: number, params?: WorkOrderListParams) => {
		return api.get<WorkOrder[]>(buildWorkOrderListPath(portfolioId, params));
	},
	listPage: (portfolioId: number, params?: WorkOrderListParams) => {
		return api.get<WorkOrderListResponse>(buildWorkOrderListPagePath(portfolioId, params));
	},
	// Detail includes a `timeline` of status-change events (oldest→newest).
	get: (id: number) => api.get<WorkOrderDetail>(`/work-orders/${id}`),
	create: (data: Record<string, unknown>) =>
		idempotentMutation(`work-order:create:${JSON.stringify(data)}`, (key) =>
			api.post<WorkOrder>('/work-orders', data, { headers: { 'Idempotency-Key': key } })
		),
	update: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`work-order:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<WorkOrder>(`/work-orders/${id}`, data, { headers: { 'Idempotency-Key': key } })
		),
	// The API has no /status route; status is a normal field on the work order, so we PATCH it.
	// An optional statusNote (≤2000 chars) is recorded on the timeline when the status changes.
	updateStatus: (id: number, status: string, statusNote?: string) =>
		idempotentMutation(`work-order:status:${id}:${status}:${statusNote ?? ''}`, (key) =>
			api.patch<WorkOrderDetail>(
				`/work-orders/${id}`,
				statusNote ? { status, statusNote } : { status },
				{ headers: { 'Idempotency-Key': key } }
			)
		),
	updateAssigned: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`work-order:assigned-update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch(`/work-orders/${id}/assigned-update`, data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	responsibilities: (id: number) =>
		api.get<WorkOrderResponsibility[]>(`/work-orders/${id}/responsibilities`),
	responsibilityCandidates: (id: number, search = '') =>
		api.get<WorkOrderResponsibilityCandidate[]>(
			`/work-orders/${id}/responsibilities/candidates?search=${encodeURIComponent(search)}`
		),
	assignResponsibility: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`work-order:responsibility:assign:${id}:${JSON.stringify(data)}`, (key) =>
			api.put(`/work-orders/${id}/responsibilities/current`, data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	closeResponsibility: (id: number, responsibilityId: string, data: Record<string, unknown>) =>
		idempotentMutation(
			`work-order:responsibility:close:${id}:${responsibilityId}:${JSON.stringify(data)}`,
			(key) =>
				api.post(`/work-orders/${id}/responsibilities/${responsibilityId}/close`, data, {
					headers: { 'Idempotency-Key': key }
				})
		),
	// Assign + text a vendor the job. The vendor replies DONE to auto-close it.
	// Throws ApiError (400) when the chosen vendor has no phone number on file.
	dispatch: (id: number, data: { vendorId: number; note?: string }) =>
		api.post<VendorDispatch>(`/work-orders/${id}/dispatch`, {
			...data,
			idempotencyKey: crypto.randomUUID()
		}),
	delete: (id: number) =>
		idempotentMutation(`work-order:delete:${id}`, (key) =>
			api.delete(`/work-orders/${id}`, { headers: { 'Idempotency-Key': key } })
		),
};
