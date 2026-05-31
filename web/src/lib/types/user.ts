/**
 * User & auth types — mirror the RentalCommand.Api AuthDtos contract.
 *
 * The API serializes camelCase. User keys are integers (matching the
 * Identity users / domain entities). Roles is an array of role names.
 */

export type UserRole = 'Admin' | 'Manager' | 'Agent' | 'Owner' | 'Tenant';

/** Matches RentalCommand.Api.DTOs.UserDto */
export interface User {
	id: number;
	email: string;
	displayName: string;
	/** Null for users not scoped to a single portfolio (e.g. platform admins). */
	portfolioId: number | null;
	/** Set when the account is linked to an Owner entity. */
	ownerEntityId: number | null;
	/** Set when the account is linked to a Tenant entity. */
	tenantId: number | null;
	roles: string[];
	emailVerified: boolean;
}

/** Matches RentalCommand.Api.DTOs.LoginRequest */
export interface LoginRequest {
	email: string;
	password: string;
}

/** Matches RentalCommand.Api.DTOs.RegisterRequest */
export interface RegisterRequest {
	email: string;
	password: string;
	displayName: string;
}

/** Matches RentalCommand.Api.DTOs.LoginResponse */
export interface LoginResponse {
	accessToken: string;
	/** ISO-8601 timestamp (DateTime serialized by the API). */
	accessTokenExpiration: string;
	user: User;
}

export function hasRole(user: User | null, ...roles: string[]): boolean {
	if (!user) return false;
	return roles.some((r) => user.roles.includes(r));
}

export function isAdmin(user: User | null): boolean {
	return hasRole(user, 'Admin');
}

export function isManager(user: User | null): boolean {
	return hasRole(user, 'Manager');
}

/** Staff = anyone who works the management side (not owner/tenant portal users). */
export function isStaff(user: User | null): boolean {
	return hasRole(user, 'Admin', 'Manager', 'Agent');
}

export function isPortalUser(user: User | null): boolean {
	return hasRole(user, 'Owner', 'Tenant');
}
