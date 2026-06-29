import type { Tenant } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export interface TenantListResponse {
	items: Tenant[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface TenantListParams extends ListParams {
	availableForLease?: boolean;
}

export interface GrantPortalAccessResponse {
	/** 'Created' when a new login was provisioned, 'AlreadyExisted' when one was already present. */
	status: string;
	alreadyExisted: boolean;
	email?: string;
}

export const tenants = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Tenant[]>(`/tenants${buildListQuery(params, { portfolioId })}`),
	listPage: (portfolioId: number, params?: TenantListParams) =>
		api.get<TenantListResponse>(
			`/tenants/page${buildListQuery(params, {
				portfolioId,
				availableForLease: params?.availableForLease ? 'true' : undefined,
			})}`
		),
	get: (id: number) => api.get<Tenant>(`/tenants/${id}`),
	create: (data: Record<string, unknown>) => api.post<Tenant>('/tenants', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Tenant>(`/tenants/${id}`, data),
	delete: (id: number) => api.delete(`/tenants/${id}`),
	grantPortalAccess: (id: number) =>
		api.post<GrantPortalAccessResponse>(`/tenants/${id}/portal-access`),
};
