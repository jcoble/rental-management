import type { Lease, LeaseQuestionResponse } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const leases = {
	list: (portfolioId: number, params?: ListParams & { tenantId?: number; propertyId?: number }) => {
		const { tenantId, propertyId, ...list } = params ?? {};
		return api.get<Lease[]>(`/leases${buildListQuery(list, { portfolioId, tenantId, propertyId })}`);
	},
	get: (id: number) => api.get<Lease>(`/leases/${id}`),
	create: (data: Record<string, unknown>) => api.post<Lease>('/leases', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Lease>(`/leases/${id}`, data),
	delete: (id: number) => api.delete(`/leases/${id}`),
	ledger: (id: number) => api.get(`/leases/${id}/ledger`),
	ask: (id: number, question: string) =>
		api.post<LeaseQuestionResponse>(`/leases/${id}/ask`, { question }),
};
