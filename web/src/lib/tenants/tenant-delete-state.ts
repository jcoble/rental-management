export type TenantDeleteInput = {
	fullName?: string | null;
	firstName?: string | null;
	lastName?: string | null;
	activeLeaseCount?: number | null;
	leaseHistoryCount?: number | null;
	canDelete?: boolean | null;
	deleteBlockedReason?: string | null;
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
	const leaseHistoryCount = tenant.leaseHistoryCount ?? 0;
	const blockedReason = tenant.deleteBlockedReason?.trim();

	if (activeLeaseCount > 0) {
		const leaseNoun = activeLeaseCount === 1 ? 'lease' : 'leases';
		return {
			message:
				blockedReason ||
				`${name} has ${activeLeaseCount} active ${leaseNoun}. End or reassign the ${leaseNoun === 'lease' ? 'lease' : 'leases'} before deleting this tenant.`,
			confirmDisabled: true,
		};
	}

	if (leaseHistoryCount > 0) {
		return {
			message:
				blockedReason ||
				`${name} has lease history. Keep the tenant record to preserve past leases and payments.`,
			confirmDisabled: true,
		};
	}

	if (tenant.canDelete === false) {
		return {
			message: blockedReason || `${name} cannot be deleted right now.`,
			confirmDisabled: true,
		};
	}

	return {
		message: `Delete "${name}"? This cannot be undone.`,
		confirmDisabled: false,
	};
}
