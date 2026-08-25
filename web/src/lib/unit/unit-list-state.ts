export type UnitsEmptyStateCopy = {
	message: string;
	description: string;
};

export function getUnitsEmptyStateCopy({
	hasActiveFilters,
}: {
	hasActiveFilters: boolean;
}): UnitsEmptyStateCopy {
	if (hasActiveFilters) {
		return {
			message: 'No units match your filters',
			description: 'Try adjusting search or filters, or add a unit that matches this view.',
		};
	}

	return {
		message: 'No units yet',
		description: 'Units live under a property. Add a property and its units to start managing them here.',
	};
}
