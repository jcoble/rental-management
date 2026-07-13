import { fetchApi, api } from '../client';

export type WorkspaceExperience = 'Management' | 'Leasing' | 'Maintenance' | 'Owner' | 'Tenant';
export type AssignmentScopeKind = 'AllProperties' | 'SelectedProperties' | 'AssignedWorkOrders';
export type MembershipStatusAction = 'Suspend' | 'Reactivate' | 'Revoke';

export interface TeamMemberSummary {
	userId: number;
	accessContextId: number;
	workspaceMembershipId: number;
	email: string;
	displayName: string;
	accessStatus: string;
	membershipStatus: string;
	accessRevision: number;
	assignmentCount: number;
	roleSummary: string;
	createdAtUtc: string;
}

export interface TeamMemberPage {
	items: TeamMemberSummary[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface TeamRoleProfile {
	key: string;
	displayName: string;
	description: string;
	defaultExperience: WorkspaceExperience;
	defaultScopeKind: AssignmentScopeKind;
}

export interface CreateWorkspaceMembershipRequest {
	email: string;
	displayName: string;
	roleProfileKey: string;
	scopeKind: AssignmentScopeKind;
	selectedPropertyIds: number[];
	effectiveFromUtc: string;
}

export interface CreateWorkspaceMembershipResult {
	userId: number;
	accessContextId: number;
	workspaceMembershipId: number;
	assignmentId: number;
	accessRevision: number;
	requiresAccountActivation: boolean;
}

export interface AtomicTeamResponse<T> {
	value: T;
	replayed: boolean;
}

function operation<T>(path: string, method: 'POST' | 'PATCH' | 'PUT', body: unknown) {
	return fetchApi<T>(path, {
		method,
		headers: { 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() },
		body: JSON.stringify(body)
	});
}

export const team = {
	members: (params: { skip: number; take: number; search?: string; sort?: string }) => {
		const query = new URLSearchParams({ skip: String(params.skip), take: String(params.take) });
		if (params.search?.trim()) query.set('search', params.search.trim());
		if (params.sort) query.set('sort', params.sort);
		return api.get<TeamMemberPage>(`/team/members?${query}`);
	},
	roleProfiles: () => api.get<TeamRoleProfile[]>('/team/role-profiles'),
	createMembership: (body: CreateWorkspaceMembershipRequest) =>
		operation<AtomicTeamResponse<CreateWorkspaceMembershipResult>>(
			'/team/memberships',
			'POST',
			body
		),
	changeStatus: (
		accessContextId: number,
		expectedAccessRevision: number,
		action: MembershipStatusAction
	) =>
		operation<AtomicTeamResponse<unknown>>(
			`/team/members/${accessContextId}/status`,
			'PATCH',
			{ expectedAccessRevision, action }
		)
};
