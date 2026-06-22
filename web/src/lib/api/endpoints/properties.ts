import type { Property, Unit } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export interface PropertyListParams extends ListParams {
	type?: string;
	status?: string;
}

export interface PropertyListResponse {
	items: Property[];
	totalCount: number;
	skip: number;
	take: number;
}

export const properties = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Property[]>(`/properties${buildListQuery(params, { portfolioId })}`),
	listPage: (portfolioId: number, params?: PropertyListParams) =>
		api.get<PropertyListResponse>(
			`/properties/page${buildListQuery(params, {
				portfolioId,
				type: params?.type,
				status: params?.status
			})}`
		),
	get: (id: number) => api.get<Property>(`/properties/${id}`),
	create: (data: Record<string, unknown>) => api.post<Property>('/properties', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Property>(`/properties/${id}`, data),
	delete: (id: number) => api.delete(`/properties/${id}`),
	listUnits: (propertyId: number) => api.get<Unit[]>(`/units${buildListQuery(undefined, { propertyId })}`),
	createUnit: (propertyId: number, data: Record<string, unknown>) => api.post<Unit>('/units', { propertyId, ...data }),
	updateUnit: (id: number, data: Record<string, unknown>) => api.patch<Unit>(`/units/${id}`, data),
	deleteUnit: (id: number) => api.delete(`/units/${id}`),
};
