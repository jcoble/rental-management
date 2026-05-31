import type { Payment } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const payments = {
	list: (portfolioId: number, params?: ListParams & { leaseId?: number }) => {
		const { leaseId, ...list } = params ?? {};
		return api.get<Payment[]>(`/payments${buildListQuery(list, { portfolioId, leaseId })}`);
	},
	get: (id: number) => api.get<Payment>(`/payments/${id}`),
	create: (data: Record<string, unknown>) => api.post<Payment>('/payments', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Payment>(`/payments/${id}`, data),
	markPaid: (id: number, data: Record<string, unknown>) => api.post<Payment>(`/payments/${id}/mark-paid`, data),
	delete: (id: number) => api.delete(`/payments/${id}`),
	// Payment collection rollups (collected/outstanding/overdue) live on the accounting
	// summary; use the `accounting` endpoint module rather than a payments-only summary route.

	/**
	 * Initiate an online payment for a scheduled/owed payment record.
	 *
	 * 200 → { clientSecret: string, publishableKey: string, transactionId: number }
	 * 503 → ApiError with status 503 when Stripe is not yet configured (expected default).
	 */
	createIntent: (id: number) =>
		api.post<{ clientSecret: string; publishableKey: string; transactionId: number }>(
			`/payments/${id}/create-intent`,
			{}
		),
};
