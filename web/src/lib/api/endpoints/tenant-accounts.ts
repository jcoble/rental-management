import { api } from '../client';

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

function queryString(params: object): string {
	const query = new URLSearchParams();
	for (const [key, value] of Object.entries(params)) {
		if (value !== undefined && value !== null && value !== '') query.set(key, String(value));
	}
	const text = query.toString();
	return text ? `?${text}` : '';
}

export const tenantAccounts = {
	listPage: (params: ListParams = {}) =>
		api.get<Page<TenantAccountListItem>>(`/tenant-accounts/page${queryString(params)}`),
	get: (tenantAccountId: number) =>
		api.get<TenantAccountDetail>(`/tenant-accounts/${tenantAccountId}`),
	entriesPage: (params: EntryListParams = {}) =>
		api.get<EntryPage<TenantLedgerEntryGlobal>>(`/tenant-accounts/entries/page${queryString(params)}`),
	accountEntriesPage: (tenantAccountId: number, params: EntryListParams = {}) =>
		api.get<EntryPage<TenantLedgerEntry>>(`/tenant-accounts/${tenantAccountId}/entries/page${queryString(params)}`),
	entry: (tenantAccountId: number, tenantLedgerEntryId: number) =>
		api.get<TenantLedgerEntryDetail>(`/tenant-accounts/${tenantAccountId}/entries/${tenantLedgerEntryId}`),
};
