import type { ActivityLog } from '$lib/types';
import { api } from '../client';

export const activity = {
	list: (portfolioId: number, take = 50) =>
		api.get<ActivityLog[]>(`/portfolios/${portfolioId}/activity?take=${take}`),
};
