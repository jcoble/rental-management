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

/** Portal-login state for a tenant. */
export type PortalAccessState = 'none' | 'active' | 'disabled';

export interface PortalAccessResponse {
	/** The resulting portal-login state after the toggle. */
	portalAccess: PortalAccessState;
	/** The email the tenant signs in with, when known. */
	email?: string;
}

export interface PortalInviteResponse {
	/** The email the invite was sent to. */
	email?: string;
	/** True when the tenant already had a portal login (this was a resend). */
	alreadyExisted: boolean;
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
	setPortalAccess: (id: number, enabled: boolean) =>
		api.post<PortalAccessResponse>(`/tenants/${id}/portal-access`, { enabled }),
	sendPortalInvite: (id: number) =>
		api.post<PortalInviteResponse>(`/tenants/${id}/portal-invite`),
};
