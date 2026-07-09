import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export type EvictionCaseStatus =
	| 'Draft'
	| 'NoticeServed'
	| 'Filed'
	| 'HearingScheduled'
	| 'Judgment'
	| 'MoveOut'
	| 'Settled'
	| 'Dismissed';

export type EvictionEventType =
	| 'NoticeServed'
	| 'Filed'
	| 'HearingScheduled'
	| 'Judgment'
	| 'MoveOut'
	| 'Settlement'
	| 'Dismissal'
	| 'PaymentPlan'
	| 'Note';

export interface EvictionCaseEvent {
	id: number;
	portfolioId: number;
	evictionCaseId: number;
	eventType: EvictionEventType;
	eventDate: string;
	notes?: string | null;
	createdAt: string;
	updatedAt: string;
	testId: string;
}

export interface EvictionCase {
	id: number;
	portfolioId: number;
	leaseId: number;
	leaseNumber?: string | null;
	propertyId: number;
	propertyName?: string | null;
	unitId: number;
	unitNumber?: string | null;
	tenantId: number;
	tenantName?: string | null;
	status: EvictionCaseStatus;
	filedOnDate?: string | null;
	hearingDate?: string | null;
	resolvedOnDate?: string | null;
	courtName?: string | null;
	caseNumber?: string | null;
	resolution?: string | null;
	notes?: string | null;
	eventCount: number;
	latestEventDate?: string | null;
	events: EvictionCaseEvent[];
	createdAt: string;
	updatedAt: string;
	testId: string;
}

export interface EvictionCaseListParams extends ListParams {
	leaseId?: number;
	propertyId?: number;
	tenantId?: number;
	status?: EvictionCaseStatus;
}

export interface EvictionCaseListResponse {
	items: EvictionCase[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface CreateEvictionCaseRequest {
	leaseId: number;
	status: EvictionCaseStatus;
	filedOnDate?: string | null;
	hearingDate?: string | null;
	courtName?: string | null;
	caseNumber?: string | null;
	notes?: string | null;
}

export interface CreateEvictionCaseEventRequest {
	eventType: EvictionEventType;
	eventDate: string;
	notes?: string | null;
}

const queryParams = (params?: EvictionCaseListParams) => ({
	leaseId: params?.leaseId,
	propertyId: params?.propertyId,
	tenantId: params?.tenantId,
	status: params?.status
});

export const evictionCases = {
	list: (params?: EvictionCaseListParams) =>
		api.get<EvictionCase[]>(`/eviction-cases${buildListQuery(params, queryParams(params))}`),
	listPage: (params?: EvictionCaseListParams) =>
		api.get<EvictionCaseListResponse>(`/eviction-cases/page${buildListQuery(params, queryParams(params))}`),
	get: (id: number) => api.get<EvictionCase>(`/eviction-cases/${id}`),
	create: (data: CreateEvictionCaseRequest) => api.post<EvictionCase>('/eviction-cases', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<EvictionCase>(`/eviction-cases/${id}`, data),
	addEvent: (id: number, data: CreateEvictionCaseEventRequest) =>
		api.post<EvictionCase>(`/eviction-cases/${id}/events`, data),
	remove: (id: number) => api.delete(`/eviction-cases/${id}`)
};
