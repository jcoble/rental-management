import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import type { AccountingPage } from './accounting-books';

export type TenantLedgerEntryType =
	| 'OpeningBalance'
	| 'RentCharge'
	| 'AddendumCharge'
	| 'LateFeeCharge'
	| 'DepositCharge'
	| 'ManualCharge'
	| 'PaymentReceipt'
	| 'Credit'
	| 'Adjustment'
	| 'Refund'
	| 'TransferIn'
	| 'TransferOut'
	| 'Reversal';
export type TenantLedgerDirection = 'Debit' | 'Credit';
export type TenantLedgerSort =
	| 'effectiveOn'
	| '-effectiveOn'
	| 'oldestDueOn'
	| 'postedAtUtc'
	| '-postedAtUtc';

export interface TenantLedgerActionCapabilities {
	canViewDetail: boolean;
	canGiveCredit: boolean;
	canAddRelatedCharge: boolean;
	canReverseCharge: boolean;
	canReverseLedgerEntry: boolean;
	canReviewPaymentAllocation: boolean;
}

export interface AllocationRef {
	allocationId: number;
	targetSourceId: number;
	targetPublicId: string;
	targetDescription: string;
	amount: number;
	effectiveOn: string;
}

export interface TenantLedgerRow {
	tenantLedgerEntryId: number;
	publicId: string;
	sourceType: string;
	sourceId: number;
	sourcePublicId: string | null;
	effectiveOn: string;
	postedAtUtc: string;
	type: TenantLedgerEntryType;
	direction: TenantLedgerDirection;
	ledgerKind: string;
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
	accountLabel: string | null;
	recurringScheduleContext: string | null;
	sourceDocumentContext: string | null;
	allocations: AllocationRef[];
	reversesEntryId: number | null;
	replacedByEntryId: number | null;
	journalEntryPublicId: string | null;
	currency: string;
	relatedTenantLedgerEntryId: number | null;
	relatedEntryDescription: string | null;
	categoryName: string | null;
	servicePeriodStartOn: string | null;
	servicePeriodEndOn: string | null;
	actionCapabilities: TenantLedgerActionCapabilities;
}

export interface TenantLedgerParams {
	skip?: number;
	take?: number;
	search?: string;
	sort?: TenantLedgerSort;
	entryType?: TenantLedgerEntryType;
	effectiveFrom?: string;
	effectiveTo?: string;
	openOnly?: boolean;
	settledOnly?: boolean;
}

export interface TenantMonthSummaryParams {
	from?: string;
	to?: string;
	entryType?: TenantLedgerEntryType;
	openOnly?: boolean;
	settledOnly?: boolean;
	take?: number;
}

export interface TenantCreditTarget {
	tenantLedgerEntryId: number;
	publicId: string;
	description: string;
	chargeAmount: number;
	remainingTargetableAmount: number;
	effectiveOn: string;
	currency: string;
}

export interface TenantCreditTargetParams extends Pick<ListParams, 'skip' | 'take' | 'sort'> {
	targetEntryId?: number;
}

export interface TenantMonthSummary {
	year: number;
	month: number;
	currency: string;
	openingBalance: number;
	chargeAmount: number;
	paymentAmount: number;
	creditAmount: number;
	closingBalance: number;
	needsReview: boolean;
	rows: TenantLedgerRow[];
}

export type TenantLedgerSummaryMonths = 3 | 6 | 9 | 12;

export interface TenantLedgerPeriodSummary {
	periodMonths: number;
	currency: string;
	chargeAmount: number;
	paymentAmount: number;
	creditAmount: number;
	endingBalance: number;
	agingCurrent: number;
	aging1To30: number;
	aging31To60: number;
	aging61To90: number;
	aging90Plus: number;
}

export interface RecurringTenantChargeRow {
	id: number;
	publicId: string;
	tenantAccountId: number;
	leaseAgreementId: number | null;
	displayName: string;
	amount: number;
	currency: string;
	ledgerAccountId: number;
	effectiveStartOn: string;
	effectiveEndOn: string | null;
	monthlyDueDay: number;
	nextRunDate: string;
	isActive: boolean;
	propertyId: number | null;
	unitId: number | null;
}

export interface CreateRecurringTenantChargeRequest {
	displayName: string;
	amount: number;
	ledgerAccountId: number;
	leaseAgreementId?: number | null;
	effectiveStartOn: string;
	effectiveEndOn?: string | null;
	monthlyDueDay: number;
	nextRunDate?: string | null;
	propertyId?: number | null;
	unitId?: number | null;
}

export interface PatchRecurringTenantChargeRequest {
	displayName?: string | null;
	amount?: number | null;
	ledgerAccountId?: number | null;
	effectiveStartOn?: string | null;
	effectiveEndOn?: string | null;
	monthlyDueDay?: number | null;
	nextRunDate?: string | null;
	isActive?: boolean | null;
	propertyId?: number | null;
	unitId?: number | null;
}

function mutationOptions(operationKey: string): RequestInit {
	return { headers: { 'Idempotency-Key': operationKey } };
}

export function buildTenantLedgerPath(
	tenantAccountId: number,
	params: TenantLedgerParams = {}
): string {
	return `/tenant-accounts/${tenantAccountId}/ledger${buildListQuery(
		{
			skip: params.skip,
			take: params.take,
			search: params.search,
			sort: params.sort
		},
		{
			entryType: params.entryType,
			effectiveFrom: params.effectiveFrom,
			effectiveTo: params.effectiveTo,
			openOnly: params.openOnly == null ? undefined : String(params.openOnly),
			settledOnly: params.settledOnly == null ? undefined : String(params.settledOnly)
		}
	)}`;
}

export function buildTenantMonthSummaryPath(
	tenantAccountId: number,
	params: TenantMonthSummaryParams = {}
): string {
	return `/tenant-accounts/${tenantAccountId}/month-summary${buildListQuery(undefined, {
		from: params.from,
		to: params.to,
		entryType: params.entryType,
		openOnly: params.openOnly == null ? undefined : String(params.openOnly),
		settledOnly: params.settledOnly == null ? undefined : String(params.settledOnly),
		take: params.take
	})}`;
}

export function buildTenantLedgerSummaryPath(
	tenantAccountId: number,
	months: TenantLedgerSummaryMonths = 12
): string {
	return `/tenant-accounts/${tenantAccountId}/ledger-summary${buildListQuery(undefined, { months })}`;
}

export function buildTenantCreditTargetsPath(
	tenantAccountId: number,
	params: TenantCreditTargetParams = {}
): string {
	return `/tenant-accounts/${tenantAccountId}/credit-targets${buildListQuery(
		{
			skip: params.skip,
			take: params.take,
			sort: params.sort
		},
		{ targetEntryId: params.targetEntryId }
	)}`;
}

export function buildRecurringTenantChargesPath(
	tenantAccountId: number,
	params: Pick<ListParams, 'skip' | 'take' | 'search' | 'sort'> = {}
): string {
	return `/tenant-accounts/${tenantAccountId}/recurring-charges${buildListQuery(params)}`;
}

export const tenantLedgers = {
	list: (tenantAccountId: number, params?: TenantLedgerParams) =>
		api.get<AccountingPage<TenantLedgerRow>>(buildTenantLedgerPath(tenantAccountId, params)),
	monthSummary: (tenantAccountId: number, params?: TenantMonthSummaryParams) =>
		api.get<TenantMonthSummary[]>(buildTenantMonthSummaryPath(tenantAccountId, params)),
	ledgerSummary: (tenantAccountId: number, months: TenantLedgerSummaryMonths = 12) =>
		api.get<TenantLedgerPeriodSummary>(buildTenantLedgerSummaryPath(tenantAccountId, months)),
	creditTargets: (tenantAccountId: number, params?: TenantCreditTargetParams) =>
		api.get<AccountingPage<TenantCreditTarget>>(
			buildTenantCreditTargetsPath(tenantAccountId, params)
		),
	recurringCharges: (tenantAccountId: number, params?: Pick<ListParams, 'skip' | 'take' | 'search' | 'sort'>) =>
		api.get<AccountingPage<RecurringTenantChargeRow>>(
			buildRecurringTenantChargesPath(tenantAccountId, params)
		),
	createRecurringCharge: (
		tenantAccountId: number,
		operationKey: string,
		body: CreateRecurringTenantChargeRequest
	) =>
		api.post<RecurringTenantChargeRow>(
			`/tenant-accounts/${tenantAccountId}/recurring-charges`,
			body,
			mutationOptions(operationKey)
		),
	patchRecurringCharge: (
		tenantAccountId: number,
		id: number,
		operationKey: string,
		body: PatchRecurringTenantChargeRequest
	) =>
		api.patch<RecurringTenantChargeRow>(
			`/tenant-accounts/${tenantAccountId}/recurring-charges/${id}`,
			body,
			mutationOptions(operationKey)
		),
	deactivateRecurringCharge: (tenantAccountId: number, id: number, operationKey: string) =>
		api.post<RecurringTenantChargeRow>(
			`/tenant-accounts/${tenantAccountId}/recurring-charges/${id}/deactivate`,
			undefined,
			mutationOptions(operationKey)
		)
};
