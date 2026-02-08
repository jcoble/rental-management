import type { Label } from '$lib/types';
import { api } from '../client';

export const labels = {
	list: (projectId: number) => api.get<Label[]>(`/projects/${projectId}/labels`),
	create: (data: Partial<Label>) => api.post<Label>(`/projects/${data.projectId}/labels`, data),
	update: (id: number, data: Partial<Label> & { projectId: number }) =>
		api.patch<Label>(`/projects/${data.projectId}/labels/${id}`, data),
	delete: (id: number, projectId: number) =>
		api.delete(`/projects/${projectId}/labels/${id}`),
};
