import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { idempotentMutation } from '../idempotency';

export type DistributionMethod = 'Check' | 'Ach' | 'Wire' | 'Cash' | 'Other';
export type OwnerDistributionStatus = 'Draft' | 'Approved' | 'Rejected';

export interface OwnerDistribution {
	id: number;
	portfolioId: number;
	ownerEntityId: number;
	ownerName: string;
	propertyId?: number | null;
	propertyName?: string | null;
	date: string;
	amount: number;
	method: DistributionMethod;
	status: OwnerDistributionStatus;
	approvedAt?: string | null;
	approvedBusinessDate?: string | null;
	approvedByUserId?: number | null;
	rejectedAt?: string | null;
	rejectedByUserId?: number | null;
	rejectionReason?: string | null;
	bankReference?: string | null;
	exportReference?: string | null;
	exportedAt?: string | null;
	memo?: string | null;
	createdAt: string;
	updatedAt: string;
	accountId?: number | null;
	accountName?: string | null;
	journalEntryPublicId?: string | null;
	testId: string;
}

export interface OwnerDistributionListParams extends ListParams {
	ownerEntityId?: number;
	propertyId?: number;
	year?: number;
	status?: OwnerDistributionStatus;
}

export interface CreateOwnerDistributionRequest {
	ownerEntityId: number;
	propertyId?: number;
	date: string;
	amount: number;
	method: DistributionMethod;
	memo?: string;
}

export interface ApproveOwnerDistributionRequest {
	bankReference: string;
	exportReference: string;
	exportedAt?: string;
}

export interface RejectOwnerDistributionRequest {
	reason?: string;
}

export const ownerDistributions = {
	list: (params?: OwnerDistributionListParams) =>
		api.get<OwnerDistribution[]>(
			`/owner-distributions${buildListQuery(params, {
				ownerEntityId: params?.ownerEntityId,
				propertyId: params?.propertyId,
				year: params?.year,
				status: params?.status
			})}`
		),
	create: (data: CreateOwnerDistributionRequest) =>
		idempotentMutation(`owner-distributions:create:${JSON.stringify(data)}`, (key) =>
			api.post<OwnerDistribution>('/owner-distributions', data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	approve: (id: number, data: ApproveOwnerDistributionRequest) =>
		idempotentMutation(`owner-distributions:approve:${id}:${JSON.stringify(data)}`, (key) =>
			api.post<OwnerDistribution>(`/owner-distributions/${id}/approve`, data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	reject: (id: number, data: RejectOwnerDistributionRequest) =>
		idempotentMutation(`owner-distributions:reject:${id}:${JSON.stringify(data)}`, (key) =>
			api.post<OwnerDistribution>(`/owner-distributions/${id}/reject`, data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	delete: (id: number) =>
		idempotentMutation(`owner-distributions:delete:${id}`, (key) =>
			api.delete(`/owner-distributions/${id}`, {
				headers: { 'Idempotency-Key': key }
			})
		)
};
