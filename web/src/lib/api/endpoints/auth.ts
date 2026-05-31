import type { LoginRequest, LoginResponse, User } from '$lib/types/user';
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
	/** Revoke the refresh token (cookie sent automatically via credentials). */
	logout: () => api.post<void>('/auth/logout'),
	listUsers: (portfolioId: number) => api.get(`/auth/users?portfolioId=${portfolioId}`),
	createUser: (data: Record<string, unknown>) => api.post('/auth/users', data),
	updateUser: (id: number, data: Record<string, unknown>) => api.patch(`/auth/users/${id}`, data)
};
