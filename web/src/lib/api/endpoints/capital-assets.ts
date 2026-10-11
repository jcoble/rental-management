import { api } from '../client';
import { idempotentMutation } from '../idempotency';
import { buildListQuery, type ListParams } from '../list-params';

export type DepreciationMethod = 'StraightLine' | 'Macrs';
export type DepreciationConvention = 'MidMonth' | 'HalfYear' | 'MidQuarter';

export interface CapitalAsset {
	id: number;
	portfolioId: number;
	propertyId: number;
	propertyName?: string | null;
	unitId?: number | null;
	unitNumber?: string | null;
	sourceExpenseId?: number | null;
	sourceExpenseDescription?: string | null;
	description: string;
	costBasis: number;
	inServiceDate: string;
	method: DepreciationMethod;
	recoveryYears: number;
	convention: DepreciationConvention;
	accumulatedDepreciation: number;
	disposedOnDate?: string | null;
	depreciationYear: number;
	annualDepreciation: number;
	isFirstYearEstimate: boolean;
	createdAt: string;
	updatedAt: string;
	testId: string;
}

export interface CapitalAssetListParams extends ListParams {
	propertyId?: number;
	unitId?: number;
	year?: number;
}

export interface CapitalAssetListResponse {
	items: CapitalAsset[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface CreateCapitalAssetRequest {
	propertyId: number;
	unitId?: number | null;
	description: string;
	costBasis: number;
	inServiceDate: string;
	method: DepreciationMethod;
	recoveryYears: number;
	convention: DepreciationConvention;
	accumulatedDepreciation?: number | null;
	disposedOnDate?: string | null;
}

export interface CapitalizeExpenseRequest {
	inServiceDate: string;
	method: DepreciationMethod;
	recoveryYears: number;
	convention: DepreciationConvention;
	description?: string | null;
}

const queryParams = (params?: CapitalAssetListParams) => ({
	propertyId: params?.propertyId,
	unitId: params?.unitId,
	year: params?.year
});

export const capitalAssets = {
	list: (params?: CapitalAssetListParams) =>
		api.get<CapitalAsset[]>(`/capital-assets${buildListQuery(params, queryParams(params))}`),
	listPage: (params?: CapitalAssetListParams) =>
		api.get<CapitalAssetListResponse>(`/capital-assets/page${buildListQuery(params, queryParams(params))}`),
	get: (id: number, year?: number) =>
		api.get<CapitalAsset>(`/capital-assets/${id}${buildListQuery(undefined, { year })}`),
	create: (data: CreateCapitalAssetRequest) =>
		idempotentMutation(`capital-assets:create:${JSON.stringify(data)}`, (key) =>
			api.post<CapitalAsset>('/capital-assets', data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	update: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`capital-assets:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<CapitalAsset>(`/capital-assets/${id}`, data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	remove: (id: number) =>
		idempotentMutation(`capital-assets:delete:${id}`, (key) =>
			api.delete(`/capital-assets/${id}`, { headers: { 'Idempotency-Key': key } })
		)
};
