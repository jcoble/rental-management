import type { AnalyticsOverview } from '$lib/types';
import { api } from '../client';

export const analytics = {
	// GET /api/v1/analytics/overview — portfolio scope comes from the JWT claim.
	overview: () => api.get<AnalyticsOverview>('/analytics/overview'),
};
