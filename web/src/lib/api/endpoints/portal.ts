import type { PortalMessage } from '$lib/types';
import { api } from '../client';

export const portal = {
	overview: () => api.get('/portal/overview'),
	messages: () => api.get<PortalMessage[]>('/portal/messages'),
	createMessage: (data: Record<string, unknown>) => api.post<PortalMessage>('/portal/messages', data),
	updateMessage: (id: number, data: Record<string, unknown>) => api.patch<PortalMessage>(`/portal/messages/${id}`, data),
	createTenantWorkOrder: (data: Record<string, unknown>) => api.post('/portal/tenant/work-orders', data),
};
