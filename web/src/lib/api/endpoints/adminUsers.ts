import type { TeamMember, CreateTeamMemberResponse, UserRole } from '$lib/types';
import { api } from '../client';

export interface CreateTeamMemberBody {
	email: string;
	displayName?: string;
	role: UserRole;
	temporaryPassword?: string;
}

export const adminUsers = {
	list: () => api.get<TeamMember[]>('/admin/users'),
	create: (body: CreateTeamMemberBody) =>
		api.post<CreateTeamMemberResponse>('/admin/users', body),
	setRole: (id: number, role: UserRole) =>
		api.patch<TeamMember>(`/admin/users/${id}/role`, { role }),
	setActive: (id: number, isActive: boolean) =>
		api.patch<TeamMember>(`/admin/users/${id}/active`, { isActive })
};
