import type { Phase } from '$lib/types';
import { api } from '../client';

export const phases = {
	list: (milestoneId: number) => api.get<Phase[]>(`/milestones/${milestoneId}/phases`),
	get: (id: number) => api.get<Phase>(`/phases/${id}`),
	create: (milestoneId: number, data: Partial<Phase>) => api.post<Phase>(`/milestones/${milestoneId}/phases`, data),
	update: (id: number, data: Partial<Phase>) => api.patch<Phase>(`/phases/${id}`, data),
	delete: (id: number) => api.delete(`/phases/${id}`),
	changeStatus: (id: number, status: string) => api.post<Phase>(`/phases/${id}/status`, { status }),
};
