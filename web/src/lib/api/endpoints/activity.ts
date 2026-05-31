import type { ActivityLog } from '$lib/types';
import { api } from '../client';

export const activity = {
	// The API scopes the activity feed to the caller's portfolio via the JWT portfolioId claim,
	// so the portfolio is not part of the path. `portfolioId` is kept only for query-cache keying
	// by callers. Route: GET /api/v1/activities?skip&take&search&sort.
	list: (_portfolioId: number, take = 50) =>
		api.get<ActivityLog[]>(`/activities?take=${take}`),
};
