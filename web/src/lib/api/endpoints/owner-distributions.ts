import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

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
		api.post<OwnerDistribution>('/owner-distributions', data),
	delete: (id: number) => api.delete(`/owner-distributions/${id}`),
};
