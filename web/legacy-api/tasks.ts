import type { Task } from '$lib/types';
import { api } from '../client';

export const tasks = {
	list: (params?: Record<string, string>) => {
		const query = params ? '?' + new URLSearchParams(params).toString() : '';
		return api.get<Task[]>(`/tasks${query}`);
	},
	get: (id: number) => api.get<Task>(`/tasks/${id}`),
	create: (data: Partial<Task>) => api.post<Task>('/tasks', data),
	update: (id: number, data: Partial<Task>) => api.patch<Task>(`/tasks/${id}`, data),
	delete: (id: number) => api.delete(`/tasks/${id}`),
	move: (id: number, status: string, orderInColumn: number) =>
		api.post<Task>(`/tasks/${id}/move`, { status, orderInColumn }),
	reorder: (status: string, items: { id: number; order: number }[]) =>
		api.patch('/tasks/reorder', { status, items }),
};
