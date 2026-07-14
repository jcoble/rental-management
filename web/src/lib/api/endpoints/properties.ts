import type { Property, Unit } from '$lib/types';
import { api } from '../client';
import { idempotentMutation } from '../idempotency';
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
	create: (data: Record<string, unknown>) =>
		idempotentMutation(`properties:create:${JSON.stringify(data)}`, (key) =>
			api.post<Property>('/properties', data, { headers: { 'Idempotency-Key': key } })
		),
	update: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`properties:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<Property>(`/properties/${id}`, data, { headers: { 'Idempotency-Key': key } })
		),
	delete: (id: number) =>
		idempotentMutation(`properties:delete:${id}`, (key) =>
			api.delete(`/properties/${id}`, { headers: { 'Idempotency-Key': key } })
		),
	listUnits: (propertyId: number) => api.get<Unit[]>(`/units${buildListQuery(undefined, { propertyId })}`),
	createUnit: (propertyId: number, data: Record<string, unknown>) =>
		idempotentMutation(`units:create:${propertyId}:${JSON.stringify(data)}`, (operationKey) =>
			api.post<Unit>('/units', { propertyId, ...data }, {
				headers: { 'Idempotency-Key': operationKey }
			})
		),
	updateUnit: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`units:update:${id}:${JSON.stringify(data)}`, (operationKey) =>
			api.patch<Unit>(`/units/${id}`, data, {
				headers: { 'Idempotency-Key': operationKey }
			})
		),
	deleteUnit: (id: number) =>
		idempotentMutation(`units:delete:${id}`, (operationKey) =>
			api.delete(`/units/${id}`, { headers: { 'Idempotency-Key': operationKey } })
		),
};
