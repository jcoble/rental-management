import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { idempotentMutation } from '../idempotency';

export type DistributionMethod = 'Check' | 'Ach' | 'Wire' | 'Cash' | 'Other';

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
	memo?: string | null;
	createdAt: string;
	updatedAt: string;
	testId: string;
}

export interface OwnerDistributionListParams extends ListParams {
	ownerEntityId?: number;
	propertyId?: number;
	year?: number;
}

export interface CreateOwnerDistributionRequest {
	ownerEntityId: number;
	propertyId?: number;
	date: string;
	amount: number;
	method: DistributionMethod;
	memo?: string;
}

export const ownerDistributions = {
	list: (params?: OwnerDistributionListParams) =>
		api.get<OwnerDistribution[]>(
			`/owner-distributions${buildListQuery(params, {
				ownerEntityId: params?.ownerEntityId,
				propertyId: params?.propertyId,
				year: params?.year
			})}`
		),
	create: (data: CreateOwnerDistributionRequest) =>
		idempotentMutation(`owner-distributions:create:${JSON.stringify(data)}`, (key) =>
			api.post<OwnerDistribution>('/owner-distributions', data, {
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
