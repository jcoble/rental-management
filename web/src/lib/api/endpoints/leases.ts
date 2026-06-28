import type { EsignStatus, Lease, LeaseLedger, LeaseQuestionResponse, LeaseStatus } from '$lib/types';
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

/** Response from GET /api/v1/leases/{id}/document-status. */
export interface LeaseDocumentStatusResponse {
	leaseId: number;
	hasDocument: boolean;
	storedFileId?: number | null;
	fileName?: string | null;
	fileSize?: number | null;
	downloadUrl?: string | null;
	generatedAt?: string | null;
}

/** Response from GET /api/v1/leases/{id}/signature-status (and the send-for-signature POST). */
export interface LeaseSignatureStatusResponse {
	leaseId: number;
	esignStatus: EsignStatus;
	leaseStatus: LeaseStatus;
	envelopeId?: string | null;
	hasSignedDocument: boolean;
	testId?: string | null;
}

/** Response from GET /api/v1/leases/{id}/signature-queue. */
export interface LeaseSignatureQueueResponse {
	leaseId: number;
	items: LeaseSignatureQueueItemResponse[];
}

export interface LeaseSignatureQueueItemResponse {
	id: number;
	recipientEmail: string;
	subject?: string | null;
	status: 'Queued' | 'Retrying' | 'Sent' | 'Failed' | 'DeliveryDisabled' | string;
	queuedAt: string;
	statusAt: string;
	sentAt?: string | null;
	failedAt?: string | null;
	retryCount: number;
	error?: string | null;
	signatureRequestId?: string | null;
	signingUrl?: string | null;
}

export interface LeaseListParams extends ListParams {
	tenantId?: number;
	propertyId?: number;
}

export interface LeaseListPageParams extends LeaseListParams {
	unitId?: number;
	status?: string;
}

export interface LeaseListResponse {
	items: Lease[];
	totalCount: number;
	skip: number;
	take: number;
}

export const leases = {
	list: (portfolioId: number, params?: LeaseListParams) => {
		const { tenantId, propertyId, ...list } = params ?? {};
		return api.get<Lease[]>(`/leases${buildListQuery(list, { portfolioId, tenantId, propertyId })}`);
	},
	listPage: (portfolioId: number, params?: LeaseListPageParams) => {
		const { tenantId, propertyId, unitId, status, ...list } = params ?? {};
		return api.get<LeaseListResponse>(
			`/leases/page${buildListQuery(list, { portfolioId, tenantId, propertyId, unitId, status })}`
		);
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

	/** GET /api/v1/leases/{id}/document-status — generated agreement metadata without streaming the PDF. */
	documentStatus: (id: number) =>
		api.get<LeaseDocumentStatusResponse>(`/leases/${id}/document-status`),

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

	/**
	 * POST /api/v1/leases/{id}/send-for-signature — kicks off the e-sign workflow.
	 * Defaults the signer to the lease's tenant when no name/email is supplied.
	 * Returns 503 (surfaced as ApiError.status) when no e-sign provider is configured;
	 * callers should treat that as "not set up yet" rather than a hard failure.
	 */
	sendForSignature: (id: number, body?: { signerName?: string; signerEmail?: string }) =>
		api.post<LeaseSignatureStatusResponse>(`/leases/${id}/send-for-signature`, body ?? {}),

	/** GET /api/v1/leases/{id}/signature-status — current e-sign + lease status for this lease. */
	signatureStatus: (id: number) =>
		api.get<LeaseSignatureStatusResponse>(`/leases/${id}/signature-status`),

	/** GET /api/v1/leases/{id}/signature-queue — recent lease e-sign email queue rows. */
	signatureQueue: (id: number) =>
		api.get<LeaseSignatureQueueResponse>(`/leases/${id}/signature-queue`),

	/**
	 * GET /api/v1/leases/{id}/signed-document — streams the signed PDF with the bearer
	 * token attached, triggering a browser download via a temporary object URL. Mirrors
	 * downloadDocument above. Returns false when no signed document exists yet (404).
	 */
	downloadSignedDocument: async (id: number): Promise<boolean> => {
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
		const url = `${CLIENT_API_BASE_URL}/leases/${id}/signed-document`;

		const response = await fetch(url, {
			credentials: 'include',
			headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {}
		});

		if (response.status === 404) return false;
		if (!response.ok) {
			throw new Error(`Signed lease download failed (${response.status})`);
		}

		const blob = await response.blob();
		const objectUrl = URL.createObjectURL(blob);
		const a = document.createElement('a');
		a.href = objectUrl;
		a.download = `signed-lease-${id}.pdf`;
		document.body.appendChild(a);
		a.click();
		document.body.removeChild(a);
		URL.revokeObjectURL(objectUrl);
		return true;
	},
};
