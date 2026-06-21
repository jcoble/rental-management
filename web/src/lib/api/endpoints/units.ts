import type { AuditEntry, Unit, UnitDashboard, UnitHealth } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

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
	listWithHealth: (params?: ListParams & { propertyId?: number }) => {
		const { propertyId, ...list } = params ?? {};
		return api.get<UnitHealth[]>(`/units/list-with-health${buildListQuery(list, { propertyId })}`);
	},

	get: (id: number) => api.get<Unit>(`/units/${id}`),

	/** The Unit Command Center at-a-glance aggregate (header, lease/tenant, stage, overview, timeline). */
	dashboard: (id: number) => api.get<UnitDashboard>(`/units/${id}/dashboard`),

	/** The unit's deep, paged history (timeline tab) — a bounded AuditLog union over the unit + children. */
	timeline: (id: number, params?: ListParams) => api.get<AuditEntry[]>(`/units/${id}/timeline${buildListQuery(params)}`),
};
