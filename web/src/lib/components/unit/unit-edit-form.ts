import type { Unit } from '$lib/types';

export interface UnitEditForm {
	unitNumber: string;
	bedrooms: string;
	bathrooms: string;
	marketRent: string;
}

export function createUnitEditForm(unit: Unit): UnitEditForm {
	return {
		unitNumber: unit.unitNumber,
		bedrooms: String(unit.bedrooms),
		bathrooms: String(unit.bathrooms),
		marketRent: String(unit.marketRent),
	};
}
