import { api } from '../client';
import { idempotentMutation } from '../idempotency';
import type {
	Appointment,
	LeaseQuestionResponse,
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
export interface PortalTenantAccount {
	tenantAccountId: number;
	tenantAccountPublicId: string;
	leaseManagementId: number;
	leaseManagementPublicId: string;
	propertyId: number;
	propertyName: string;
	unitId: number;
	unitNumber: string;
	accountNumber: string;
	relationshipNumber: string;
	lifecycle: string;
	currency: string;
	openedAtUtc: string;
	closedAtUtc?: string | null;
	effectiveNowUtc: string;
	businessDate: string;
	totalDebits: number;
	totalCredits: number;
	receivableBalance: number;
	unappliedCredit: number;
	pastDueAmount: number;
	pastDueCount: number;
	nextDueOn?: string | null;
	nextDueAmount: number;
	condition: string;
	lastReceiptOn?: string | null;
	lastReceiptAmount?: number | null;
	currentAgreement?: PortalLeaseAgreement | null;
	deposit?: PortalTenantAccountDeposit | null;
}

export interface PortalTenantLedgerEntry {
	tenantAccountId: number;
	leaseManagementId: number;
	tenantLedgerEntryId: number;
	publicId: string;
	entryType: string;
	direction: string;
	amount: number;
	currency: string;
	effectiveOn: string;
	dueOn?: string | null;
	postedAtUtc: string;
	description: string;
	businessKey: string;
	transferPublicId?: string | null;
	leaseAgreementId?: number | null;
	leaseAddendumId?: number | null;
	reversesEntryId?: number | null;
	providerPaymentAttemptId?: number | null;
	sourceStoredFileId?: number | null;
}

export interface PortalTenantCharge extends Omit<PortalTenantLedgerEntry, 'amount' | 'businessKey'> {
	originalAmount: number;
	reversedAmount: number;
	netAllocations: number;
	openAmount: number;
	isPastDue: boolean;
}

export interface PortalTenantAccountDeposit {
	tenantAccountId: number;
	leaseManagementId: number;
	securityDepositAccountId: number;
	originatingAgreementId: number;
	currency: string;
	createdAtUtc: string;
	effectiveNowUtc: string;
	businessDate: string;
	totalReceived: number;
	totalDeductions: number;
	totalRefunded: number;
	totalTransferredIn: number;
	totalTransferredOut: number;
	netAdjustments: number;
	heldBalance: number;
	status: string;
}

export interface PortalPage<T> {
	items: T[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface PortalAccountChildPage<T> extends PortalPage<T> {
	tenantAccountId: number;
	leaseManagementId: number;
}

export interface PortalListParams {
	skip?: number;
	take?: number;
	search?: string;
	sort?: string;
	from?: string;
	to?: string;
}

export interface PortalTenantAccountListParams extends PortalListParams {
	lifecycle?: string;
	closed?: boolean;
}

export interface PortalTenantLedgerEntryListParams extends PortalListParams {
	entryType?: string;
	direction?: string;
}

export interface PortalTenantChargeListParams extends PortalListParams {
	entryType?: string;
	isPastDue?: boolean;
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

function queryString(params: object): string {
	const query = new URLSearchParams();
	for (const [key, value] of Object.entries(params)) {
		if (value !== undefined && value !== null && value !== '') query.set(key, String(value));
	}
	const text = query.toString();
	return text ? `?${text}` : '';
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
	tenantAccountsPage: (params: PortalTenantAccountListParams = {}) =>
		api.get<PortalPage<PortalTenantAccount>>(`/portal/tenant-accounts/page${queryString(params)}`),
	tenantAccount: (tenantAccountId: number) =>
		api.get<PortalTenantAccount>(`/portal/tenant-accounts/${tenantAccountId}`),
	tenantAccountEntriesPage: (tenantAccountId: number, params: PortalTenantLedgerEntryListParams = {}) =>
		api.get<PortalAccountChildPage<PortalTenantLedgerEntry>>(
			`/portal/tenant-accounts/${tenantAccountId}/entries/page${queryString(params)}`
		),
	tenantAccountChargesPage: (tenantAccountId: number, params: PortalTenantChargeListParams = {}) =>
		api.get<PortalAccountChildPage<PortalTenantCharge>>(
			`/portal/tenant-accounts/${tenantAccountId}/charges/page${queryString(params)}`
		),
	tenantAccountDeposit: (tenantAccountId: number) =>
		api.get<PortalTenantAccountDeposit>(`/portal/tenant-accounts/${tenantAccountId}/deposit`),
	appointments: () => api.get<Appointment[]>('/portal/appointments'),
	workOrders: () => api.get<WorkOrder[]>('/portal/work-orders'),
	/** One of the tenant's own work orders plus its status timeline (404 if not theirs). */
	workOrder: (id: number) => api.get<WorkOrderDetail>(`/portal/work-orders/${id}`),
	createTenantWorkOrder: (data: Record<string, unknown>) =>
		idempotentMutation(`portal:work-order:create:${JSON.stringify(data)}`, (key) =>
			api.post<WorkOrder>('/portal/tenant/work-orders', data, {
				headers: { 'Idempotency-Key': key }
			})
		),

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
		/** Mark one thread read, then fetch its full history. */
		get: async (id: number) => {
			await idempotentMutation(`portal:conversations:read:${id}`, (operationKey) =>
				api.post<void>(`/portal/conversations/${id}/read`, {}, {
					headers: { 'Idempotency-Key': operationKey }
				})
			);
			return api.get<Conversation>(`/portal/conversations/${id}`);
		},
		/** Start a new thread to the landlord. */
		start: (data: StartPortalConversationRequest) => api.post<Conversation>('/portal/conversations', data),
		/** Reply to an existing thread. */
		sendMessage: (id: number, data: SendPortalMessageRequest) =>
			api.post<Conversation>(`/portal/conversations/${id}/messages`, data)
	}
};
