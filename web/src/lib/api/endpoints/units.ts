import type { AuditEntry, SaveUnitListingRequest, Unit, UnitDashboard, UnitHealth, UnitListing } from '$lib/types';
import { api } from '../client';
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
 * the JWT portfolioId claim, so it is not part of the path (kept only for query-cache keying by callers).
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

	listing: async (id: number) =>
		normalizeOptionalApiResult(await api.get<UnitListing | null | undefined>(`/units/${id}/listing`)),

	generateListing: (id: number) => api.post<UnitListing>(`/units/${id}/listing/generate`),

	saveListing: (id: number, data: SaveUnitListingRequest) =>
		api.put<UnitListing>(`/units/${id}/listing`, data),

	/** The unit's deep, paged history (timeline tab) — a bounded AuditLog union over the unit + children. */
	timeline: (id: number, params?: ListParams) => api.get<AuditEntry[]>(`/units/${id}/timeline${buildListQuery(params)}`),
};
