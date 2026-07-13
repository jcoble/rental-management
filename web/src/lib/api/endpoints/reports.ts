/**
 * Reports Hub API client. The backend (RentalCommand.Api ReportsController) is a thin,
 * read-only reporting layer over existing portfolio data: every report is a pure GET that
 * projects to one of the DTOs in `RentalCommand.Api/DTOs/ReportsDtos.cs`. Portfolio scope is
 * implicit (the JWT `portfolioId` claim), so it never travels as a request parameter.
 *
 * The catalog (`GET /reports/catalog`) drives the UI: it lists every report grouped by category,
 * with the endpoint to call, which params it accepts, and whether it's `external` (served by an
 * existing controller — Schedule E / Owner Statement / Year-End Packet — and only deep-linked,
 * never fetched as data here).
 *
 * Endpoint paths in the catalog are absolute under the API root (e.g. `/api/v1/reports/cash-flow`);
 * our `fetchApi` client takes paths relative to `/api/v1`, so {@link reportEndpointPath} strips the
 * `/api/v1` prefix before dispatching.
 */

import { fetchApi } from '../client';

// ── Catalog ─────────────────────────────────────────────────────────────────────────────────────

/** Well-known param keys a catalog entry can declare (mirrors `ReportParamKeys`). */
export type ReportParamKey =
	| 'from'
	| 'to'
	| 'propertyId'
	| 'propertyIds'
	| 'year'
	| 'ownerId'
	| 'days';

export interface ReportCatalogEntry {
	/** Stable report key, e.g. "rent-roll" — also the URL segment in /reports/[report]. */
	key: string;
	title: string;
	description: string;
	/** API path to call, absolute under the API root (e.g. "/api/v1/reports/cash-flow"). */
	endpoint: string;
	/** Which params the report accepts, so the param bar renders generically. */
	params: ReportParamKey[];
	/** True when served by a pre-existing endpoint (deep-link, not fetched as data). */
	external: boolean;
}

export interface ReportCategoryGroup {
	key: string;
	title: string;
	reports: ReportCatalogEntry[];
}

export interface ReportsCatalogResponse {
	categories: ReportCategoryGroup[];
}

// ── Shared request params ─────────────────────────────────────────────────────────────────────────

export interface ReportRequestParams {
	/** ISO yyyy-MM-dd (inclusive). Defaults to year-to-date server-side when omitted. */
	from?: string;
	to?: string;
	/** Single-property filter (general-ledger). */
	propertyId?: number;
	/** Multi-property filter — repeated `propertyIds` query params. */
	propertyIds?: number[];
	/** Tax/calendar year (vendor-1099, owner-distributions). */
	year?: number;
	/** Forward-looking window in days (lease-expirations). */
	days?: number;
}

// ── Report response DTOs (match ReportsDtos.cs exactly) ─────────────────────────────────────────────

export type LeaseStatusName = string;

export interface RentRollRow {
	leaseManagementId: number;
	tenantAccountId: number;
	agreementId: number;
	relationshipNumber: string;
	agreementNumber: string;
	propertyId: number;
	propertyName: string;
	unitId: number;
	unitNumber: string;
	tenantId: number;
	tenantName: string;
	monthlyRent: number;
	securityDeposit: number;
	startOn: string;
	endOn: string | null;
	statusName: string;
}

export interface RentRollResponse {
	generatedAt: string;
	rows: RentRollRow[];
	leaseCount: number;
	totalMonthlyRent: number;
	totalSecurityDeposit: number;
}

export interface RentLedgerEntry {
	date: string;
	type: string; // "Charge" | "Receipt" | "Credit"
	description: string;
	charge: number;
	credit: number;
	balance: number;
}

export interface RentLedgerLease {
	leaseManagementId: number;
	relationshipNumber: string;
	propertyId: number;
	propertyName: string;
	unitNumber: string;
	tenantName: string;
	entries: RentLedgerEntry[];
	totalCharged: number;
	totalCredits: number;
	balance: number;
}

export interface RentLedgerResponse {
	from: string;
	to: string;
	leases: RentLedgerLease[];
	totalCharged: number;
	totalCredits: number;
	totalBalance: number;
}

export interface DelinquencyBuckets {
	current: number;
	days31To60: number;
	days61To90: number;
	over90: number;
}

export interface DelinquencyRow {
	leaseManagementId: number;
	relationshipNumber: string;
	propertyId: number;
	propertyName: string;
	unitNumber: string;
	tenantId: number;
	tenantName: string;
	buckets: DelinquencyBuckets;
	total: number;
	oldestOverdueDays: number;
}

export interface DelinquencyResponse {
	asOf: string;
	rows: DelinquencyRow[];
	totals: DelinquencyBuckets;
	totalOutstanding: number;
}

export interface CashFlowMonth {
	year: number;
	month: number;
	monthKey: string;
	label: string;
	income: number;
	expense: number;
	net: number;
}

export interface CashFlowResponse {
	from: string;
	to: string;
	months: CashFlowMonth[];
	totalIncome: number;
	totalExpense: number;
	totalNet: number;
}

export interface GeneralLedgerEntry {
	date: string;
	type: string; // "Payment" | "Expense"
	id: number;
	description: string;
	category: string;
	propertyId?: number | null;
	propertyName?: string | null;
	counterparty?: string | null;
	/** Signed: positive income, negative expense. */
	amount: number;
	runningBalance: number;
}

export interface GeneralLedgerResponse {
	from: string;
	to: string;
	entries: GeneralLedgerEntry[];
	totalIncome: number;
	totalExpense: number;
	closingBalance: number;
}

export interface PropertyProfitAndLossRow {
	propertyId: number;
	propertyName: string;
	income: number;
	expense: number;
	net: number;
}

export interface PropertyProfitAndLossResponse {
	from: string;
	to: string;
	rows: PropertyProfitAndLossRow[];
	totalIncome: number;
	totalExpense: number;
	totalNet: number;
}

export interface OccupancyRow {
	propertyId: number;
	propertyName: string;
	totalUnits: number;
	occupiedUnits: number;
	vacantUnits: number;
	occupancyPercent: number;
}

export interface OccupancyResponse {
	generatedAt: string;
	rows: OccupancyRow[];
	totalUnits: number;
	occupiedUnits: number;
	vacantUnits: number;
	occupancyPercent: number;
}

export interface LeaseExpirationRow {
	leaseManagementId: number;
	agreementId: number;
	relationshipNumber: string;
	agreementNumber: string;
	propertyId: number;
	propertyName: string;
	unitNumber: string;
	tenantId: number | null;
	tenantName: string;
	monthlyRent: number;
	endOn: string;
	daysUntilExpiry: number;
	statusName: string;
}

export interface LeaseExpirationsResponse {
	asOf: string;
	windowDays: number;
	rows: LeaseExpirationRow[];
	leaseCount: number;
	totalMonthlyRent: number;
}

export interface SecurityDepositRegisterRow {
	depositId: number;
	leaseManagementId: number;
	relationshipNumber: string;
	propertyId: number;
	propertyName: string;
	unitNumber: string;
	tenantName: string;
	held: number;
	deductions: number;
	returned: number;
	currentBalance: number;
	status: string;
	statusName: string;
	heldAt: string;
	returnedAt?: string | null;
}

export interface SecurityDepositRegisterResponse {
	generatedAt: string;
	rows: SecurityDepositRegisterRow[];
	totalHeld: number;
	totalDeductions: number;
	totalReturned: number;
	totalCurrentBalance: number;
}

export interface Vendor1099Row {
	vendorId: number;
	vendorName: string;
	taxId?: string | null;
	totalPaid: number;
	is1099Eligible: boolean;
	w9OnFile: boolean;
	needsW9: boolean;
	needs1099Review: boolean;
}

export interface Vendor1099Response {
	year: number;
	rows: Vendor1099Row[];
	totalPaid: number;
	threshold: number;
}

export interface OwnerDistributionRow {
	ownerId: number;
	ownerName: string;
	netToOwner: number;
	totalDistributed: number;
	undistributed: number;
}

export interface OwnerDistributionsResponse {
	year: number;
	rows: OwnerDistributionRow[];
	totalNetToOwners: number;
	totalDistributed: number;
	totalUndistributed: number;
}

export interface WorkOrderReportRow {
	workOrderId: number;
	propertyId: number;
	propertyName: string;
	unitNumber?: string | null;
	title: string;
	category: string;
	priority: string;
	priorityName: string;
	status: string;
	statusName: string;
	vendorName?: string | null;
	requestedAt: string;
	completedAt?: string | null;
	actualCost?: number | null;
}

export interface WorkOrderReportResponse {
	from: string;
	to: string;
	rows: WorkOrderReportRow[];
	totalCount: number;
	openCount: number;
	completedCount: number;
	totalActualCost: number;
}

/** Any non-external report response shape (discriminated at the call site by report key). */
export type ReportData =
	| RentRollResponse
	| RentLedgerResponse
	| DelinquencyResponse
	| CashFlowResponse
	| GeneralLedgerResponse
	| PropertyProfitAndLossResponse
	| OccupancyResponse
	| LeaseExpirationsResponse
	| SecurityDepositRegisterResponse
	| Vendor1099Response
	| OwnerDistributionsResponse
	| WorkOrderReportResponse;

// ── Query-string + endpoint helpers ───────────────────────────────────────────────────────────────

/**
 * Strip the leading `/api/v1` (the {@link fetchApi} base) from a catalog endpoint so we can pass
 * the remainder to the client. Tolerates entries already given relative.
 */
export function reportEndpointPath(endpoint: string): string {
	return endpoint.replace(/^\/api\/v1/, '');
}

/**
 * Serialize report params into a query string, declaring only the keys the entry accepts so we
 * never send irrelevant params. `propertyIds` is repeated once per id. Empty values are omitted so
 * the backend applies its defaults (YTD range, current year, 90-day window).
 */
export function buildReportQuery(params: ReportRequestParams, accepts: ReportParamKey[]): string {
	const set = new Set(accepts);
	const qs = new URLSearchParams();

	if (set.has('from') && params.from) qs.set('from', params.from);
	if (set.has('to') && params.to) qs.set('to', params.to);
	if (set.has('propertyId') && params.propertyId != null) qs.set('propertyId', String(params.propertyId));
	if (set.has('propertyIds') && params.propertyIds?.length) {
		for (const id of params.propertyIds) qs.append('propertyIds', String(id));
	}
	if (set.has('year') && params.year != null) qs.set('year', String(params.year));
	if (set.has('days') && params.days != null) qs.set('days', String(params.days));

	const s = qs.toString();
	return s ? `?${s}` : '';
}

// ── Client ──────────────────────────────────────────────────────────────────────────────────────

export const reports = {
	/** GET /reports/catalog — the grouped report catalog that drives the hub UI. */
	catalog: () => fetchApi<ReportsCatalogResponse>('/reports/catalog'),

	/**
	 * Generic report fetch. Resolves the endpoint from the catalog entry, sends only the params the
	 * entry declares, and returns the matching DTO. The caller narrows the result by report key.
	 * Throws if the entry is `external` (those are deep-linked, not fetched).
	 */
	run: <T extends ReportData>(entry: ReportCatalogEntry, params: ReportRequestParams) => {
		if (entry.external) {
			throw new Error(`Report "${entry.key}" is external and cannot be fetched as data.`);
		}
		const path = reportEndpointPath(entry.endpoint) + buildReportQuery(params, entry.params);
		return fetchApi<T>(path);
	},

	// Typed convenience accessors (paths relative to /api/v1).
	rentRoll: (params: ReportRequestParams = {}) =>
		fetchApi<RentRollResponse>(`/reports/rent-roll${buildReportQuery(params, ['propertyIds'])}`),
	rentLedger: (params: ReportRequestParams = {}) =>
		fetchApi<RentLedgerResponse>(`/reports/rent-ledger${buildReportQuery(params, ['from', 'to', 'propertyIds'])}`),
	delinquency: (params: ReportRequestParams = {}) =>
		fetchApi<DelinquencyResponse>(`/reports/delinquency${buildReportQuery(params, ['propertyIds'])}`),
	cashFlow: (params: ReportRequestParams = {}) =>
		fetchApi<CashFlowResponse>(`/reports/cash-flow${buildReportQuery(params, ['from', 'to', 'propertyIds'])}`),
	generalLedger: (params: ReportRequestParams = {}) =>
		fetchApi<GeneralLedgerResponse>(`/reports/general-ledger${buildReportQuery(params, ['from', 'to', 'propertyId'])}`),
	propertyPnl: (params: ReportRequestParams = {}) =>
		fetchApi<PropertyProfitAndLossResponse>(`/reports/property-pnl${buildReportQuery(params, ['from', 'to', 'propertyIds'])}`),
	occupancy: (params: ReportRequestParams = {}) =>
		fetchApi<OccupancyResponse>(`/reports/occupancy${buildReportQuery(params, ['propertyIds'])}`),
	leaseExpirations: (params: ReportRequestParams = {}) =>
		fetchApi<LeaseExpirationsResponse>(`/reports/lease-expirations${buildReportQuery(params, ['days', 'propertyIds'])}`),
	securityDeposits: (params: ReportRequestParams = {}) =>
		fetchApi<SecurityDepositRegisterResponse>(`/reports/security-deposits${buildReportQuery(params, ['propertyIds'])}`),
	vendor1099: (params: ReportRequestParams = {}) =>
		fetchApi<Vendor1099Response>(`/reports/vendor-1099${buildReportQuery(params, ['year'])}`),
	ownerDistributions: (params: ReportRequestParams = {}) =>
		fetchApi<OwnerDistributionsResponse>(`/reports/owner-distributions${buildReportQuery(params, ['year'])}`),
	workOrders: (params: ReportRequestParams = {}) =>
		fetchApi<WorkOrderReportResponse>(`/reports/work-orders${buildReportQuery(params, ['from', 'to', 'propertyIds'])}`),
};
