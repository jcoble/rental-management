import { api } from '../client';
import { idempotentMutation } from '../idempotency';
import { buildListQuery, type ListParams } from '../list-params';

export type AccountType = 'Asset' | 'Liability' | 'Equity' | 'Income' | 'Expense';
export type NormalBalance = 'Debit' | 'Credit';
export type JournalSourceType =
	| 'TenantCharge'
	| 'TenantReceipt'
	| 'ProviderSettlement'
	| 'TenantConcession'
	| 'ReceivableWriteOff'
	| 'SecurityDepositReceipt'
	| 'SecurityDepositRefund'
	| 'SecurityDepositApplication'
	| 'ExpensePayment'
	| 'BillIncurred'
	| 'BillPayment'
	| 'BankTransfer'
	| 'LoanPayment'
	| 'CapitalPurchase'
	| 'Depreciation'
	| 'OwnerContribution'
	| 'OwnerDistribution'
	| 'OpeningBalance';
export type ScheduleECategory =
	| 'Advertising'
	| 'AutoTravel'
	| 'CleaningMaintenance'
	| 'Commissions'
	| 'Insurance'
	| 'LegalProfessional'
	| 'ManagementFees'
	| 'MortgageInterest'
	| 'Repairs'
	| 'Supplies'
	| 'Taxes'
	| 'Utilities'
	| 'Depreciation'
	| 'Other';

export interface AccountingPage<T> {
	items: T[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface ChartOfAccountsParams extends ListParams {
	activeOnly?: boolean;
}

export interface ChartOfAccountsRow {
	id: number;
	publicId: string;
	code: string;
	name: string;
	accountType: AccountType;
	normalBalance: NormalBalance;
	parentAccountId: number | null;
	systemKey: string | null;
	scheduleECategory: ScheduleECategory | null;
	isSystem: boolean;
	isActive: boolean;
	hasPostedLines: boolean;
}

export interface CreateChartOfAccountsRequest {
	name: string;
	categoryKind: 'income' | 'expense';
	parentAccountId?: number | null;
	scheduleECategory?: ScheduleECategory | null;
	isActive?: boolean;
}

export interface PatchChartOfAccountsRequest {
	name?: string | null;
	isActive?: boolean | null;
	scheduleECategory?: ScheduleECategory | null;
	parentAccountId?: number | null;
}

export type GeneralLedgerSort =
	| 'effectiveOn'
	| '-effectiveOn'
	| 'postedAtUtc'
	| '-postedAtUtc'
	| 'accountCode'
	| '-accountCode';

export interface GeneralLedgerParams {
	skip?: number;
	take?: number;
	search?: string;
	sort?: GeneralLedgerSort;
	accountId?: number;
	propertyId?: number;
	unitId?: number;
	sourceType?: JournalSourceType;
	effectiveFrom?: string;
	effectiveTo?: string;
}

export interface GeneralLedgerRow {
	journalEntryPublicId: string;
	lineId: number;
	effectiveOn: string;
	postedAtUtc: string;
	sourceType: JournalSourceType;
	sourceId: number;
	sourceBusinessKey: string;
	description: string;
	accountId: number;
	accountCode: string;
	accountName: string;
	accountType: AccountType;
	normalBalance: NormalBalance;
	debitAmount: number;
	creditAmount: number;
	currency: string;
	propertyId: number | null;
	unitId: number | null;
	tenantAccountId: number | null;
	ownerEntityId: number | null;
	runningBalance: number | null;
}

export interface JournalDetail {
	publicId: string;
	description: string;
	effectiveOn: string;
	postedAtUtc: string;
	sourceType: JournalSourceType;
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

export interface JournalDetailLine {
	id: number;
	accountId: number;
	accountCode: string;
	accountName: string;
	normalBalance: NormalBalance;
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

export interface SourceJournalParams {
	sourceType: JournalSourceType;
	sourceId: number;
}

export interface SourceJournalSummary {
	publicId: string;
	effectiveOn: string;
	postedAtUtc: string;
	sourceType: JournalSourceType;
	description: string;
	totalDebits: number;
	totalCredits: number;
	isReversal: boolean;
	reversesPublicId: string | null;
	lines: JournalDetailLine[];
}

export interface TrialBalanceParams {
	to?: string;
}

export interface TrialBalanceRow {
	accountId: number;
	accountCode: string;
	accountName: string;
	accountType: AccountType;
	debitBalance: number;
	creditBalance: number;
	typeSubtotal: number;
	currency: string;
	isZeroBalance: boolean;
}

export interface TrialBalanceResponse {
	rows: TrialBalanceRow[];
	totalDebits: number;
	totalCredits: number;
	isBalanced: boolean;
	zeroBalanceCount: number;
}

export interface StatementParams {
	to?: string;
	currency?: string;
	propertyId?: number;
	unitId?: number;
}

export interface IncomeStatementParams extends StatementParams {
	from?: string;
}

export interface FinancialStatementRow {
	accountId: number;
	accountCode: string;
	accountName: string;
	amount: number;
	currency: string;
}

export interface StatementSection {
	label: string;
	rows: FinancialStatementRow[];
	subtotal: number;
}

export interface StatementTotals {
	total: number;
	netIncome: number | null;
	assets: number | null;
	liabilitiesAndEquity: number | null;
	currentEarnings: number | null;
	isBalanced: boolean | null;
}

export interface FinancialStatementResponse {
	sections: StatementSection[];
	totals: StatementTotals;
}

export function buildChartOfAccountsPath(params: ChartOfAccountsParams = {}): string {
	return `/accounting/chart-of-accounts${buildListQuery(params, {
		activeOnly: params.activeOnly == null ? undefined : String(params.activeOnly)
	})}`;
}

export function buildGeneralLedgerPath(params: GeneralLedgerParams = {}): string {
	const { accountId, propertyId, unitId, sourceType, effectiveFrom, effectiveTo, ...list } = params;
	return `/accounting/general-ledger${buildListQuery(list, {
		accountId,
		propertyId,
		unitId,
		sourceType,
		effectiveFrom,
		effectiveTo
	})}`;
}

export function buildSourceJournalsPath(params: SourceJournalParams): string {
	const query = new URLSearchParams({
		sourceType: params.sourceType,
		sourceId: String(params.sourceId)
	});
	return `/accounting/source-journals?${query.toString()}`;
}

function buildStatementPath(path: string, params: StatementParams = {}, includeFrom = false): string {
	return `${path}${buildListQuery(undefined, {
		from: includeFrom ? (params as IncomeStatementParams).from : undefined,
		to: params.to,
		currency: params.currency,
		propertyId: params.propertyId,
		unitId: params.unitId
	})}`;
}

export function buildTrialBalancePath(params: TrialBalanceParams = {}): string {
	return `/accounting/trial-balance${buildListQuery(undefined, { to: params.to })}`;
}

export function buildBalanceSheetPath(params: StatementParams = {}): string {
	return buildStatementPath('/accounting/balance-sheet', params);
}

export function buildIncomeStatementPath(params: IncomeStatementParams = {}): string {
	return buildStatementPath('/accounting/income-statement', params, true);
}

export const accountingBooks = {
	chartOfAccounts: (params?: ChartOfAccountsParams) =>
		api.get<AccountingPage<ChartOfAccountsRow>>(buildChartOfAccountsPath(params)),
	createChartOfAccount: (body: CreateChartOfAccountsRequest) =>
		idempotentMutation(`accounting:chart-of-accounts:create:${JSON.stringify(body)}`, (key) =>
			api.post<ChartOfAccountsRow>('/accounting/chart-of-accounts', body, {
				headers: { 'Idempotency-Key': key }
			})
		),
	patchChartOfAccount: (id: number, body: PatchChartOfAccountsRequest) =>
		idempotentMutation(`accounting:chart-of-accounts:update:${id}:${JSON.stringify(body)}`, (key) =>
			api.patch<ChartOfAccountsRow>(`/accounting/chart-of-accounts/${id}`, body, {
				headers: { 'Idempotency-Key': key }
			})
		),
	generalLedger: (params?: GeneralLedgerParams) =>
		api.get<AccountingPage<GeneralLedgerRow>>(buildGeneralLedgerPath(params)),
	journalDetail: (publicId: string) =>
		api.get<JournalDetail>(`/accounting/journal-entries/${publicId}`),
	sourceJournals: (params: SourceJournalParams) =>
		api.get<SourceJournalSummary[]>(buildSourceJournalsPath(params)),
	trialBalance: (params?: TrialBalanceParams) =>
		api.get<TrialBalanceResponse>(buildTrialBalancePath(params)),
	balanceSheet: (params?: StatementParams) =>
		api.get<FinancialStatementResponse>(buildBalanceSheetPath(params)),
	incomeStatement: (params?: IncomeStatementParams) =>
		api.get<FinancialStatementResponse>(buildIncomeStatementPath(params))
};
