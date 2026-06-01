import type { Message } from '$lib/types';
import { api } from '../client';

export const messages = {
	list: (status?: string) =>
		api.get<Message[]>(`/messages${status ? `?status=${status}` : ''}`),
	get: (id: number) => api.get<Message>(`/messages/${id}`),
	reply: (id: number, data: { reply: string; status?: string }) =>
		api.post<Message>(`/messages/${id}/reply`, data),
	setStatus: (id: number, status: string) =>
		api.patch<Message>(`/messages/${id}/status`, { status }),
};
