import type {
	BankingSummary,
	BankConnection,
	BankReviewQueueResponse,
	BankTransaction,
	BankTransactionListResponse,
	ConfirmBankMatchRequest,
	ExchangePlaidPublicTokenRequest,
	ImportBankTransactionsRequest,
	ImportBankTransactionsResponse,
	MatchBankTransactionRequest,
	OperationalBankTransaction,
	PlaidLinkTokenResponse,
	PlaidSettings,
	RouteBankTransactionRequest,
	SyncBankConnectionResponse
} from '$lib/types';
import { api } from '../client';
import { buildListQuery } from '../list-params';

export const banking = {
	summary: () => api.get<BankingSummary>('/banking/summary'),
	connections: () => api.get<BankConnection[]>('/banking/connections'),
	plaidSettings: () => api.get<PlaidSettings>('/banking/plaid/settings'),
	createPlaidLinkToken: (platform = 'web') =>
		api.post<PlaidLinkTokenResponse>('/banking/plaid/link-token', { platform }),
	exchangePlaidPublicToken: (request: ExchangePlaidPublicTokenRequest) =>
		api.post<BankConnection>('/banking/plaid/exchange-public-token', request),
	syncConnection: (id: number) => api.post<SyncBankConnectionResponse>(`/banking/connections/${id}/sync`, {}),
	transactions: (params: { status?: string; skip?: number; take?: number } = {}) => {
		const query = buildListQuery(undefined, {
			status: params.status,
			skip: params.skip,
			take: params.take
		});
		return api.get<BankTransactionListResponse>(`/banking/transactions${query}`);
	},
	importTransactions: (request: ImportBankTransactionsRequest) =>
		api.post<ImportBankTransactionsResponse>('/banking/transactions/import', request),
	match: (id: number, request: MatchBankTransactionRequest) =>
		api.post<OperationalBankTransaction>(`/banking/transactions/${id}/match`, request),
	routeTransaction: (id: number, request: RouteBankTransactionRequest) =>
		api.put<OperationalBankTransaction>(`/banking/transactions/${id}/route`, request),
	clearMatch: (
		id: number,
		request: Pick<ConfirmBankMatchRequest, 'operationKey' | 'expectedUpdatedAtUtc'>
	) => api.post<BankTransaction>(`/banking/transactions/${id}/clear-match`, request),
	reviewQueue: (params: { skip?: number; take?: number } = {}) => {
		const query = buildListQuery(undefined, {
			skip: params.skip,
			take: params.take
		});
		return api.get<BankReviewQueueResponse>(`/banking/review-queue${query}`);
	},
	confirmMatch: (id: number, request: ConfirmBankMatchRequest) =>
		api.post<OperationalBankTransaction>(`/banking/transactions/${id}/confirm-match`, request),
	dismissMatch: (
		id: number,
		request: Pick<ConfirmBankMatchRequest, 'operationKey' | 'expectedUpdatedAtUtc'>
	) => api.post<BankTransaction>(`/banking/transactions/${id}/dismiss-match`, request),
	// Mark a bank line as personal / not business money. It leaves the unmatched review queue and is
	// excluded from the books (server sets MatchStatus="Removed"), but stays listable via ?status=Removed.
	// Un-ignore by calling clearMatch (resets it back to Unmatched).
	ignore: (
		id: number,
		request: Pick<ConfirmBankMatchRequest, 'operationKey' | 'expectedUpdatedAtUtc'>
	) => api.post<BankTransaction>(`/banking/transactions/${id}/ignore`, request)
};
