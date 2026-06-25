import type {
	BankingSummary,
	BankConnection,
	BankReviewQueueResponse,
	BankTransaction,
	ConfirmBankMatchRequest,
	ExchangePlaidPublicTokenRequest,
	ImportBankTransactionsRequest,
	ImportBankTransactionsResponse,
	MatchBankTransactionRequest,
	PlaidLinkTokenResponse,
	PlaidSettings,
	SyncBankConnectionResponse
} from '$lib/types';
import { api } from '../client';

export const banking = {
	summary: () => api.get<BankingSummary>('/banking/summary'),
	connections: () => api.get<BankConnection[]>('/banking/connections'),
	plaidSettings: () => api.get<PlaidSettings>('/banking/plaid/settings'),
	createPlaidLinkToken: (platform = 'web') =>
		api.post<PlaidLinkTokenResponse>('/banking/plaid/link-token', { platform }),
	exchangePlaidPublicToken: (request: ExchangePlaidPublicTokenRequest) =>
		api.post<BankConnection>('/banking/plaid/exchange-public-token', request),
	syncConnection: (id: number) => api.post<SyncBankConnectionResponse>(`/banking/connections/${id}/sync`, {}),
	transactions: (status?: string) =>
		api.get<BankTransaction[]>(`/banking/transactions${status ? `?status=${encodeURIComponent(status)}` : ''}`),
	importTransactions: (request: ImportBankTransactionsRequest) =>
		api.post<ImportBankTransactionsResponse>('/banking/transactions/import', request),
	match: (id: number, request: MatchBankTransactionRequest) =>
		api.post<BankTransaction>(`/banking/transactions/${id}/match`, request),
	clearMatch: (id: number) =>
		api.post<BankTransaction>(`/banking/transactions/${id}/clear-match`, {}),
	reviewQueue: (params: { skip?: number; take?: number } = {}) => {
		const query = new URLSearchParams();
		if (params.skip !== undefined) query.set('skip', String(params.skip));
		if (params.take !== undefined) query.set('take', String(params.take));
		const suffix = query.toString();
		return api.get<BankReviewQueueResponse>(`/banking/review-queue${suffix ? `?${suffix}` : ''}`);
	},
	confirmMatch: (id: number, request: ConfirmBankMatchRequest = {}) =>
		api.post<BankTransaction>(`/banking/transactions/${id}/confirm-match`, request),
	dismissMatch: (id: number) =>
		api.post<BankTransaction>(`/banking/transactions/${id}/dismiss-match`, {}),
	// Mark a bank line as personal / not business money. It leaves the unmatched review queue and is
	// excluded from the books (server sets MatchStatus="Removed"), but stays listable via ?status=Removed.
	// Un-ignore by calling clearMatch (resets it back to Unmatched).
	ignore: (id: number) =>
		api.post<BankTransaction>(`/banking/transactions/${id}/ignore`, {})
};
