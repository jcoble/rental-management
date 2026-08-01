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
	const payload: Record<string, unknown> = { ...mutableFields };
	if (payload.addressLine2 === null) payload.addressLine2 = '';
	return payload as Omit<T, 'rentalStructure' | 'ownerEntityId'>;
}
