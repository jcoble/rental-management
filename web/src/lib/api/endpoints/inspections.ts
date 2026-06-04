import type {
	Inspection,
	InspectionCompleteResult,
	InspectionDetail,
	InspectionItem,
	InspectionTemplate,
} from '$lib/types';
import { api, refreshToken } from '../client';
import { CLIENT_API_BASE_URL } from '$lib/config';
import { getAuthState, isTokenExpired } from '$lib/stores/auth.svelte';
import { browser } from '$app/environment';

export const inspections = {
	list: (portfolioId: number) => api.get<Inspection[]>(`/inspections?portfolioId=${portfolioId}`),
	get: (id: number) => api.get<InspectionDetail>(`/inspections/${id}`),
	// Create now returns the full detail (items materialized as Pending when a templateId is given).
	create: (data: Record<string, unknown>) => api.post<InspectionDetail>('/inspections', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Inspection>(`/inspections/${id}`, data),
	delete: (id: number) => api.delete(`/inspections/${id}`),

	// Smart-checklist templates. Built-in templates have NEGATIVE ids — pass back to create as-is.
	templates: () => api.get<InspectionTemplate[]>('/inspections/templates'),

	// Update a checklist item's result and/or note.
	updateItem: (inspectionId: number, itemId: number, data: { result?: string; note?: string }) =>
		api.patch<InspectionItem>(`/inspections/${inspectionId}/items/${itemId}`, data),

	// Attach a previously-uploaded document (storedFileId) as the item's photo.
	setItemPhoto: (inspectionId: number, itemId: number, storedFileId: number) =>
		api.post<InspectionItem>(`/inspections/${inspectionId}/items/${itemId}/photo`, { storedFileId }),

	// Finalize the inspection → renders the PDF report and spawns work orders for failed items.
	complete: (id: number) => api.post<InspectionCompleteResult>(`/inspections/${id}/complete`, {}),
};

/**
 * Fetch the completed inspection's PDF report with a bearer token and open it in
 * a new tab. A plain <a href> can't send the Authorization header, so we fetch
 * the blob manually and use a temporary object URL.
 */
export async function openInspectionReport(id: number): Promise<void> {
	if (!browser) return;

	if (isTokenExpired(120)) {
		try {
			await refreshToken();
		} catch {
			// Proceed; bearer may still be usable.
		}
	}

	const { accessToken } = getAuthState();
	const url = `${CLIENT_API_BASE_URL}/inspections/${id}/report`;

	const response = await fetch(url, {
		credentials: 'include',
		headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
	});

	if (!response.ok) {
		throw new Error(`Report download failed (${response.status})`);
	}

	const blob = await response.blob();
	const objectUrl = URL.createObjectURL(blob);
	const opened = window.open(objectUrl, '_blank');
	if (!opened) {
		// Pop-up blocked — fall back to a direct download.
		const a = document.createElement('a');
		a.href = objectUrl;
		a.download = `inspection-${id}-report.pdf`;
		document.body.appendChild(a);
		a.click();
		document.body.removeChild(a);
	}
	// Revoke after a delay so the new tab has time to load the blob.
	setTimeout(() => URL.revokeObjectURL(objectUrl), 60_000);
}
