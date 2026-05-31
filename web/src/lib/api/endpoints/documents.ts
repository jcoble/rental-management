import type { DocumentItem } from '$lib/types';
import { api, refreshToken } from '../client';
import { CLIENT_API_BASE_URL } from '$lib/config';
import { getAuthState, isTokenExpired } from '$lib/stores/auth.svelte';
import { browser } from '$app/environment';

export const documents = {
	/**
	 * List all documents for a given entity.
	 * GET /api/v1/documents?entityType=&entityId=
	 */
	list: (entityType: string, entityId: number): Promise<DocumentItem[]> =>
		api.get<DocumentItem[]>(`/documents?entityType=${encodeURIComponent(entityType)}&entityId=${entityId}`),

	/**
	 * Upload a file to a given entity.
	 * POST /api/v1/documents (multipart)
	 */
	upload: (entityType: string, entityId: number, file: File, category?: string): Promise<DocumentItem> => {
		const form = new FormData();
		form.append('file', file);
		form.append('entityType', entityType);
		form.append('entityId', String(entityId));
		if (category) form.append('category', category);
		return api.upload<DocumentItem>('/documents', form);
	},

	/**
	 * Delete a document.
	 * DELETE /api/v1/documents/{id}
	 */
	delete: (id: number): Promise<void> => api.delete(`/documents/${id}`),
};

/**
 * Fetch the document blob with a bearer token and trigger a browser download.
 * Mirrors downloadScheduleECsv from accounting.ts.
 */
export async function downloadDocument(id: number, fileName: string): Promise<void> {
	if (!browser) return;

	if (isTokenExpired(120)) {
		try {
			await refreshToken();
		} catch {
			// Proceed; bearer may still be usable.
		}
	}

	const { accessToken } = getAuthState();
	const url = `${CLIENT_API_BASE_URL}/documents/${id}/file`;

	const response = await fetch(url, {
		credentials: 'include',
		headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {}
	});

	if (!response.ok) {
		throw new Error(`Document download failed (${response.status})`);
	}

	const blob = await response.blob();
	const objectUrl = URL.createObjectURL(blob);
	const a = document.createElement('a');
	a.href = objectUrl;
	a.download = fileName;
	document.body.appendChild(a);
	a.click();
	document.body.removeChild(a);
	URL.revokeObjectURL(objectUrl);
}

/**
 * Fetch the document blob with a bearer token and return a temporary object URL
 * suitable for inline preview (images, PDFs). Caller is responsible for calling
 * URL.revokeObjectURL when done.
 */
export async function fileObjectUrl(id: number): Promise<string> {
	if (isTokenExpired(120)) {
		try {
			await refreshToken();
		} catch {
			// Proceed.
		}
	}

	const { accessToken } = getAuthState();
	const url = `${CLIENT_API_BASE_URL}/documents/${id}/file`;

	const response = await fetch(url, {
		credentials: 'include',
		headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {}
	});

	if (!response.ok) {
		throw new Error(`Document fetch failed (${response.status})`);
	}

	const blob = await response.blob();
	return URL.createObjectURL(blob);
}
