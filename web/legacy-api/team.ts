import type { TeamMember, AgentEscalation } from '$lib/types';
import { api } from '../client';

export const team = {
	list: (projectId: number) =>
		api.get<TeamMember[]>(`/teams?projectId=${projectId}`),

	get: (id: number) =>
		api.get<TeamMember>(`/teams/${id}`),

	create: (data: {
		projectId: number;
		role: string;
		agentName: string;
		modelName: string;
		isPersistent?: boolean;
		configJson?: string;
		spawnPromptTemplate?: string;
		triggerStatuses?: string;
	}) => api.post<TeamMember>('/teams', data),

	getRoleTemplates: () =>
		api.get<Array<{ role: string; triggerStatuses: string; modelName: string; spawnPromptTemplate: string }>>('/teams/role-templates'),

	update: (id: number, data: Partial<TeamMember>) =>
		api.patch<TeamMember>(`/teams/${id}`, data),

	delete: (id: number) =>
		api.delete(`/teams/${id}`),

	spawn: (id: number, sessionId?: string) =>
		api.post(`/teams/${id}/spawn`, { sessionId }),

	shutdown: (id: number, tokensUsed?: number) =>
		api.post(`/teams/${id}/shutdown`, { tokensUsed }),

	assignTask: (taskId: number, data: { teamMemberId?: number; assignedBy?: string }) =>
		api.post(`/tasks/${taskId}/assign`, data),

	claimTask: (taskId: number, agentName: string) =>
		api.post(`/tasks/${taskId}/claim`, { agentName }),
};

export const escalations = {
	list: (projectId: number, status?: string) => {
		const params = new URLSearchParams({ projectId: String(projectId) });
		if (status) params.set('status', status);
		return api.get<AgentEscalation[]>(`/agents/escalations?${params}`);
	},

	create: (data: { projectId: number; description: string; taskId?: number }) =>
		api.post<AgentEscalation>('/agents/escalations', data),

	resolve: (id: number, data: { status?: string; resolution?: string }) =>
		api.patch<AgentEscalation>(`/agents/escalations/${id}`, data),
};
