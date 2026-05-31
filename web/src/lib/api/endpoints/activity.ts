import type { ActivityLog } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const activity = {
	// The API scopes the activity feed to the caller's portfolio via the JWT portfolioId claim,
	// so the portfolio is not part of the path. `portfolioId` is kept only for query-cache keying
	// by callers. Route: GET /api/v1/activities?skip&take&search&sort&type&entityType&entityId.
	list: (
		_portfolioId: number,
		params?: ListParams & { type?: string; entityType?: string; entityId?: number }
	) => {
		const { type, entityType, entityId, ...list } = params ?? {};
		return api.get<ActivityLog[]>(`/activities${buildListQuery(list, { type, entityType, entityId })}`);
	},
};
