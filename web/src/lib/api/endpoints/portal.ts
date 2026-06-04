import { api } from '../client';
import type { Lease, Payment, WorkOrder, WorkOrderDetail } from '$lib/types';
import type {
	Conversation,
	ConversationMessage,
	ConversationSummary
} from './messages';

// Re-export the shared conversation types so portal consumers can import them
// from here without reaching into the landlord-side module.
export type { Conversation, ConversationMessage, ConversationSummary };

/** Tenant starts a topic thread to their landlord/management. No channels, no recipient. */
export interface StartPortalConversationRequest {
	subject: string;
	body: string;
}

/** Tenant replies to an existing thread. No channels (portal-only on the tenant side). */
export interface SendPortalMessageRequest {
	body: string;
}

export const portal = {
	overview: () => api.get('/portal/overview'),
	leases: () => api.get<Lease[]>('/portal/leases'),
	balance: () => api.get('/portal/balance'),
	payments: () => api.get<Payment[]>('/portal/payments'),
	workOrders: () => api.get<WorkOrder[]>('/portal/work-orders'),
	/** One of the tenant's own work orders plus its status timeline (404 if not theirs). */
	workOrder: (id: number) => api.get<WorkOrderDetail>(`/portal/work-orders/${id}`),
	createTenantWorkOrder: (data: Record<string, unknown>) => api.post<WorkOrder>('/portal/tenant/work-orders', data),

	/**
	 * Tenant-scoped messaging. The tenant's JWT scopes every call to their own
	 * threads; the recipient is always their landlord/management, so there is no
	 * recipient or channel picker on this side.
	 */
	conversations: {
		/** List the tenant's conversations, newest activity first. */
		list: () => api.get<ConversationSummary[]>('/portal/conversations'),
		/** Fetch one thread's full history. Marks it read for the tenant. */
		get: (id: number) => api.get<Conversation>(`/portal/conversations/${id}`),
		/** Start a new thread to the landlord. */
		start: (data: StartPortalConversationRequest) =>
			api.post<Conversation>('/portal/conversations', data),
		/** Reply to an existing thread. */
		sendMessage: (id: number, data: SendPortalMessageRequest) =>
			api.post<Conversation>(`/portal/conversations/${id}/messages`, data)
	}
};
