import type { Owner } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export interface OwnerListResponse {
	items: Owner[];
	totalCount: number;
	skip: number;
	take: number;
}

export const owners = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Owner[]>(`/owner-entities${buildListQuery(params, { portfolioId })}`),
	listPage: (portfolioId: number, params?: ListParams) =>
		api.get<OwnerListResponse>(`/owner-entities/page${buildListQuery(params, { portfolioId })}`),
	get: (id: number) => api.get<Owner>(`/owner-entities/${id}`),
	create: (data: Record<string, unknown>) => api.post<Owner>('/owner-entities', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Owner>(`/owner-entities/${id}`, data),
	delete: (id: number, options?: { clearPropertyAssignments?: boolean }) =>
		api.delete(`/owner-entities/${id}${options?.clearPropertyAssignments ? '?clearPropertyAssignments=true' : ''}`),
	emailStatement: (ownerId: number, year: number) =>
		api.post<void>(`/accounting/owner-statements/${ownerId}/email?year=${year}`, {}),
};
