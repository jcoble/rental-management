export type TenantDeleteInput = {
	fullName?: string | null;
	firstName?: string | null;
	lastName?: string | null;
	activeLeaseCount?: number | null;
};

export type TenantDeleteState = {
	message: string;
	confirmDisabled: boolean;
};

function tenantName(tenant: TenantDeleteInput) {
	const name = tenant.fullName?.trim() || `${tenant.firstName ?? ''} ${tenant.lastName ?? ''}`.trim();
	return name || 'This tenant';
}

export function getTenantDeleteState(tenant: TenantDeleteInput): TenantDeleteState {
	const name = tenantName(tenant);
	const activeLeaseCount = tenant.activeLeaseCount ?? 0;

	if (activeLeaseCount > 0) {
		const leaseNoun = activeLeaseCount === 1 ? 'lease' : 'leases';
		return {
			message: `${name} has ${activeLeaseCount} active ${leaseNoun}. End or reassign the ${leaseNoun === 'lease' ? 'lease' : 'leases'} before deleting this tenant.`,
			confirmDisabled: true,
		};
	}

	return {
		message: `Delete "${name}"? This cannot be undone.`,
		confirmDisabled: false,
	};
}
