import type {
	AccountingReports,
	AccountingSummary,
	AccountingTransaction,
	CashFlowSummary,
	MoneySnapshotResponse,
	OwnerStatementReport,
	OwnerStatementSummary,
	PastDueResponse,
	ScheduleEReport,
	YearEndView
} from '$lib/types';
import { api, refreshToken } from '../client';
import { CLIENT_API_BASE_URL } from '$lib/config';
import { getAuthState, isTokenExpired } from '$lib/stores/auth.svelte';
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

export interface AccountingPage<T> {
	items: T[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface ChartOfAccountsRow {
	id: number;
	publicId: string;
	code: string;
	name: string;
	accountType: string;
	normalBalance: string;
	parentAccountId: number | null;
	systemKey: string | null;
	scheduleECategory: string | null;
	isSystem: boolean;
	isActive: boolean;
	hasPostedLines: boolean;
}

export interface CreateChartOfAccountsRequest {
	code: string;
	name: string;
	accountType: string;
	normalBalance: string;
	parentAccountId?: number | null;
	systemKey?: string | null;
	scheduleECategory?: string | null;
	isActive?: boolean;
}

export interface PatchChartOfAccountsRequest {
	name?: string;
	isActive?: boolean;
	scheduleECategory?: string | null;
	parentAccountId?: number | null;
}

export interface GeneralLedgerRow {
	journalEntryPublicId: string;
	lineId: number;
	effectiveOn: string;
	postedAtUtc: string;
	sourceType: string;
	sourceId: number;
	sourceBusinessKey: string;
	description: string;
	accountId: number;
	accountCode: string;
	accountName: string;
	debitAmount: number;
	creditAmount: number;
	currency: string;
	propertyId: number | null;
	unitId: number | null;
	tenantAccountId: number | null;
	ownerEntityId: number | null;
	runningBalance: number | null;
}

export interface JournalDetailLine {
	id: number;
	accountId: number;
	accountCode: string;
	accountName: string;
	debitAmount: number;
	creditAmount: number;
	memo: string | null;
	propertyId: number | null;
	unitId: number | null;
	tenantAccountId: number | null;
	ownerEntityId: number | null;
}

export interface BankReconciliationEvidence {
	bankTransactionId: number | null;
	bankAccountLabel: string | null;
	matchedOn: string | null;
	status: string | null;
}

export interface JournalDetail {
	publicId: string;
	description: string;
	effectiveOn: string;
	postedAtUtc: string;
	sourceType: string;
	sourceId: number;
	sourceBusinessKey: string;
	actor: string | null;
	attemptId: string;
	atomicReceiptId: string;
	idempotencyDigest: string;
	currency: string;
	lines: JournalDetailLine[];
	totalDebits: number;
	totalCredits: number;
	isBalanced: boolean;
	reversesJournalEntryPublicId: string | null;
	reversalPublicIds: string[];
	auditLink: string | null;
	documentIds: number[];
	bankReconciliationEvidence: BankReconciliationEvidence | null;
}

export interface TrialBalanceRow {
	accountId: number;
	accountCode: string;
	accountName: string;
	accountType: string;
	debitBalance: number;
	creditBalance: number;
	currency: string;
}

export interface TrialBalanceResponse {
	rows: TrialBalanceRow[];
	totalDebits: number;
	totalCredits: number;
	isBalanced: boolean;
}

export interface FinancialStatementRow {
	accountId: number;
	accountCode: string;
	accountName: string;
	amount: number;
	currency: string;
}

export interface FinancialStatementSection {
	label: string;
	rows: FinancialStatementRow[];
	subtotal: number;
}

export interface FinancialStatementTotals {
	total: number;
	netIncome: number | null;
	assets: number | null;
	liabilitiesAndEquity: number | null;
}

export interface FinancialStatementResponse {
	sections: FinancialStatementSection[];
	totals: FinancialStatementTotals;
}

export interface OwnerContributionResponse {
	id: number;
	portfolioId: number;
	ownerEntityId: number;
	ownerName: string;
	propertyId: number | null;
	propertyName: string | null;
	date: string;
	amount: number;
	method: string;
	status: string;
	approvedAt: string | null;
	approvedBusinessDate: string | null;
	approvedByUserId: number | null;
	rejectedAt: string | null;
	rejectedByUserId: number | null;
	rejectionReason: string | null;
	bankReference: string | null;
	exportReference: string | null;
	exportedAt: string | null;
	memo: string | null;
	createdAt: string;
	updatedAt: string;
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
	chartOfAccounts: (params: Pick<ListParams, 'skip' | 'take' | 'search' | 'sort'> & { activeOnly?: boolean } = {}) =>
		api.get<AccountingPage<ChartOfAccountsRow>>(`/accounting/chart-of-accounts${buildListQuery(params)}`),
	createChartOfAccounts: (body: CreateChartOfAccountsRequest) =>
		api.post<ChartOfAccountsRow>('/accounting/chart-of-accounts', body),
	patchChartOfAccounts: (id: number, body: PatchChartOfAccountsRequest) =>
		api.patch<ChartOfAccountsRow>(`/accounting/chart-of-accounts/${id}`, body),
	generalLedger: (params: ListParams & {
		accountId?: number;
		propertyId?: number;
		unitId?: number;
		sourceType?: string;
		effectiveFrom?: string;
		effectiveTo?: string;
	} = {}) => api.get<AccountingPage<GeneralLedgerRow>>(`/accounting/general-ledger${buildListQuery(params)}`),
	journalEntry: (publicId: string) =>
		api.get<JournalDetail>(`/accounting/journal-entries/${publicId}`),
	trialBalance: () => api.get<TrialBalanceResponse>('/accounting/trial-balance'),
	balanceSheet: () => api.get<FinancialStatementResponse>('/accounting/balance-sheet'),
	incomeStatement: () => api.get<FinancialStatementResponse>('/accounting/income-statement'),
	ownerContributions: (params: ListParams = {}) =>
		api.get<OwnerContributionResponse[]>(`/owner-contributions${buildListQuery(params)}`),
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

	// GET /api/v1/accounting/cash-flow?from=&to= — true cash flow (rent − opex − debt service),
	// escrow-aware, per property + portfolio. Omitting the range defaults to the current year-to-date.
	cashFlow: (params?: { from?: string; to?: string }) =>
		api.get<CashFlowSummary>(`/accounting/cash-flow${buildListQuery(undefined, { from: params?.from, to: params?.to })}`),

	// GET /api/v1/accounting/year-end?year=YYYY — the three-block view (cash flow vs taxable income +
	// rent roll). Omitting the year defaults to the previous calendar year (the year you file for).
	yearEnd: (year?: number, propertyId?: number) =>
		api.get<YearEndView>(`/accounting/year-end${buildListQuery(undefined, { year, propertyId })}`),

	// GET /api/v1/accounting/owner-statements?year=YYYY
	ownerStatements: (year: number) =>
		api.get<OwnerStatementSummary[]>(`/accounting/owner-statements?year=${year}`),

	// GET /api/v1/accounting/owner-statement?ownerId=&year=YYYY
	ownerStatement: (ownerId: number, year: number) =>
		api.get<OwnerStatementReport>(`/accounting/owner-statement?ownerId=${ownerId}&year=${year}`),
};

/**
 * Download the Schedule E CSV for the given year with the bearer token attached.
 * A plain <a href> can't send the Authorization header, so we fetch the blob
 * manually and trigger a browser download via a temporary object URL.
 */
export async function downloadScheduleECsv(year: number): Promise<void> {
	if (!browser) return;

	// Proactively refresh if near expiry, mirroring the main client logic.
	if (isTokenExpired(120)) {
		try {
			await refreshToken();
		} catch {
			// Proceed; bearer may still be usable.
		}
	}

	const { accessToken } = getAuthState();
	const url = `${CLIENT_API_BASE_URL}/accounting/schedule-e/export?year=${year}`;

	const response = await fetch(url, {
		credentials: 'include',
		headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {}
	});

	if (!response.ok) {
		throw new Error(`CSV download failed (${response.status})`);
	}

	const blob = await response.blob();
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

	// Proactively refresh if near expiry, mirroring the main client logic.
	if (isTokenExpired(120)) {
		try {
			await refreshToken();
		} catch {
			// Proceed; bearer may still be usable.
		}
	}

	const { accessToken } = getAuthState();
	const url = `${CLIENT_API_BASE_URL}/accounting/year-end-packet?year=${year}`;

	const response = await fetch(url, {
		credentials: 'include',
		headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {}
	});

	if (!response.ok) {
		throw new Error(`Packet download failed (${response.status})`);
	}

	const blob = await response.blob();
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
export async function downloadOwnerStatementCsv(ownerId: number, year: number): Promise<void> {
	if (!browser) return;

	if (isTokenExpired(120)) {
		try {
			await refreshToken();
		} catch {
			// Proceed; bearer may still be usable.
		}
	}

	const { accessToken } = getAuthState();
	const url = `${CLIENT_API_BASE_URL}/accounting/owner-statement/export?ownerId=${ownerId}&year=${year}`;

	const response = await fetch(url, {
		credentials: 'include',
		headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {}
	});

	if (!response.ok) {
		throw new Error(`CSV download failed (${response.status})`);
	}

	const blob = await response.blob();
	const objectUrl = URL.createObjectURL(blob);
	const a = document.createElement('a');
	a.href = objectUrl;
	a.download = `owner-statement-${ownerId}-${year}.csv`;
	document.body.appendChild(a);
	a.click();
	document.body.removeChild(a);
	URL.revokeObjectURL(objectUrl);
}
