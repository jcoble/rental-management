import type { Portfolio, Dashboard, SandboxState, GettingStartedSignalsResponse } from '$lib/types';
import { api } from '../client';
import { idempotentMutation } from '../idempotency';

export const portfolios = {
	list: () => api.get<Portfolio[]>('/portfolios'),
	get: (id: number) => api.get<Portfolio>(`/portfolios/${id}`),
	update: (id: number, data: Partial<Portfolio>) =>
		idempotentMutation(`portfolios:update:${id}:${JSON.stringify(data)}`, (operationKey) =>
			api.patch<Portfolio>(`/portfolios/${id}`, data, {
				headers: { 'Idempotency-Key': operationKey }
			})
		),
	delete: (id: number) =>
		idempotentMutation(`portfolios:delete:${id}`, (operationKey) =>
			api.delete(`/portfolios/${id}`, {
				headers: { 'Idempotency-Key': operationKey }
			})
		),
	dashboard: (id: number) => api.get<Dashboard>(`/portfolios/${id}/dashboard`),
	gettingStarted: () => api.get<GettingStartedSignalsResponse>('/portfolios/getting-started'),

	/** Current Sandbox/Live state of the caller's account. Scoped by the validated workspace context. */
	sandboxState: () => api.get<SandboxState>('/portfolio/sandbox-state'),
	/**
	 * One-way "Go Live": wipes the caller's seeded demo data and flips the account to Live.
	 * Irreversible. Returns the new (Live) state.
	 */
	goLive: () => api.post<SandboxState>('/portfolio/go-live'),
	/**
	 * Records the first-login Sandbox-vs-Live choice. `sandbox` seeds the demo portfolio; `live`
	 * keeps an empty real portfolio. Idempotent. Returns the resulting sandbox state.
	 */
	onboardingChoice: (mode: 'sandbox' | 'live') =>
		api.post<SandboxState>('/portfolio/onboarding-choice', { mode }),
};
