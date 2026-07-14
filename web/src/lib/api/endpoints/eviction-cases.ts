import { api } from '../client';
import { idempotentMutation } from '../idempotency';
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
	leaseManagementId: number;
	relationshipNumber?: string | null;
	leaseAgreementId?: number | null;
	agreementNumber?: string | null;
	propertyId: number;
	propertyName?: string | null;
	unitId: number;
	unitNumber?: string | null;
	respondents: Array<{
		leaseManagementPartyId: number;
		tenantId: number;
		tenantName: string;
	}>;
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
	leaseManagementId?: number;
	propertyId?: number;
	leaseManagementPartyId?: number;
	status?: EvictionCaseStatus;
}

export interface EvictionCaseListResponse {
	items: EvictionCase[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface CreateEvictionCaseRequest {
	leaseManagementId: number;
	leaseAgreementId?: number | null;
	respondentLeaseManagementPartyIds: number[];
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
	leaseManagementId: params?.leaseManagementId,
	propertyId: params?.propertyId,
	leaseManagementPartyId: params?.leaseManagementPartyId,
	status: params?.status
});

export const evictionCases = {
	list: (params?: EvictionCaseListParams) =>
		api.get<EvictionCase[]>(`/eviction-cases${buildListQuery(params, queryParams(params))}`),
	listPage: (params?: EvictionCaseListParams) =>
		api.get<EvictionCaseListResponse>(`/eviction-cases/page${buildListQuery(params, queryParams(params))}`),
	get: (id: number) => api.get<EvictionCase>(`/eviction-cases/${id}`),
	create: (data: CreateEvictionCaseRequest) =>
		idempotentMutation(`eviction-case:create:${JSON.stringify(data)}`, (key) =>
			api.post<EvictionCase>('/eviction-cases', data, { headers: { 'Idempotency-Key': key } })
		),
	update: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`eviction-case:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<EvictionCase>(`/eviction-cases/${id}`, data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	addEvent: (id: number, data: CreateEvictionCaseEventRequest) =>
		idempotentMutation(`eviction-case:event:${id}:${JSON.stringify(data)}`, (key) =>
			api.post<EvictionCase>(`/eviction-cases/${id}/events`, data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	remove: (id: number) =>
		idempotentMutation(`eviction-case:delete:${id}`, (key) =>
			api.delete(`/eviction-cases/${id}`, { headers: { 'Idempotency-Key': key } })
		)
};
