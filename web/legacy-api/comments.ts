import type { Comment } from '$lib/types';
import { api } from '../client';

export const comments = {
	list: (taskId: number) => api.get<Comment[]>(`/tasks/${taskId}/comments`),
	create: (taskId: number, data: Partial<Comment>) => api.post<Comment>(`/tasks/${taskId}/comments`, data),
	update: (id: number, data: Partial<Comment>) => api.patch<Comment>(`/comments/${id}`, data),
	delete: (id: number) => api.delete(`/comments/${id}`),
};
