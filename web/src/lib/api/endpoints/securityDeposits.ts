import type { SecurityDepositHolding } from '$lib/types';
import { api } from '../client';

export const securityDeposits = {
	/** GET /api/v1/security-deposits[?leaseId=N] — scoped by JWT claim. */
	list: (leaseId?: number) =>
		api.get<SecurityDepositHolding[]>(
			leaseId != null ? `/security-deposits?leaseId=${leaseId}` : '/security-deposits'
		),

	/** GET /api/v1/security-deposits/{id} */
	get: (id: number) => api.get<SecurityDepositHolding>(`/security-deposits/${id}`),

	/** POST /api/v1/security-deposits */
	create: (body: { leaseId: number; amount?: number; notes?: string }) =>
		api.post<SecurityDepositHolding>('/security-deposits', body),

	/** POST /api/v1/security-deposits/{id}/deductions */
	addDeduction: (id: number, body: { reason: string; amount: number; notes?: string }) =>
		api.post<SecurityDepositHolding>(`/security-deposits/${id}/deductions`, body),

	/** POST /api/v1/security-deposits/{id}/return */
	processReturn: (id: number, body: { notes?: string }) =>
		api.post<SecurityDepositHolding>(`/security-deposits/${id}/return`, body),
};
