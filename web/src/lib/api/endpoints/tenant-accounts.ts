import { api } from '../client';

export interface TenantMoneyCommandResponse<T> {
	value: T;
	replayed: boolean;
}

export interface TenantAccountListItem {
	tenantAccountId: number;
	leaseManagementId: number;
	propertyId: number;
	propertyName: string;
	unitId: number;
	unitNumber: string;
	accountNumber: string;
	relationshipNumber: string;
	primaryTenantName?: string | null;
	lifecycle: string;
	currency: string;
	receivableBalance: number;
	pastDueAmount: number;
	pastDueCount: number;
}

export interface TenantAccountDetail {
	tenantAccountId: number;
	leaseManagementId: number;
	propertyId: number;
	propertyName: string;
	unitId: number;
	unitNumber: string;
	accountNumber: string;
	relationshipNumber: string;
	currency: string;
}

export interface TenantLedgerEntry {
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
	reversesEntryId?: number | null;
	hasReversal: boolean;
	providerPaymentAttemptId?: number | null;
	sourceStoredFileId?: number | null;
}

export interface TenantLedgerEntryGlobal extends TenantLedgerEntry {
	propertyId: number;
	propertyName: string;
	unitId: number;
	unitNumber: string;
	accountNumber: string;
	relationshipNumber: string;
	primaryTenantName?: string | null;
}

export interface TenantLedgerProviderAttempt {
	providerPaymentAttemptId: number;
	provider: string;
	providerReference?: string | null;
	state: string;
	paymentMethodSummary?: string | null;
	payerName?: string | null;
	checkNumber?: string | null;
	bankName?: string | null;
}

export interface TenantLedgerEntryDetail extends TenantLedgerEntryGlobal {
	portfolioId: number;
	tenantName?: string | null;
	providerAttempt?: TenantLedgerProviderAttempt | null;
	sourceFile?: {
		sourceStoredFileId: number;
		fileName: string;
		contentType: string;
	} | null;
}

export interface Page<T> {
	items: T[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface EntryPage<T> extends Page<T> {
	tenantAccountId?: number;
	leaseManagementId?: number;
}

export interface TenantCharge {
	tenantAccountId: number;
	leaseManagementId: number;
	tenantLedgerEntryId: number;
	entryType: string;
	direction: string;
	currency: string;
	effectiveOn: string;
	dueOn?: string | null;
	description: string;
	originalAmount: number;
	reversedAmount: number;
	netAllocations: number;
	openAmount: number;
	isPastDue: boolean;
}

export interface TenantAccountDeposit {
	securityDepositAccountId: number;
	tenantAccountId: number;
	leaseManagementId: number;
	propertyId: number;
	unitId: number;
	accountNumber: string;
	relationshipNumber: string;
	currency: string;
	totalReceived: number;
	totalDeductions: number;
	totalRefunded: number;
	heldBalance: number;
	status: string;
}

export interface TenantChargePage extends Page<TenantCharge> {
	tenantAccountId: number;
	leaseManagementId: number;
}

export interface ListParams {
	skip?: number;
	take?: number;
	search?: string;
	sort?: string;
	from?: string;
	to?: string;
}

export interface EntryListParams extends ListParams {
	tenantAccountId?: number;
	entryType?: string;
	direction?: string;
}

export interface ReverseTenantLedgerEntryRequest {
	reversesEntryId: number;
	effectiveOn: string;
	reason: string;
	sourceStoredFileId?: number | null;
}

export interface TenantLedgerMutationResult {
	found: boolean;
	applied: boolean;
	tenantAccountId: number;
	ledgerEntryId: number;
	reversesEntryId?: number | null;
	entryType: string;
	direction: string;
	amount: number;
	allocatedAmount: number;
	allocationCount: number;
	error?: string | null;
}

function queryString(params: object): string {
	const query = new URLSearchParams();
	for (const [key, value] of Object.entries(params)) {
		if (value !== undefined && value !== null && value !== '') query.set(key, String(value));
	}
	const text = query.toString();
	return text ? `?${text}` : '';
}

export function buildTenantAccountEntriesPagePath(
	tenantAccountId: number,
	params: EntryListParams = {}
): string {
	return `/tenant-accounts/${tenantAccountId}/entries/page${queryString(params)}`;
}

export function buildTenantAccountChargesPagePath(
	tenantAccountId: number,
	params: ListParams = {}
): string {
	return `/tenant-accounts/${tenantAccountId}/charges/page${queryString(params)}`;
}

export function buildTenantAccountDepositsPagePath(
	params: ListParams & { tenantAccountId?: number } = {}
): string {
	return `/tenant-accounts/deposits/page${queryString(params)}`;
}

export function buildTenantAccountReversalsPath(tenantAccountId: number): string {
	return `/tenant-accounts/${tenantAccountId}/reversals`;
}

export const tenantAccounts = {
	listPage: (params: ListParams = {}) =>
		api.get<Page<TenantAccountListItem>>(`/tenant-accounts/page${queryString(params)}`),
	get: (tenantAccountId: number) =>
		api.get<TenantAccountDetail>(`/tenant-accounts/${tenantAccountId}`),
	entriesPage: (params: EntryListParams = {}) =>
		api.get<EntryPage<TenantLedgerEntryGlobal>>(`/tenant-accounts/entries/page${queryString(params)}`),
	accountEntriesPage: (tenantAccountId: number, params: EntryListParams = {}) =>
		api.get<EntryPage<TenantLedgerEntry>>(buildTenantAccountEntriesPagePath(tenantAccountId, params)),
	chargesPage: (tenantAccountId: number, params: ListParams = {}) =>
		api.get<TenantChargePage>(buildTenantAccountChargesPagePath(tenantAccountId, params)),
	depositsPage: (params: ListParams & { tenantAccountId?: number } = {}) =>
		api.get<Page<TenantAccountDeposit>>(buildTenantAccountDepositsPagePath(params)),
	entry: (tenantAccountId: number, tenantLedgerEntryId: number) =>
		api.get<TenantLedgerEntryDetail>(`/tenant-accounts/${tenantAccountId}/entries/${tenantLedgerEntryId}`),
	reverseEntry: (
		tenantAccountId: number,
		operationKey: string,
		body: ReverseTenantLedgerEntryRequest
	) => api.post<TenantMoneyCommandResponse<TenantLedgerMutationResult>>(
		buildTenantAccountReversalsPath(tenantAccountId),
		body,
		{ headers: { 'Idempotency-Key': operationKey } }
	),
};
