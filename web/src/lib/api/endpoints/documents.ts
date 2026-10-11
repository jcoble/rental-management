import type { DocumentItem } from '$lib/types';
import { api } from '../client';
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
	upload: (
		entityType: string,
		entityId: number,
		file: File,
		category: string | undefined,
		clientOperationId: string
	): Promise<DocumentItem> => {
		const form = new FormData();
		form.append('file', file);
		form.append('entityType', entityType);
		form.append('entityId', String(entityId));
		form.append('clientOperationId', clientOperationId);
		if (category) form.append('category', category);
		return api.upload<DocumentItem>('/documents', form);
	},

	/**
	 * Delete a document.
	 * DELETE /api/v1/documents/{id}
	 */
	delete: (id: number, clientOperationId: string): Promise<void> =>
		api.delete(`/documents/${id}?clientOperationId=${encodeURIComponent(clientOperationId)}`),
};

/**
 * Same-origin proxy URL for a stored document blob. The SvelteKit route forwards
 * the user's auth cookie to the API so document downloads work under the same
 * browser contract as scanned lease/payment/expense files.
 */
export function documentFileHref(id: number, options?: { thumb?: boolean }): string {
	const base = `/document-file/${id}`;
	return options?.thumb ? `${base}?thumb=true` : base;
}

/**
 * Fetch the document blob through the cookie-authenticated proxy and trigger a browser download.
 */
export async function downloadDocument(id: number, fileName: string): Promise<void> {
	if (!browser) return;

	const response = await fetch(documentFileHref(id), { credentials: 'include' });

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
 * Fetch the document blob through the cookie-authenticated proxy and return a temporary object URL
 * suitable for inline preview (images, PDFs). Caller is responsible for calling
 * URL.revokeObjectURL when done.
 */
export async function fileObjectUrl(id: number, options?: { thumb?: boolean }): Promise<string> {
	const blob = await fileBlob(id, options);
	return URL.createObjectURL(blob);
}

/**
 * Fetch the document blob through the cookie-authenticated proxy. Used by inline renderers that
 * need the raw bytes rather than a browser object URL.
 */
export async function fileBlob(id: number, options?: { thumb?: boolean }): Promise<Blob> {
	const response = await fetch(documentFileHref(id, options), { credentials: 'include' });

	if (!response.ok) {
		throw new Error(`Document fetch failed (${response.status})`);
	}

	return response.blob();
}
