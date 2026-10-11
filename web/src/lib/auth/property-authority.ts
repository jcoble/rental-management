import { CAPABILITY } from './experience-policy.ts';
import type { AccessEnvelope } from '$lib/types/user';

export function hasAllPropertiesRentalsManageAuthority(access: AccessEnvelope | null): boolean {
	if (!access || access.selectedContext.activeExperience !== 'Management') return false;
	const capabilities = new Set(
		access.navigation.find((entry) => entry.experience === 'Management')?.capabilityKeys ?? []
	);
	if (!capabilities.has(CAPABILITY.rentalsManage)) return false;
	return access.assignments.some((assignment) =>
		assignment.status === 'Active' &&
		assignment.scope.kind === 'AllProperties'
	);
}
