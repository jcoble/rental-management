export type TenantNoticeEmptyCopy = {
	message: string;
	description: string;
};

export type TenantNoticeEmptyState = TenantNoticeEmptyCopy & {
	showForceControls: boolean;
	leaseActionHref?: string;
	leaseActionLabel?: string;
};

export function getTenantNoticeEmptyCopy(forcedNoticeLabel?: string | null): TenantNoticeEmptyCopy {
	if (forcedNoticeLabel) {
		return {
			message: `No ${forcedNoticeLabel.toLowerCase()} could be created.`,
			description: 'This tenant needs an active eligible lease for that notice type.'
		};
	}

	return {
		message: 'No notices are due for this tenant right now.',
		description: 'Renewal, late-rent, and move-out notices appear here automatically when they come due.'
	};
}

export function getTenantNoticeEmptyState({
	forcedNoticeLabel,
	activeLeaseCount,
	tenantId
}: {
	forcedNoticeLabel?: string | null;
	activeLeaseCount: number;
	tenantId?: number | null;
}): TenantNoticeEmptyState {
	if (activeLeaseCount <= 0) {
		const safeTenantId = tenantId && Number.isInteger(tenantId) && tenantId > 0 ? tenantId : null;

		return {
			message: 'No notice can be created yet.',
			description: 'Create or activate a lease for this tenant before sending renewal or move-out notices.',
			showForceControls: false,
			leaseActionHref: safeTenantId ? `/applications?prepareMoveIn=1&tenantId=${safeTenantId}` : '/applications',
			leaseActionLabel: 'Create lease'
		};
	}

	if (forcedNoticeLabel) {
		return {
			...getTenantNoticeEmptyCopy(forcedNoticeLabel),
			showForceControls: false
		};
	}

	return {
		...getTenantNoticeEmptyCopy(),
		showForceControls: true
	};
}
