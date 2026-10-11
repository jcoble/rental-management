import type { Unit } from '$lib/types';

export interface UnitEditForm {
	unitNumber: string;
	floorPlan: string;
	bedrooms: string;
	bathrooms: string;
	squareFeet: string;
	marketRent: string;
	notes: string;
}

export function createUnitEditForm(unit: Unit): UnitEditForm {
	return {
		unitNumber: unit.unitNumber,
		floorPlan: unit.floorPlan ?? '',
		bedrooms: String(unit.bedrooms),
		bathrooms: String(unit.bathrooms),
		squareFeet: unit.squareFeet == null ? '' : String(unit.squareFeet),
		marketRent: String(unit.marketRent),
		notes: unit.notes ?? '',
	};
}
