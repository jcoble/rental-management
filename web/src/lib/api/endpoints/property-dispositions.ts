import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export interface PropertyDisposition {
	id: number;
	portfolioId: number;
	propertyId: number;
	propertyName?: string | null;
	closedOnDate: string;
	salePrice: number;
	sellingCosts: number;
	netSaleProceeds: number;
	purchasePrice: number;
	landValue: number;
	buildingBasis: number;
	accumulatedDepreciationBeforeSale: number;
	saleYearDepreciation: number;
	totalDepreciation: number;
	adjustedBasis: number;
	gainLoss: number;
	unrecapturedSection1250Gain: number;
	buyerName?: string | null;
	memo?: string | null;
	createdAt: string;
	updatedAt: string;
	testId: string;
}

export interface PropertyDispositionListParams extends ListParams {
	propertyId?: number;
	year?: number;
}

export interface PropertyDispositionListResponse {
	items: PropertyDisposition[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface CreatePropertyDispositionRequest {
	propertyId: number;
	closedOnDate: string;
	salePrice: number;
	sellingCosts: number;
	buyerName?: string | null;
	memo?: string | null;
}

const queryParams = (params?: PropertyDispositionListParams) => ({
	propertyId: params?.propertyId,
	year: params?.year
});

export const propertyDispositions = {
	list: (params?: PropertyDispositionListParams) =>
		api.get<PropertyDisposition[]>(`/property-dispositions${buildListQuery(params, queryParams(params))}`),
	listPage: (params?: PropertyDispositionListParams) =>
		api.get<PropertyDispositionListResponse>(
			`/property-dispositions/page${buildListQuery(params, queryParams(params))}`
		),
	get: (id: number) => api.get<PropertyDisposition>(`/property-dispositions/${id}`),
	create: (data: CreatePropertyDispositionRequest) =>
		api.post<PropertyDisposition>('/property-dispositions', data),
	update: (id: number, data: Record<string, unknown>) =>
		api.patch<PropertyDisposition>(`/property-dispositions/${id}`, data),
	remove: (id: number) => api.delete(`/property-dispositions/${id}`)
};
