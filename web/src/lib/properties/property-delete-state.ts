export type PropertyDeleteInput = {
	name?: string | null;
	type?: string | null;
	unitCount?: number | null;
};

export type PropertyDeleteState = {
	message: string;
	confirmDisabled: boolean;
};

function propertyName(property: PropertyDeleteInput) {
	return property.name?.trim() || 'This property';
}

function isPropertyUnitType(type: string | null | undefined) {
	return type === 'SingleFamily' || type === 'Condo' || type === 'Townhome';
}

export function getPropertyDeleteState(property: PropertyDeleteInput): PropertyDeleteState {
	const name = propertyName(property);
	const unitCount = property.unitCount ?? 0;

	if (unitCount > 0) {
		if (unitCount === 1 && isPropertyUnitType(property.type)) {
			return {
				message: `Delete "${name}"? This will also remove its empty rental space. If it has any history, the server will stop the delete and explain why.`,
				confirmDisabled: false,
			};
		}

		// Multi-unit properties can have real child units. The server blocks the delete while live units
		// remain, so disable the confirm and tell the landlord to remove the units first.
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
