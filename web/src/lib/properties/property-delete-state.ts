export type PropertyDeleteInput = {
	name?: string | null;
	unitCount?: number | null;
};

export type PropertyDeleteState = {
	message: string;
	confirmDisabled: boolean;
};

function propertyName(property: PropertyDeleteInput) {
	return property.name?.trim() || 'This property';
}

export function getPropertyDeleteState(property: PropertyDeleteInput): PropertyDeleteState {
	const name = propertyName(property);
	const unitCount = property.unitCount ?? 0;

	// A property delete only soft-deletes the property row; its units would be orphaned (live but
	// invisible). The server blocks the delete while live units remain, so disable the confirm and tell
	// the landlord to remove the units first instead of falsely promising the units are removed too.
	if (unitCount > 0) {
		const unitNoun = unitCount === 1 ? 'unit' : 'units';
		return {
			message: `${name} still has ${unitCount} ${unitNoun}. Remove the ${unitNoun} before deleting this property.`,
			confirmDisabled: true,
		};
	}

	return {
		message: `Delete "${name}"? This cannot be undone.`,
		confirmDisabled: false,
	};
}
