import { api, downloadFile } from '../client';
import { idempotentMutation } from '../idempotency';
import type {
	Appointment,
	LeaseQuestionResponse,
	WorkOrder,
	WorkOrderDetail,
} from '$lib/types';
import type {
	Conversation,
	ConversationMessage,
	ConversationSummary,
} from './messages';

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
export interface SendTenantConversationMessageRequest {
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

export type PortalTenantAccountHistoryPeriod =
	| 'currentMonth'
	| 'previousMonth'
	| 'last3Months'
	| 'thisYear'
	| 'all'
	| 'custom';

export interface PortalTenantAccountHistoryItem {
	tenantLedgerEntryId: number;
	entryType: string;
	direction: string;
	displayType: string;
	description: string;
	effectiveOn: string;
	dueOn?: string | null;
	postedAtUtc: string;
	signedAmount: number;
	runningBalance: number;
	openAmount: number;
	payable: boolean;
	reversesEntryId?: number | null;
	reversedByEntryId?: number | null;
	isFocused: boolean;
}

export interface PortalTenantLedgerAllocationRef {
	targetSourceId: number;
	targetPublicId: string;
	targetDescription: string;
	amount: number;
	effectiveOn: string;
}

export interface PortalTenantLedgerRow {
	tenantLedgerEntryId: number;
	publicId: string;
	effectiveOn: string;
	postedAtUtc: string;
	type: string;
	description: string;
	chargeAmount: number;
	paymentAmount: number;
	creditAmount: number;
	runningAmountOwed: number;
	dueOn: string | null;
	openAmount: number;
	status: string;
	paymentMethod: string | null;
	reference: string | null;
	sourceDocumentContext: string | null;
	allocations: PortalTenantLedgerAllocationRef[];
	reversesEntryId: number | null;
	replacedByEntryId: number | null;
	currency: string;
}

export interface PortalTenantMonthSummary {
	year: number;
	month: number;
	currency: string;
	openingBalance: number;
	chargeAmount: number;
	paymentAmount: number;
	creditAmount: number;
	closingBalance: number;
}

export interface PortalTenantStatement {
	tenantAccountId: number;
	periodFrom: string | null;
	periodTo: string | null;
	openingBalance: number;
	closingBalance: number;
	rows: PortalTenantLedgerRow[];
	monthSummaries: PortalTenantMonthSummary[];
}

export interface PortalTenantAccountHistory {
	tenantAccountId: number;
	leaseManagementId: number;
	currency: string;
	businessDate: string;
	period: PortalTenantAccountHistoryPeriod;
	periodFrom?: string | null;
	periodTo: string;
	currentDue: number;
	beginningBalance: number;
	closingBalance: number;
	items: PortalTenantAccountHistoryItem[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface PortalTenantCharge
	extends Omit<PortalTenantLedgerEntry, 'amount' | 'businessKey'> {
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

export interface PortalTenantAccountHistoryParams {
	period?: PortalTenantAccountHistoryPeriod;
	from?: string;
	to?: string;
	skip?: number;
	take?: number;
	entry?: number;
}

export interface PortalTenantChargeListParams extends PortalListParams {
	entryType?: string;
	isPastDue?: boolean;
}

export interface PortalTenantWorkOrderListParams extends PortalListParams {
	status?: string;
	openOnly?: boolean;
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
	executedDocumentAvailable: boolean;
	executedDocumentFileName?: string | null;
	executedDocumentContentType?: string | null;
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
		if (value !== undefined && value !== null && value !== '')
			query.set(key, String(value));
	}
	const text = query.toString();
	return text ? `?${text}` : '';
}

function saveBlob(blob: Blob, fileName: string): void {
	const objectUrl = URL.createObjectURL(blob);
	const anchor = document.createElement('a');
	anchor.href = objectUrl;
	anchor.download = fileName;
	document.body.appendChild(anchor);
	anchor.click();
	document.body.removeChild(anchor);
	URL.revokeObjectURL(objectUrl);
}

export async function downloadPortalExecutedAgreement(
	leaseManagementId: number,
	leaseAgreementId: number,
	fileName: string,
	contentType: string
): Promise<void> {
	const blob = await downloadFile(
		`/portal/leases/${leaseManagementId}/agreements/${leaseAgreementId}/executed-document`
	);
	saveBlob(
		blob.type ? blob : new Blob([blob], { type: contentType }),
		fileName
	);
}

export const portal = {
	overview: () => api.get('/portal/overview'),
	leases: () => api.get<PortalLeaseRelationship[]>('/portal/leases'),
	downloadExecutedAgreement: downloadPortalExecutedAgreement,
	/**
	 * Ask a plain-English question grounded in the tenant's OWN lease. Omit `leaseId` to use the
	 * tenant's most relevant lease. Scoped server-side to the signed-in tenant; 404 if they have no
	 * lease (or the lease isn't theirs).
	 */
	askLease: (question: string, leaseManagementId?: number) =>
		api.post<LeaseQuestionResponse>(
			`/portal/lease/ask${
				leaseManagementId != null
					? `?leaseManagementId=${leaseManagementId}`
					: ''
			}`,
			{ question }
		),
	tenantAccountsPage: (params: PortalTenantAccountListParams = {}) =>
		api.get<PortalPage<PortalTenantAccount>>(
			`/portal/tenant-accounts/page${queryString(params)}`
		),
	tenantAccount: (tenantAccountId: number) =>
		api.get<PortalTenantAccount>(`/portal/tenant-accounts/${tenantAccountId}`),
	tenantAccountEntriesPage: (
		tenantAccountId: number,
		params: PortalTenantLedgerEntryListParams = {}
	) =>
		api.get<PortalAccountChildPage<PortalTenantLedgerEntry>>(
			`/portal/tenant-accounts/${tenantAccountId}/entries/page${queryString(
				params
			)}`
		),
	tenantAccountHistory: (
		tenantAccountId: number,
		params: PortalTenantAccountHistoryParams = {}
	) =>
		api.get<PortalTenantAccountHistory>(
			`/portal/tenant-accounts/${tenantAccountId}/history${queryString(params)}`
		),
	tenantAccountLedger: (
		tenantAccountId: number,
		params: { from?: string; to?: string; skip?: number; take?: number } = {}
	) => api.get<PortalPage<PortalTenantLedgerRow>>(
		`/portal/tenant-accounts/${tenantAccountId}/ledger${queryString(params)}`
	),
	tenantAccountMonthSummary: (
		tenantAccountId: number,
		params: { from?: string; to?: string } = {}
	) => api.get<PortalTenantMonthSummary[]>(
		`/portal/tenant-accounts/${tenantAccountId}/month-summary${queryString(params)}`
	),
	tenantAccountLedgerEntry: (tenantAccountId: number, entryId: number) =>
		api.get<PortalTenantLedgerRow>(
			`/portal/tenant-accounts/${tenantAccountId}/ledger/${entryId}`
		),
	tenantAccountStatement: (
		tenantAccountId: number,
		params: { from?: string; to?: string } = {}
	) => api.get<PortalTenantStatement>(
		`/portal/tenant-accounts/${tenantAccountId}/statement${queryString(params)}`
	),
	downloadTenantAccountStatementCsv: (
		tenantAccountId: number,
		params: { from?: string; to?: string } = {}
	) =>
		downloadFile(`/portal/tenant-accounts/${tenantAccountId}/statement.csv${queryString(params)}`),
	tenantAccountChargesPage: (
		tenantAccountId: number,
		params: PortalTenantChargeListParams = {}
	) =>
		api.get<PortalAccountChildPage<PortalTenantCharge>>(
			`/portal/tenant-accounts/${tenantAccountId}/charges/page${queryString(
				params
			)}`
		),
	tenantAccountDeposit: (tenantAccountId: number) =>
		api.get<PortalTenantAccountDeposit>(
			`/portal/tenant-accounts/${tenantAccountId}/deposit`
		),
	appointments: () => api.get<Appointment[]>('/portal/appointments'),
	workOrders: (params: PortalTenantWorkOrderListParams = {}) =>
		api.get<PortalPage<WorkOrder>>(`/portal/work-orders${queryString(params)}`),
	/** One of the tenant's own work orders plus its status timeline (404 if not theirs). */
	workOrder: (id: number) =>
		api.get<WorkOrderDetail>(`/portal/work-orders/${id}`),
	updateTenantWorkOrder: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(
			`portal:work-order:update:${id}:${JSON.stringify(data)}`,
			(key) =>
				api.patch<void>(`/portal/work-orders/${id}`, data, {
					headers: { 'Idempotency-Key': key },
				})
		),
	commentTenantWorkOrder: (id: number, data: { body: string }) =>
		idempotentMutation(
			`portal:work-order:comment:${id}:${JSON.stringify(data)}`,
			(key) =>
				api.post<void>(`/portal/work-orders/${id}/comments`, data, {
					headers: { 'Idempotency-Key': key },
				})
		),
	cancelTenantWorkOrder: (id: number, data: { note?: string }) =>
		idempotentMutation(
			`portal:work-order:cancel:${id}:${JSON.stringify(data)}`,
			(key) =>
				api.post<void>(`/portal/work-orders/${id}/cancel`, data, {
					headers: { 'Idempotency-Key': key },
				})
		),
	createTenantWorkOrder: (data: Record<string, unknown>) =>
		idempotentMutation(
			`portal:work-order:create:${JSON.stringify(data)}`,
			(key) =>
				api.post<WorkOrder>('/portal/tenant/work-orders', data, {
					headers: { 'Idempotency-Key': key },
				})
		),

	/**
	 * Start a Stripe-hosted Checkout for one of the tenant's owed payments and
	 * return the URL to redirect to. Throws an {@link ApiError} with status 503
	 * when online payments aren't configured, or 404 if the payment isn't theirs.
	 */
	payCheckout: (
		tenantAccountId: number,
		chargeLedgerEntryId: number,
		body: CheckoutUrls = {}
	) =>
		api.post<CheckoutSession>(
			`/portal/tenant-accounts/${tenantAccountId}/charges/${chargeLedgerEntryId}/checkout`,
			body
		),
	/** Current autopay enrollment for a canonical tenant account. */
	autopayStatus: (tenantAccountId: number) =>
		api.get<AutopayStatus>(
			`/portal/tenant-accounts/${tenantAccountId}/autopay`
		),
	/**
	 * Begin autopay enrollment via a setup-mode Checkout; redirect to the returned
	 * `checkoutUrl`. Throws 503 when online payments aren't configured.
	 */
	autopayEnroll: (
		tenantAccountId: number,
		body: { operationKey: string } & CheckoutUrls
	) =>
		api.post<CheckoutSession>(
			`/portal/tenant-accounts/${tenantAccountId}/autopay/enroll`,
			body
		),
	/** Cancel autopay for a canonical tenant account. */
	autopayCancel: (tenantAccountId: number) =>
		idempotentMutation(`portal:autopay:cancel:${tenantAccountId}`, (key) =>
			api.post<AutopayStatus>(
				`/portal/tenant-accounts/${tenantAccountId}/autopay/cancel`,
				{},
				{
					headers: { 'Idempotency-Key': key },
				}
			)
		),

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
			await idempotentMutation(
				`portal:conversations:read:${id}`,
				(operationKey) =>
					api.post<void>(
						`/portal/conversations/${id}/read`,
						{},
						{
							headers: { 'Idempotency-Key': operationKey },
						}
					)
			);
			return api.get<Conversation>(`/portal/conversations/${id}`);
		},
		/** Start a new thread to the landlord. */
		start: (data: StartPortalConversationRequest) =>
			api.post<Conversation>('/portal/conversations', data),
		/** Reply to an existing thread. */
		sendMessage: (id: number, data: SendTenantConversationMessageRequest) =>
			api.post<Conversation>(`/portal/conversations/${id}/messages`, data),
	},
};
