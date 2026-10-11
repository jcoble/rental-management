import { api } from '../client';
import { buildListQuery } from '../list-params.ts';

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
	businessDate: string;
	receivableBalance: number;
	unappliedCredit: number;
	pastDueAmount: number;
	pastDueCount: number;
	nextDueOn?: string | null;
	nextDueAmount: number;
	oldestOpenChargeDueOn?: string | null;
	oldestOpenChargeAmount?: number | null;
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
	filteredTotalCount: number;
	monthCharges: number;
	monthPaymentsAndCredits: number;
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
	propertyId?: number;
	unitId?: number;
	entryType?: string;
	direction?: string;
}

export function buildTenantAccountEntriesPagePath(
	tenantAccountId: number,
	params: EntryListParams = {}
): string {
	return `/tenant-accounts/${tenantAccountId}/entries/page${buildListQuery(
		undefined,
		params as Record<string, string | number | null | undefined>
	)}`;
}

export function buildTenantAccountChargesPagePath(
	tenantAccountId: number,
	params: ListParams = {}
): string {
	return `/tenant-accounts/${tenantAccountId}/charges/page${buildListQuery(
		undefined,
		params as Record<string, string | number | null | undefined>
	)}`;
}

export const tenantAccounts = {
	listPage: (params: ListParams = {}) =>
		api.get<Page<TenantAccountListItem>>(`/tenant-accounts/page${buildListQuery(
			undefined,
			params as Record<string, string | number | null | undefined>
		)}`),
	get: (tenantAccountId: number) =>
		api.get<TenantAccountDetail>(`/tenant-accounts/${tenantAccountId}`),
	entriesPage: (params: EntryListParams = {}) =>
		api.get<EntryPage<TenantLedgerEntryGlobal>>(`/tenant-accounts/entries/page${buildListQuery(
			undefined,
			params as Record<string, string | number | null | undefined>
		)}`),
	accountEntriesPage: (tenantAccountId: number, params: EntryListParams = {}) =>
		api.get<EntryPage<TenantLedgerEntry>>(buildTenantAccountEntriesPagePath(tenantAccountId, params)),
	chargesPage: (tenantAccountId: number, params: ListParams = {}) =>
		api.get<TenantChargePage>(buildTenantAccountChargesPagePath(tenantAccountId, params)),
	entry: (tenantAccountId: number, tenantLedgerEntryId: number) =>
		api.get<TenantLedgerEntryDetail>(`/tenant-accounts/${tenantAccountId}/entries/${tenantLedgerEntryId}`),
};
