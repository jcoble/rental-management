export function propertyUpdateFields<T extends { rentalStructure?: unknown }>(
	property: T
): Omit<T, 'rentalStructure'> {
	const { rentalStructure: _structuralField, ...mutableFields } = property;
	return mutableFields;
}
