import type { Property, Unit } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const properties = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Property[]>(`/properties${buildListQuery(params, { portfolioId })}`),
	get: (id: number) => api.get<Property>(`/properties/${id}`),
	create: (data: Record<string, unknown>) => api.post<Property>('/properties', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Property>(`/properties/${id}`, data),
	delete: (id: number) => api.delete(`/properties/${id}`),
	listUnits: (propertyId: number) => api.get<Unit[]>(`/properties/${propertyId}/units`),
	createUnit: (propertyId: number, data: Record<string, unknown>) => api.post<Unit>(`/properties/${propertyId}/units`, data),
	updateUnit: (id: number, data: Record<string, unknown>) => api.patch<Unit>(`/units/${id}`, data),
	deleteUnit: (id: number) => api.delete(`/units/${id}`),
};
