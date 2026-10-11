/**
 * User & auth types — mirror the RentalCommand.Api AuthDtos contract.
 *
 * The API serializes camelCase. User keys are integers (matching the
 * Identity users / domain entities). Authorization and active experience come exclusively from the
 * accompanying AccessEnvelope.
 */

export type WorkspaceExperience = 'Management' | 'Leasing' | 'Maintenance' | 'Owner' | 'Tenant';

export interface AccessIdentitySummary {
	userId: number;
	displayName: string;
	email: string | null;
}

export interface SelectedAccessContextSummary {
	accessContextId: number;
	portfolioId: number;
	workspaceName: string;
	accessRevision: number;
	activeExperience: WorkspaceExperience;
}

export interface AssignmentSummary {
	assignmentId: number;
	roleProfileKey: string;
	roleProfileName: string;
	status: string;
	scope: {
		kind: string;
		selectedPropertyCount: number;
		selectedProperties: Array<{ propertyId: number; name: string }>;
	};
}

export interface AccessEnvelope {
	identity: AccessIdentitySummary;
	selectedContext: SelectedAccessContextSummary;
	defaultExperience: WorkspaceExperience;
	availableExperiences: WorkspaceExperience[];
	assignments: AssignmentSummary[];
	navigation: Array<{ experience: WorkspaceExperience; capabilityKeys: string[] }>;
}

export interface EffectiveAccessContextOption {
	accessContextId: number;
	portfolioId: number;
	workspaceName: string;
	accessRevision: number;
	defaultExperience: WorkspaceExperience;
	totalEffectiveContexts: number;
}

export interface AccessContextSelectionRequiredResponse {
	code: 'ACCESS_CONTEXT_REQUIRED';
	error: string;
	contexts: EffectiveAccessContextOption[];
}

/** Matches RentalCommand.Api.DTOs.UserDto */
export interface User {
	id: number;
	email: string;
	displayName: string;
	emailVerified: boolean;
}

/** Matches RentalCommand.Api.DTOs.LoginRequest */
export interface LoginRequest {
	email: string;
	password: string;
	accessContextId?: number;
}

/** Matches RentalCommand.Api.DTOs.RegisterRequest */
export interface RegisterRequest {
	email: string;
	password: string;
	displayName: string;
	termsPrivacyAccepted: boolean;
}

/** Matches RentalCommand.Api.DTOs.LoginResponse */
export interface LoginResponse {
	accessToken: string;
	/** ISO-8601 timestamp (DateTime serialized by the API). */
	accessTokenExpiration: string;
	user: User;
	access: AccessEnvelope;
}

export function userFromAccessEnvelope(access: AccessEnvelope, emailVerified = true): User {
	return {
		id: access.identity.userId,
		email: access.identity.email ?? '',
		displayName: access.identity.displayName,
		emailVerified
	};
}

export function capabilityKeysForExperience(
	access: AccessEnvelope | null,
	experience: WorkspaceExperience | null
): ReadonlySet<string> {
	if (!access || !experience) return new Set();
	return new Set(
		access.navigation.find((item) => item.experience === experience)?.capabilityKeys ?? []
	);
}
