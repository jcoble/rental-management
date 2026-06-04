import type { Lease, LeaseLedger, LeaseQuestionResponse } from '$lib/types';
import { api, refreshToken } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { CLIENT_API_BASE_URL } from '$lib/config';
import { getAuthState, isTokenExpired } from '$lib/stores/auth.svelte';
import { browser } from '$app/environment';

/** Response from POST /api/v1/leases/{id}/generate-document. */
export interface LeaseDocumentResponse {
	storedFileId: number;
	leaseId: number;
	fileName: string;
	fileSize: number;
	downloadUrl: string;
	generatedAt: string;
}

export const leases = {
	list: (portfolioId: number, params?: ListParams & { tenantId?: number; propertyId?: number }) => {
		const { tenantId, propertyId, ...list } = params ?? {};
		return api.get<Lease[]>(`/leases${buildListQuery(list, { portfolioId, tenantId, propertyId })}`);
	},
	get: (id: number) => api.get<Lease>(`/leases/${id}`),
	create: (data: Record<string, unknown>) => api.post<Lease>('/leases', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Lease>(`/leases/${id}`, data),
	delete: (id: number) => api.delete(`/leases/${id}`),
	ledger: (id: number) => api.get<LeaseLedger>(`/leases/${id}/ledger`),
	ask: (id: number, question: string) =>
		api.post<LeaseQuestionResponse>(`/leases/${id}/ask`, { question }),

	/** POST /api/v1/leases/{id}/generate-document — renders + stores a lease-agreement PDF. */
	generateDocument: (id: number) =>
		api.post<LeaseDocumentResponse>(`/leases/${id}/generate-document`, {}),

	/**
	 * GET /api/v1/leases/{id}/document — streams the latest lease-agreement PDF with the
	 * bearer token attached. A plain <a href> can't send the Authorization header, so we
	 * fetch the blob manually and trigger a browser download via a temporary object URL.
	 * Mirrors downloadMoveOutStatement / downloadYearEndPacket.
	 *
	 * Returns false when no document exists yet (404) so callers can fall back to
	 * showing the Generate button; throws on any other failure.
	 */
	downloadDocument: async (id: number, download = true): Promise<boolean> => {
		if (!browser) return false;

		// Proactively refresh if near expiry, mirroring the main client logic.
		if (isTokenExpired(120)) {
			try {
				await refreshToken();
			} catch {
				// Proceed; bearer may still be usable.
			}
		}

		const { accessToken } = getAuthState();
		const url = `${CLIENT_API_BASE_URL}/leases/${id}/document`;

		const response = await fetch(url, {
			credentials: 'include',
			headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {}
		});

		if (response.status === 404) return false;
		if (!response.ok) {
			throw new Error(`Lease agreement download failed (${response.status})`);
		}

		if (download) {
			const blob = await response.blob();
			const objectUrl = URL.createObjectURL(blob);
			const a = document.createElement('a');
			a.href = objectUrl;
			a.download = `lease-agreement-${id}.pdf`;
			document.body.appendChild(a);
			a.click();
			document.body.removeChild(a);
			URL.revokeObjectURL(objectUrl);
		}
		return true;
	},
};
