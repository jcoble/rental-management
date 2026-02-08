import type { Project, Dashboard } from '$lib/types';
import { api } from '../client';

export const projects = {
	list: () => api.get<Project[]>('/projects'),
	get: (id: number) => api.get<Project>(`/projects/${id}`),
	create: (data: Partial<Project>) => api.post<Project>('/projects', data),
	update: (id: number, data: Partial<Project>) => api.patch<Project>(`/projects/${id}`, data),
	delete: (id: number) => api.delete(`/projects/${id}`),
	dashboard: (id: number) => api.get<Dashboard>(`/projects/${id}/dashboard`),
};
