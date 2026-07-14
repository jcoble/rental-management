import type {
	AccessEnvelope,
	EffectiveAccessContextOption,
	LoginRequest,
	LoginResponse,
	User,
	WorkspaceExperience
} from '$lib/types/user';
import { api, fetchPublicApi } from '../client';

/**
 * Auth endpoints. The session lives in httpOnly cookies set by the API /
 * SvelteKit form actions — these client helpers do NOT persist tokens
 * themselves. `login`/`logout` here are the client-side fallbacks; the primary
 * flows go through the /login form action and the /logout server route.
 */
export const auth = {
	login: (data: LoginRequest) =>
		fetchPublicApi<LoginResponse>('/auth/login', {
			method: 'POST',
			body: JSON.stringify(data)
		}),
	me: () => api.get<User>('/auth/me'),
	access: () => api.get<AccessEnvelope>('/auth/access'),
	contexts: () => api.get<EffectiveAccessContextOption[]>('/auth/contexts'),
	selectContext: (accessContextId: number) =>
		api.post<{ accessToken: string; accessTokenExpiration: string; access: AccessEnvelope }>(
			'/auth/contexts/select',
			{ accessContextId }
		),
	selectExperience: (experience: WorkspaceExperience) =>
		api.post<AccessEnvelope>('/auth/experience/select', { experience }),
	/**
	 * Re-send the email-verification message. Anonymous endpoint — the API always responds
	 * with a neutral success (no account enumeration), so callers can fire-and-forget.
	 */
	resendVerification: (email: string) =>
		fetchPublicApi<{ message: string }>('/auth/resend-verification', {
			method: 'POST',
			body: JSON.stringify({ email })
		}),
	/** Change the signed-in user's password (Bearer-authenticated). */
	changePassword: (currentPassword: string, newPassword: string) => {
		const operationKey = crypto.randomUUID();
		return api.post<{ message: string }>(
			'/auth/change-password',
			{ currentPassword, newPassword },
			{ headers: { 'Idempotency-Key': operationKey } }
		);
	},
	/** Revoke the refresh token (cookie sent automatically via credentials). */
	logout: () => api.post<void>('/auth/logout'),
	listUsers: (portfolioId: number) => api.get(`/auth/users?portfolioId=${portfolioId}`),
	createUser: (data: Record<string, unknown>) => api.post('/auth/users', data),
	updateUser: (id: number, data: Record<string, unknown>) => api.patch(`/auth/users/${id}`, data)
};
