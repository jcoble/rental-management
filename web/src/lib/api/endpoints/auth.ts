import type { AuthUser } from '$lib/types';
import { api, authToken } from '../client';

export const auth = {
	login: async (data: { portfolioId: number; email: string; password: string }) => {
		const result = await api.post<{ token: string; expiresAt: string; user: AuthUser }>('/auth/login', data);
		authToken.set(result.token);
		return result;
	},
	me: () => api.get<AuthUser>('/auth/me'),
	logout: async () => {
		try {
			await api.post('/auth/logout');
		} finally {
			authToken.clear();
		}
	},
	listUsers: (portfolioId: number) => api.get(`/auth/users?portfolioId=${portfolioId}`),
	createUser: (data: Record<string, unknown>) => api.post('/auth/users', data),
	updateUser: (id: number, data: Record<string, unknown>) => api.patch(`/auth/users/${id}`, data),
};
