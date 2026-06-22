import type { TeamMember, CreateTeamMemberResponse, TeamMemberListResponse, UserRole } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export interface CreateTeamMemberBody {
	email: string;
	displayName?: string;
	role: UserRole;
	temporaryPassword?: string;
}

export const adminUsers = {
	list: () => api.get<TeamMember[]>('/admin/users?take=50'),
	listPage: (params?: ListParams) =>
		api.get<TeamMemberListResponse>(`/admin/users/page${buildListQuery(params)}`),
	create: (body: CreateTeamMemberBody) =>
		api.post<CreateTeamMemberResponse>('/admin/users', body),
	setRole: (id: number, role: UserRole) =>
		api.patch<TeamMember>(`/admin/users/${id}/role`, { role }),
	setActive: (id: number, isActive: boolean) =>
		api.patch<TeamMember>(`/admin/users/${id}/active`, { isActive })
};
