export type TenantsEmptyStateCopy = {
	message: string;
	description: string;
	actionLabel: string;
};

export function getTenantsEmptyStateCopy({
	hasActiveFilters,
}: {
	hasActiveFilters: boolean;
}): TenantsEmptyStateCopy {
	if (hasActiveFilters) {
		return {
			message: 'No tenants match your search',
			description: 'Try adjusting the search, or add a tenant that matches this view.',
			actionLabel: 'Add tenant',
		};
	}

	return {
		message: 'No tenants yet',
		description: 'Tenants are the people who rent from you. Add your first to start tracking leases and rent.',
		actionLabel: 'Add your first tenant',
	};
}
