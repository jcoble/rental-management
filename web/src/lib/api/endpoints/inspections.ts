import type {
	Inspection,
	InspectionCompleteResult,
	InspectionDetail,
	InspectionItem,
	InspectionItemInput,
	InspectionItemUpdate,
	InspectionTemplate,
	InspectionTemplateInput,
} from '$lib/types';
import { api, refreshToken } from '../client';
import { CLIENT_API_BASE_URL } from '$lib/config';
import { getAuthState, isTokenExpired } from '$lib/stores/auth.svelte';
import { browser } from '$app/environment';
import { idempotentMutation } from '../idempotency';
import { buildListQuery, type ListParams } from '../list-params';

export interface UnitInspectionListParams extends ListParams {
	unitId: number;
}

export interface InspectionListResponse {
	items: Inspection[];
	totalCount: number;
	skip: number;
	take: number;
}

export const inspections = {
	list: (portfolioId: number) => api.get<Inspection[]>(`/inspections?portfolioId=${portfolioId}`),
	listUnitPage: ({ unitId, ...list }: UnitInspectionListParams) =>
		api.get<InspectionListResponse>(
			`/inspections/page${buildListQuery(list, { unitId })}`
		),
	get: (id: number) => api.get<InspectionDetail>(`/inspections/${id}`),
	// Create now returns the full detail (items materialized as Pending when a templateId is given).
	create: (data: Record<string, unknown>) =>
		idempotentMutation(`inspections:create:${JSON.stringify(data)}`, (key) =>
			api.post<InspectionDetail>('/inspections', data, { headers: { 'Idempotency-Key': key } })
		),
	update: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`inspections:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<Inspection>(`/inspections/${id}`, data, { headers: { 'Idempotency-Key': key } })
		),
	delete: (id: number) =>
		idempotentMutation(`inspections:delete:${id}`, (key) =>
			api.delete(`/inspections/${id}`, { headers: { 'Idempotency-Key': key } })
		),

	// Smart-checklist templates. Built-in templates have NEGATIVE ids — pass back to create as-is.
	templates: () => api.get<InspectionTemplate[]>('/inspections/templates'),
	getTemplate: (id: number) => api.get<InspectionTemplate>(`/inspections/templates/${id}`),
	createTemplate: (data: InspectionTemplateInput) =>
		idempotentMutation(`inspection-templates:create:${JSON.stringify(data)}`, (key) =>
			api.post<InspectionTemplate>('/inspections/templates', data, {
				headers: { 'Idempotency-Key': key },
			})
		),
	updateTemplate: (id: number, data: InspectionTemplateInput) =>
		idempotentMutation(`inspection-templates:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<InspectionTemplate>(`/inspections/templates/${id}`, data, {
				headers: { 'Idempotency-Key': key },
			})
		),
	deleteTemplate: (id: number) =>
		idempotentMutation(`inspection-templates:delete:${id}`, (key) =>
			api.delete<void>(`/inspections/templates/${id}`, { headers: { 'Idempotency-Key': key } })
		),

	// Add/edit/delete/reorder checklist questions on an editable scheduled inspection.
	createItem: (inspectionId: number, data: InspectionItemInput) =>
		idempotentMutation(`inspections:${inspectionId}:items:create:${JSON.stringify(data)}`, (key) =>
			api.post<InspectionItem>(`/inspections/${inspectionId}/items`, data, {
				headers: { 'Idempotency-Key': key },
			})
		),
	updateItem: (inspectionId: number, itemId: number, data: InspectionItemUpdate) =>
		idempotentMutation(
			`inspections:${inspectionId}:items:${itemId}:update:${JSON.stringify(data)}`,
			(key) => api.patch<InspectionItem>(`/inspections/${inspectionId}/items/${itemId}`, data, {
				headers: { 'Idempotency-Key': key },
			})
		),
	deleteItem: (inspectionId: number, itemId: number) =>
		idempotentMutation(`inspections:${inspectionId}:items:${itemId}:delete`, (key) =>
			api.delete<void>(`/inspections/${inspectionId}/items/${itemId}`, {
				headers: { 'Idempotency-Key': key },
			})
		),
	reorderItems: (inspectionId: number, itemIds: number[]) =>
		idempotentMutation(`inspections:${inspectionId}:items:reorder:${itemIds.join(',')}`, (key) =>
			api.patch<InspectionItem[]>(`/inspections/${inspectionId}/items/reorder`, { itemIds }, {
				headers: { 'Idempotency-Key': key },
			})
		),

	// Attach a previously-uploaded document (storedFileId) as the item's photo.
	setItemPhoto: (inspectionId: number, itemId: number, storedFileId: number) =>
		idempotentMutation(`inspections:${inspectionId}:items:${itemId}:photo:${storedFileId}`, (key) =>
			api.post<InspectionItem>(`/inspections/${inspectionId}/items/${itemId}/photo`, { storedFileId }, {
				headers: { 'Idempotency-Key': key },
			})
		),

	// Finalize the inspection → renders the PDF report and spawns work orders for failed items.
	complete: (id: number) =>
		idempotentMutation(`inspections:${id}:complete`, (key) =>
			api.post<InspectionCompleteResult>(`/inspections/${id}/complete`, {}, {
				headers: { 'Idempotency-Key': key },
			})
		),
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
