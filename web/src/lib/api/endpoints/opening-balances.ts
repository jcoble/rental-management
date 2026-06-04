import { api } from '../client';

/**
 * A per-lease opening balance — the tenant balance carried over from before the
 * landlord started using Rental Command. Signed amount: positive = the tenant
 * owed money, negative = the tenant had a credit. The lease ledger renders this
 * automatically as an "Opening" entry once one exists.
 *
 * Response from GET/POST/PATCH /api/v1/opening-balances (OpeningBalanceResponse).
 */
export interface OpeningBalanceResponse {
	id: number;
	portfolioId: number;
	leaseId: number;
	/** Signed: + = tenant owed; - = credit. */
	amount: number;
	/** ISO date (YYYY-MM-DD). */
	asOfDate: string;
	note?: string;
	createdAt: string;
	updatedAt: string;
	testId: string;
}

/** Body for POST /api/v1/opening-balances. */
export interface CreateOpeningBalanceBody {
	leaseId: number;
	/** Signed: + = tenant owed; - = credit. */
	amount: number;
	/** ISO date (YYYY-MM-DD). */
	asOfDate: string;
	note?: string;
}

/** Body for PATCH /api/v1/opening-balances/{id} — any subset. */
export interface UpdateOpeningBalanceBody {
	amount?: number;
	asOfDate?: string;
	note?: string;
}

export const openingBalances = {
	/** GET /api/v1/opening-balances?leaseId={id} — newest asOfDate first. */
	list: (leaseId: number) =>
		api.get<OpeningBalanceResponse[]>(`/opening-balances?leaseId=${leaseId}`),

	/** POST /api/v1/opening-balances — 409 if the lease already has one (use update). */
	create: (body: CreateOpeningBalanceBody) =>
		api.post<OpeningBalanceResponse>('/opening-balances', body),

	/** PATCH /api/v1/opening-balances/{id}. */
	update: (id: number, body: UpdateOpeningBalanceBody) =>
		api.patch<OpeningBalanceResponse>(`/opening-balances/${id}`, body),

	/** DELETE /api/v1/opening-balances/{id} → 204. */
	remove: (id: number) => api.delete(`/opening-balances/${id}`),
};
