import type { Portfolio, Dashboard, SandboxState } from '$lib/types';
import { api } from '../client';

export const portfolios = {
	list: () => api.get<Portfolio[]>('/portfolios'),
	get: (id: number) => api.get<Portfolio>(`/portfolios/${id}`),
	create: (data: Partial<Portfolio>) => api.post<Portfolio>('/portfolios', data),
	update: (id: number, data: Partial<Portfolio>) => api.patch<Portfolio>(`/portfolios/${id}`, data),
	delete: (id: number) => api.delete(`/portfolios/${id}`),
	dashboard: (id: number) => api.get<Dashboard>(`/portfolios/${id}/dashboard`),

	/** Current Sandbox/Live state of the caller's account. Scoped server-side to the JWT portfolio. */
	sandboxState: () => api.get<SandboxState>('/portfolio/sandbox-state'),
	/**
	 * One-way "Go Live": wipes the caller's seeded demo data and flips the account to Live.
	 * Irreversible. Returns the new (Live) state.
	 */
	goLive: () => api.post<SandboxState>('/portfolio/go-live'),
};
