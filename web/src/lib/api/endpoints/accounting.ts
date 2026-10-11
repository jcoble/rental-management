import type {
	AccountingReports,
	AccountingSummary,
	AccountingTransaction,
	MoneySnapshotResponse,
	OwnerStatementReport,
	OwnerStatementSummary,
	PastDueResponse,
	ScheduleEReport,
	YearEndView
} from '$lib/types';
import { api, downloadFile } from '../client';
import { browser } from '$app/environment';
import { buildListQuery, type ListParams } from '../list-params';

export type AccountingTransactionParams = ListParams & {
	kind?: string;
	status?: string;
	category?: string;
	propertyId?: number;
	from?: string;
	to?: string;
};

export interface OwnerStatementPeriodParams {
	period?: string;
	asOf?: string;
	year?: number;
}

function ownerStatementQuery(params: OwnerStatementPeriodParams | number = {}): string {
	const normalized = typeof params === 'number' ? { year: params } : params;
	return buildListQuery(undefined, {
		period: normalized.period,
		asOf: normalized.asOf,
		year: normalized.year
	});
}

/**
 * A high-confidence, still-unmatched bank line the user can one-tap confirm against a
 * Payment/Expense row. Powers the "Match?" chip on the accounting ledger. Nothing is auto-matched.
 */
export interface SuggestedBankMatch {
	bankTransactionId: number;
	name: string;
	amount: number;
	date: string;
	confidence: number;
}

/**
 * The bank-reconciliation fields the accounting transactions endpoint adds to Payment/Expense rows.
 * Declared here (rather than on the shared AccountingTransaction type) to keep the reconciliation
 * wave self-contained; merge `AccountingTransaction & ReconciledTransactionFields` at the call site.
 */
export interface ReconciledTransactionFields {
	/** True when a bank line has been confirmed (Matched) against this row — it has "cleared". */
	reconciled?: boolean;
	/** Bank/institution name of the matched line, when reconciled. */
	clearedBankName?: string | null;
	/** Posted date of the matched bank line, when reconciled. */
	clearedAt?: string | null;
	/** Suggested (unconfirmed) bank line to one-tap confirm; null when none or already reconciled. */
	suggestedBankMatch?: SuggestedBankMatch | null;
}

export type ReconciledAccountingTransaction = AccountingTransaction & ReconciledTransactionFields;

export interface ReconciledAccountingTransactionsResponse {
	items: ReconciledAccountingTransaction[];
	totalCount: number;
	skip: number;
	take: number;
}

export const accounting = {
	// GET /api/v1/accounting/summary — portfolio scope comes from the JWT claim.
	// Returns expense totals by Schedule E category + a payment collection rollup
	// (collected / outstanding / overdue).
	summary: () => api.get<AccountingSummary>('/accounting/summary'),
	// GET /api/v1/accounting/snapshot — plain-English money snapshot (collected / spent /
	// kept) with ready-to-show explanation sentences for the non-technical landlord.
	snapshot: () => api.get<MoneySnapshotResponse>('/accounting/snapshot'),
	// GET /api/v1/accounting/past-due — the server-paged "Who's behind" list: one row per tenant account.
	// Shares the snapshot's past-due definition server-side, so totalCount == snapshot.pastDueCount.
	// This is the single source both the dashboard KPI and the past-due list read from.
	pastDue: (params: Pick<ListParams, 'skip' | 'take'> = {}) =>
		api.get<PastDueResponse>(`/accounting/past-due${buildListQuery(params)}`),
	reports: () => api.get<AccountingReports>('/accounting/reports'),
	transactions: (params?: AccountingTransactionParams) => {
		const { kind, status, category, propertyId, from, to, ...list } = params ?? {};
		return api.get<ReconciledAccountingTransactionsResponse>(
			`/accounting/transactions${buildListQuery(list, { kind, status, category, propertyId, from, to })}`
		);
	},

	// Confirm a suggested bank match for a Payment/Expense ledger row in one tap. The bank line id
	// comes from the row's suggestedBankMatch; an empty body lets the server use the current
	// suggestion. After this the row flips to "Cleared" (invalidate 'accounting-transactions').
	confirmBankMatch: (bankTransactionId: number) =>
		api.post(`/banking/transactions/${bankTransactionId}/confirm-match`, {}),

	// GET /api/v1/accounting/schedule-e?year=YYYY
	scheduleE: (year: number) => api.get<ScheduleEReport>(`/accounting/schedule-e?year=${year}`),

	// GET /api/v1/accounting/year-end?year=YYYY — the three-block view (cash flow vs taxable income +
	// rent roll). Omitting the year defaults to the previous calendar year (the year you file for).
	yearEnd: (year?: number, propertyId?: number) =>
		api.get<YearEndView>(`/accounting/year-end${buildListQuery(undefined, { year, propertyId })}`),

	// GET /api/v1/accounting/owner-statements?period=&asOf=&year=
	// Omitting period/asOf defaults to the previous completed month; year remains a
	// compatibility escape hatch for the former annual report.
	ownerStatements: (params: OwnerStatementPeriodParams | number = {}) =>
		api.get<OwnerStatementSummary[]>(`/accounting/owner-statements${ownerStatementQuery(params)}`),

	// GET /api/v1/accounting/owner-statement?ownerId=&period=&asOf=&year=
	ownerStatement: (ownerId: number, params: OwnerStatementPeriodParams | number = {}) =>
		api.get<OwnerStatementReport>(`/accounting/owner-statement?ownerId=${ownerId}${ownerStatementQuery(params).replace('?', '&')}`),
};

/**
 * Download the Schedule E CSV for the given year with the bearer token attached.
 * A plain <a href> can't send the Authorization header, so we fetch the blob
 * manually and trigger a browser download via a temporary object URL.
 */
export async function downloadScheduleECsv(year: number): Promise<void> {
	if (!browser) return;

	const blob = await downloadFile(`/accounting/schedule-e/export?year=${year}`);
	const objectUrl = URL.createObjectURL(blob);
	const a = document.createElement('a');
	a.href = objectUrl;
	a.download = `schedule-e-${year}.csv`;
	document.body.appendChild(a);
	a.click();
	document.body.removeChild(a);
	URL.revokeObjectURL(objectUrl);
}

/**
 * Download the year-end packet PDF for the given year with the bearer token attached.
 * A plain <a href> can't send the Authorization header, so we fetch the blob
 * manually and trigger a browser download via a temporary object URL.
 */
export async function downloadYearEndPacket(year: number): Promise<void> {
	if (!browser) return;

	const blob = await downloadFile(`/accounting/year-end-packet?year=${year}`);
	const objectUrl = URL.createObjectURL(blob);
	const a = document.createElement('a');
	a.href = objectUrl;
	a.download = `year-end-${year}.pdf`;
	document.body.appendChild(a);
	a.click();
	document.body.removeChild(a);
	URL.revokeObjectURL(objectUrl);
}

/**
 * Download the owner-statement CSV for the given owner and year with the bearer token attached.
 * Mirrors downloadScheduleECsv exactly.
 */
export async function downloadOwnerStatementCsv(
	ownerId: number,
	params: OwnerStatementPeriodParams | number = {}
): Promise<void> {
	if (!browser) return;

	const normalized = typeof params === 'number' ? { year: params } : params;
	const query = buildListQuery(undefined, {
		ownerId,
		period: normalized.period,
		asOf: normalized.asOf,
		year: normalized.year
	});
	const periodLabel = normalized.period ?? normalized.year?.toString() ?? 'statement';
	const blob = await downloadFile(`/accounting/owner-statement/export${query}`);
	const objectUrl = URL.createObjectURL(blob);
	const a = document.createElement('a');
	a.href = objectUrl;
	a.download = `owner-statement-${ownerId}-${periodLabel}.csv`;
	document.body.appendChild(a);
	a.click();
	document.body.removeChild(a);
	URL.revokeObjectURL(objectUrl);
}
