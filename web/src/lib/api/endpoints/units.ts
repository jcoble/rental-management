import type { AuditEntry, ListingWorkspace, SaveListingWorkspaceRequest, Unit, UnitDashboard, UnitHealth } from '$lib/types';
import { api, downloadFile } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { normalizeOptionalApiResult } from '../optional-result';
import { idempotentMutation } from '../idempotency';

const idempotencyHeaders = (key: string): RequestInit => ({
	headers: { 'Idempotency-Key': key }
});

export interface UnitHealthListParams extends ListParams {
	propertyId?: number;
}

export interface UnitHealthListResponse {
	items: UnitHealth[];
	totalCount: number;
	skip: number;
	take: number;
}

/**
 * Unit endpoints. Units are portfolio-scoped through their owning property; the API resolves scope from
 * the server-validated workspace context, so it is not part of the path (kept only for query-cache keying by callers).
 */
export const units = {
	/** Plain unit list (optionally filtered by property). */
	list: (params?: ListParams & { propertyId?: number }) => {
		const { propertyId, ...list } = params ?? {};
		return api.get<Unit[]>(`/units${buildListQuery(list, { propertyId })}`);
	},

	/** Units with cheap health badges for the /units page (one projection query server-side). */
	listWithHealth: (params?: UnitHealthListParams) => {
		const { propertyId, ...list } = params ?? {};
		return api.get<UnitHealth[]>(`/units/list-with-health${buildListQuery(list, { propertyId })}`);
	},

	listWithHealthPage: (params?: UnitHealthListParams) => {
		const { propertyId, ...list } = params ?? {};
		return api.get<UnitHealthListResponse>(
			`/units/list-with-health/page${buildListQuery(list, { propertyId })}`
		);
	},

	get: (id: number) => api.get<Unit>(`/units/${id}`),

	update: (id: number, data: Record<string, unknown>) => api.patch<Unit>(`/units/${id}`, data),

	/** The Unit Command Center at-a-glance aggregate (header, lease/tenant, stage, overview, timeline). */
	dashboard: (id: number) => api.get<UnitDashboard>(`/units/${id}/dashboard`),

	listingWorkspace: async (id: number) =>
		normalizeOptionalApiResult(await api.get<ListingWorkspace | null | undefined>(`/units/${id}/listing-workspace`)),

	generateListingWorkspace: (id: number) =>
		idempotentMutation(`listing:${id}:generate`, (key) =>
			api.post<ListingWorkspace>(`/units/${id}/listing-workspace/generate`, undefined, idempotencyHeaders(key))
		),

	saveListingWorkspace: (id: number, data: SaveListingWorkspaceRequest) =>
		idempotentMutation(`listing:${id}:save:${JSON.stringify(data)}`, (key) =>
			api.put<ListingWorkspace>(`/units/${id}/listing-workspace`, data, idempotencyHeaders(key))
		),

	attachListingPhoto: (id: number, photoId: number, file: File, clientOperationId: string) => {
		const form = new FormData();
		form.append('file', file);
		return api.upload<ListingWorkspace>(
			`/units/${id}/listing-workspace/photos/${photoId}/content`,
			form,
			idempotencyHeaders(clientOperationId)
		);
	},

	updateListingPhoto: (id: number, photoId: number, category: string, caption?: string | null) =>
		idempotentMutation(`listing:${id}:photo:${photoId}:update:${category}:${caption ?? ''}`, (key) =>
			api.patch<ListingWorkspace>(
				`/units/${id}/listing-workspace/photos/${photoId}`,
				{ category, caption },
				idempotencyHeaders(key)
			)
		),

	removeListingPhoto: (id: number, photoId: number) =>
		idempotentMutation(`listing:${id}:photo:${photoId}:remove`, (key) =>
			api.delete<ListingWorkspace>(`/units/${id}/listing-workspace/photos/${photoId}/content`, idempotencyHeaders(key))
		),

	reorderListingPhotos: (id: number, photoIds: number[]) =>
		idempotentMutation(`listing:${id}:photos:reorder:${photoIds.join(',')}`, (key) =>
			api.put<ListingWorkspace>(`/units/${id}/listing-workspace/photos/order`, { photoIds }, idempotencyHeaders(key))
		),

	downloadListingPhoto: (id: number, photoId: number) =>
		downloadFile(`/units/${id}/listing-workspace/photos/${photoId}/content`),

	prepareConnectedListing: (id: number, publicationId: number) =>
		idempotentMutation(`listing:${id}:publication:${publicationId}:prepare`, (key) =>
			api.post<ListingWorkspace>(
				`/units/${id}/listing-workspace/publications/${publicationId}/connected/prepare`,
				undefined,
				idempotencyHeaders(key)
			)
		),

	runConnectedListingCommand: (
		id: number,
		publicationId: number,
		action: 'publish' | 'update' | 'unpublish',
		clientOperationId: string
	) =>
		api.post<ListingWorkspace>(
			`/units/${id}/listing-workspace/publications/${publicationId}/connected/${action}`,
			undefined,
			idempotencyHeaders(clientOperationId)
		),

	confirmListingSignal: (id: number, signalId: number, accept: boolean) =>
		idempotentMutation(`listing:${id}:signal:${signalId}:confirm:${accept}`, (key) =>
			api.post<ListingWorkspace>(
				`/units/${id}/listing-workspace/signals/${signalId}/confirm?accept=${accept}`,
				undefined,
				idempotencyHeaders(key)
			)
		),

	/** The unit's deep, paged history (timeline tab) — a bounded AtomicAuditLog union over the unit + children. */
	timeline: (id: number, params?: ListParams) => api.get<AuditEntry[]>(`/units/${id}/timeline${buildListQuery(params)}`),
};
