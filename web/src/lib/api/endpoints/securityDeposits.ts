import type { SecurityDepositHolding } from '$lib/types';
import { api, refreshToken } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { CLIENT_API_BASE_URL } from '$lib/config';
import { getAuthState, isTokenExpired } from '$lib/stores/auth.svelte';
import { browser } from '$app/environment';

export interface SecurityDepositListParams extends ListParams {
	leaseId?: number;
}

export interface SecurityDepositListResponse {
	items: SecurityDepositHolding[];
	totalCount: number;
	skip: number;
	take: number;
}

export const securityDeposits = {
	/** GET /api/v1/security-deposits[?leaseId=N] — scoped by JWT claim. */
	list: (leaseId?: number) =>
		api.get<SecurityDepositHolding[]>(
			leaseId != null ? `/security-deposits?leaseId=${leaseId}` : '/security-deposits'
		),

	/** GET /api/v1/security-deposits/page[?skip&take&sort&leaseId=N] — scoped by JWT claim. */
	listPage: (params?: SecurityDepositListParams) =>
		api.get<SecurityDepositListResponse>(
			`/security-deposits/page${buildListQuery(params, { leaseId: params?.leaseId })}`
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

/**
 * Download the move-out statement PDF for a deposit with the bearer token attached.
 * A plain <a href> can't send the Authorization header, so we fetch the blob
 * manually and trigger a browser download via a temporary object URL.
 * Mirrors downloadScheduleECsv from accounting.ts.
 */
export async function downloadMoveOutStatement(id: number): Promise<void> {
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
	const url = `${CLIENT_API_BASE_URL}/security-deposits/${id}/move-out-statement`;

	const response = await fetch(url, {
		credentials: 'include',
		headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {}
	});

	if (!response.ok) {
		throw new Error(`Move-out statement download failed (${response.status})`);
	}

	const blob = await response.blob();
	const objectUrl = URL.createObjectURL(blob);
	const a = document.createElement('a');
	a.href = objectUrl;
	a.download = `move-out-statement-${id}.pdf`;
	document.body.appendChild(a);
	a.click();
	document.body.removeChild(a);
	URL.revokeObjectURL(objectUrl);
}
