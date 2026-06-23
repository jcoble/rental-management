import { formatStatusLabel } from '../utils/status-labels.ts';

export const LEASE_STATUSES = ['Draft', 'Active', 'NoticeGiven', 'Expired', 'Terminated'] as const;

export type LeasesEmptyStateCopy = {
	message: string;
	description: string;
	actionLabel: string;
};

export function getLeasesEmptyStateCopy({
	hasActiveFilters,
}: {
	hasActiveFilters: boolean;
}): LeasesEmptyStateCopy {
	if (hasActiveFilters) {
		return {
			message: 'No leases match your filters',
			description: 'Try adjusting search or filters, or add a lease that matches this view.',
			actionLabel: 'Add lease',
		};
	}

	return {
		message: 'No leases yet',
		description: 'A lease ties a tenant to a unit and sets the rent and dates. Add your first to start tracking rent.',
		actionLabel: 'Add your first lease',
	};
}

export function getLeaseStatusOptions(): Array<{ value: string; label: string }> {
	return LEASE_STATUSES.map((value) => ({ value, label: formatStatusLabel(value) }));
}
