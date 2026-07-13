import { api } from '../client';
import type {
	Appointment,
	LeaseQuestionResponse,
	PaymentStatus,
	PaymentType,
	WorkOrder,
	WorkOrderDetail
} from '$lib/types';
import type { Conversation, ConversationMessage, ConversationSummary } from './messages';

// Re-export the shared conversation types so portal consumers can import them
// from here without reaching into the landlord-side module.
export type { Conversation, ConversationMessage, ConversationSummary };

/** Tenant starts a topic thread to their landlord/management. No channels, no recipient. */
export interface StartPortalConversationRequest {
	operationKey: string;
	subject: string;
	body: string;
}

/** Tenant replies to an existing thread. No channels (portal-only on the tenant side). */
export interface SendPortalMessageRequest {
	operationKey: string;
	body: string;
}

/**
 * Optional return URLs Stripe sends the tenant to after a hosted Checkout/setup
 * session. Defaults to `/portal/payments?checkout=success|cancel` server-side.
 */
export interface CheckoutUrls {
	successUrl?: string;
	cancelUrl?: string;
}

/** Hosted Checkout response — redirect the browser straight to `checkoutUrl`. */
export interface CheckoutSession {
	checkoutUrl: string;
}

/** Autopay enrollment state for one canonical tenant account. */
export interface AutopayStatus {
	tenantAccountId: number;
	active: boolean;
	onlinePaymentsAvailable: boolean;
	enrolledAt?: string | null;
}

/** Tenant-facing payable charge shape. This is not the management receipt projection. */
export interface PortalPayment {
	id: number;
	tenantAccountId: number;
	leaseManagementId: number;
	paymentType: PaymentType;
	status: PaymentStatus;
	amount: number;
	amountPaid?: number | null;
	dueDate: string;
}

export interface PortalLeaseAgreement {
	leaseAgreementId: number;
	versionNumber: number;
	agreementNumber: string;
	agreementStatus: string;
	isGoverning: boolean;
	changeType: string;
	termType: string;
	termStartOn: string;
	termEndOn?: string | null;
	baseRentAmount: number;
	securityDepositObligation: number;
	lateFeeAmount: number;
	rentDueDay: number;
	currency: string;
	fullyExecutedAtUtc?: string | null;
	executedStoredFileId?: number | null;
}

export interface PortalLeaseRelationship {
	leaseManagementId: number;
	leaseManagementPublicId: string;
	portfolioId: number;
	propertyId: number;
	unitId: number;
	tenantId: number;
	tenantAccountId?: number | null;
	relationshipNumber: string;
	lifecycle: string;
	propertyName: string;
	unitNumber: string;
	tenantName: string;
	agreement?: PortalLeaseAgreement | null;
}

export const portal = {
	overview: () => api.get('/portal/overview'),
	leases: () => api.get<PortalLeaseRelationship[]>('/portal/leases'),
	/**
	 * Ask a plain-English question grounded in the tenant's OWN lease. Omit `leaseId` to use the
	 * tenant's most relevant lease. Scoped server-side to the signed-in tenant; 404 if they have no
	 * lease (or the lease isn't theirs).
	 */
	askLease: (question: string, leaseManagementId?: number) =>
		api.post<LeaseQuestionResponse>(
			`/portal/lease/ask${leaseManagementId != null ? `?leaseManagementId=${leaseManagementId}` : ''}`,
			{ question }
		),
	balance: () => api.get('/portal/balance'),
	payments: () => api.get<PortalPayment[]>('/portal/payments'),
	appointments: () => api.get<Appointment[]>('/portal/appointments'),
	workOrders: () => api.get<WorkOrder[]>('/portal/work-orders'),
	/** One of the tenant's own work orders plus its status timeline (404 if not theirs). */
	workOrder: (id: number) => api.get<WorkOrderDetail>(`/portal/work-orders/${id}`),
	createTenantWorkOrder: (data: Record<string, unknown>) => api.post<WorkOrder>('/portal/tenant/work-orders', data),

	/**
	 * Start a Stripe-hosted Checkout for one of the tenant's owed payments and
	 * return the URL to redirect to. Throws an {@link ApiError} with status 503
	 * when online payments aren't configured, or 404 if the payment isn't theirs.
	 */
	payCheckout: (tenantAccountId: number, chargeLedgerEntryId: number, body: CheckoutUrls = {}) =>
		api.post<CheckoutSession>(
			`/portal/tenant-accounts/${tenantAccountId}/charges/${chargeLedgerEntryId}/checkout`,
			body
		),
	/** Current autopay enrollment for a canonical tenant account. */
	autopayStatus: (tenantAccountId: number) =>
		api.get<AutopayStatus>(`/portal/tenant-accounts/${tenantAccountId}/autopay`),
	/**
	 * Begin autopay enrollment via a setup-mode Checkout; redirect to the returned
	 * `checkoutUrl`. Throws 503 when online payments aren't configured.
	 */
	autopayEnroll: (tenantAccountId: number, body: { operationKey: string } & CheckoutUrls) =>
		api.post<CheckoutSession>(`/portal/tenant-accounts/${tenantAccountId}/autopay/enroll`, body),
	/** Cancel autopay for a canonical tenant account. */
	autopayCancel: (tenantAccountId: number) =>
		api.post<AutopayStatus>(`/portal/tenant-accounts/${tenantAccountId}/autopay/cancel`),

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
		start: (data: StartPortalConversationRequest) => api.post<Conversation>('/portal/conversations', data),
		/** Reply to an existing thread. */
		sendMessage: (id: number, data: SendPortalMessageRequest) =>
			api.post<Conversation>(`/portal/conversations/${id}/messages`, data)
	}
};
