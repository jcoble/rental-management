import { fetchApi, api } from '../client';
import { buildListQuery } from '../list-params';

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
	requiresAccountActivation: boolean;
	createdAtUtc: string;
}

export interface TeamMemberPage {
	items: TeamMemberSummary[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface TeamAssignmentSummary {
	assignmentId: number;
	roleProfileKey: string;
	roleProfileName: string;
	status: string;
	scopeKind: AssignmentScopeKind;
	selectedPropertyCount: number;
	selectedPropertyIds: number[];
	effectiveFromUtc: string;
	effectiveToUtc: string | null;
}

export interface TeamAssignmentPage {
	items: TeamAssignmentSummary[];
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

export interface WorkspaceTeamMutationResult {
	accessContextId: number;
	workspaceMembershipId: number;
	assignmentId: number | null;
	accessRevision: number;
	contextStatus: string;
	membershipStatus: string;
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
		const query = buildListQuery(undefined, {
			skip: params.skip,
			take: params.take,
			search: params.search?.trim(),
			sort: params.sort
		});
		return api.get<TeamMemberPage>(`/team/members${query}`);
	},
	assignments: (accessContextId: number, params: { skip?: number; take?: number } = {}) => {
		const query = buildListQuery(undefined, {
			skip: params.skip ?? 0,
			take: params.take ?? 250,
			sort: '-effectiveFrom'
		});
		return api.get<TeamAssignmentPage>(`/team/members/${accessContextId}/assignments${query}`);
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
		operation<AtomicTeamResponse<WorkspaceTeamMutationResult>>(
			`/team/members/${accessContextId}/status`,
			'PATCH',
			{ expectedAccessRevision, action }
		),
	addAssignment: (
		accessContextId: number,
		body: {
			expectedAccessRevision: number;
			roleProfileKey: string;
			scopeKind: AssignmentScopeKind;
			selectedPropertyIds: number[];
			effectiveFromUtc: string;
		}
	) =>
		operation<AtomicTeamResponse<WorkspaceTeamMutationResult>>(
			`/team/members/${accessContextId}/assignments`,
			'POST',
			body
		),
	endAssignment: (
		accessContextId: number,
		assignmentId: number,
		expectedAccessRevision: number,
		effectiveToUtc: string
	) =>
		operation<AtomicTeamResponse<WorkspaceTeamMutationResult>>(
			`/team/members/${accessContextId}/assignments/${assignmentId}/end`,
			'PATCH',
			{ expectedAccessRevision, effectiveToUtc }
		),
	replaceAssignmentProperties: (
		accessContextId: number,
		assignmentId: number,
		expectedAccessRevision: number,
		propertyIds: number[]
	) =>
		operation<AtomicTeamResponse<WorkspaceTeamMutationResult>>(
			`/team/members/${accessContextId}/assignments/${assignmentId}/properties`,
			'PUT',
			{ expectedAccessRevision, propertyIds }
		)
};
