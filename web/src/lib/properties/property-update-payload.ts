export function propertyUpdateFields<
	T extends { rentalStructure?: unknown; ownerEntityId?: unknown }
>(
	property: T
): Omit<T, 'rentalStructure' | 'ownerEntityId'> {
	const {
		rentalStructure: _structuralField,
		ownerEntityId: _ownerSelectionField,
		...mutableFields
	} = property;
	return mutableFields;
}
