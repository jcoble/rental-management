import type { Tenant } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { idempotentMutation } from '../idempotency';

export interface TenantListResponse {
	items: Tenant[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface TenantListParams extends ListParams {
	availableForLease?: boolean;
	includeLeaseManagementId?: number;
}

export const tenants = {
	list: (portfolioId: number, params?: ListParams) =>
		api.get<Tenant[]>(`/tenants${buildListQuery(params, { portfolioId })}`),
	listPage: (portfolioId: number, params?: TenantListParams) =>
		api.get<TenantListResponse>(
			`/tenants/page${buildListQuery(params, {
				portfolioId,
				availableForLease: params?.availableForLease ? 'true' : undefined,
				includeLeaseManagementId: params?.includeLeaseManagementId,
			})}`
		),
	get: (id: number) => api.get<Tenant>(`/tenants/${id}`),
	create: (data: Record<string, unknown>) =>
		idempotentMutation(`tenants:create:${JSON.stringify(data)}`, (key) =>
			api.post<Tenant>('/tenants', data, { headers: { 'Idempotency-Key': key } })
		),
	createGuidedSetupBatch: (reviewedTenants: Record<string, unknown>[]) =>
		idempotentMutation(`tenants:guided-setup:${JSON.stringify(reviewedTenants)}`, (key) =>
			api.post<Tenant[]>(
				'/tenants/guided-setup',
				{ tenants: reviewedTenants },
				{ headers: { 'Idempotency-Key': key } }
			)
		),
	update: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`tenants:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<Tenant>(`/tenants/${id}`, data, { headers: { 'Idempotency-Key': key } })
		),
	delete: (id: number) =>
		idempotentMutation(`tenants:delete:${id}`, (key) =>
			api.delete(`/tenants/${id}`, { headers: { 'Idempotency-Key': key } })
		),
};
