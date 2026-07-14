import type { Vendor, VendorRating, VendorScorecard } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { idempotentMutation } from '../idempotency';

export interface VendorListResponse {
	items: Vendor[];
	totalCount: number;
	skip: number;
	take: number;
}

export const vendors = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Vendor[]>(`/vendors${buildListQuery(params, { portfolioId })}`),
	listPage: (portfolioId: number, params?: ListParams) =>
		api.get<VendorListResponse>(`/vendors/page${buildListQuery(params, { portfolioId })}`),
	get: (id: number) => api.get<Vendor>(`/vendors/${id}`),
	create: (data: Record<string, unknown>) =>
		idempotentMutation(`vendors:create:${JSON.stringify(data)}`, (key) =>
			api.post<Vendor>('/vendors', data, { headers: { 'Idempotency-Key': key } })
		),
	update: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`vendors:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<Vendor>(`/vendors/${id}`, data, { headers: { 'Idempotency-Key': key } })
		),
	delete: (id: number) =>
		idempotentMutation(`vendors:delete:${id}`, (key) =>
			api.delete(`/vendors/${id}`, { headers: { 'Idempotency-Key': key } })
		),
	// Record a 1–5 star rating; optionally tied to the work order it followed.
	rate: (id: number, data: { stars: number; comment?: string; workOrderId?: number }) =>
		api.post<VendorRating>(`/vendors/${id}/ratings`, data),
	// Performance scorecard: rating, jobs completed, average DONE response time.
	scorecard: (id: number) => api.get<VendorScorecard>(`/vendors/${id}/scorecard`),
	// Text the vendor a W-9 request. 200 { queued, sentTo }; 400 { error } if no phone.
	requestW9: (id: number, clientOperationId: string) =>
		api.post<{ queued: boolean; sentTo: string }>(`/vendors/${id}/request-w9`, {
			clientOperationId
		}),
};
