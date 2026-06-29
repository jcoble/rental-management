export type UnitScopedRecord = {
	unitId?: number | null;
};

export function isMismatchedUnitSelection(
	record: UnitScopedRecord | null | undefined,
	expectedUnitId: number | null | undefined
): boolean {
	if (!record || !expectedUnitId || expectedUnitId <= 0) return false;
	return record.unitId !== expectedUnitId;
}
