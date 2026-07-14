import type { AuditEntry, ListingWorkspace, SaveListingWorkspaceRequest, Unit, UnitDashboard, UnitHealth } from '$lib/types';
import { api, downloadFile } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import { normalizeOptionalApiResult } from '../optional-result';

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

	generateListingWorkspace: (id: number) => api.post<ListingWorkspace>(`/units/${id}/listing-workspace/generate`),

	saveListingWorkspace: (id: number, data: SaveListingWorkspaceRequest) =>
		api.put<ListingWorkspace>(`/units/${id}/listing-workspace`, data),

	attachListingPhoto: (id: number, photoId: number, file: File, clientOperationId: string) => {
		const form = new FormData();
		form.append('file', file);
		form.append('clientOperationId', clientOperationId);
		return api.upload<ListingWorkspace>(`/units/${id}/listing-workspace/photos/${photoId}/content`, form);
	},

	updateListingPhoto: (id: number, photoId: number, category: string, caption?: string | null) =>
		api.patch<ListingWorkspace>(`/units/${id}/listing-workspace/photos/${photoId}`, { category, caption }),

	removeListingPhoto: (id: number, photoId: number) =>
		api.delete<ListingWorkspace>(`/units/${id}/listing-workspace/photos/${photoId}/content`),

	reorderListingPhotos: (id: number, photoIds: number[]) =>
		api.put<ListingWorkspace>(`/units/${id}/listing-workspace/photos/order`, { photoIds }),

	downloadListingPhoto: (id: number, photoId: number) =>
		downloadFile(`/units/${id}/listing-workspace/photos/${photoId}/content`),

	prepareConnectedListing: (id: number, publicationId: number) =>
		api.post<ListingWorkspace>(`/units/${id}/listing-workspace/publications/${publicationId}/connected/prepare`),

	runConnectedListingCommand: (
		id: number,
		publicationId: number,
		action: 'publish' | 'update' | 'unpublish',
		clientOperationId: string
	) => api.post<ListingWorkspace>(
		`/units/${id}/listing-workspace/publications/${publicationId}/connected/${action}`,
		{ clientOperationId }
	),

	confirmListingSignal: (id: number, signalId: number, accept: boolean) =>
		api.post<ListingWorkspace>(`/units/${id}/listing-workspace/signals/${signalId}/confirm?accept=${accept}`),

	/** The unit's deep, paged history (timeline tab) — a bounded AtomicAuditLog union over the unit + children. */
	timeline: (id: number, params?: ListParams) => api.get<AuditEntry[]>(`/units/${id}/timeline${buildListQuery(params)}`),
};
