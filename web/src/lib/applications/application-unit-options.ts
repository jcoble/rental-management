import type { DerivedUnitStatus } from '$lib/types';

export type ApplicationUnitOption = {
	unitNumber: string;
	status?: DerivedUnitStatus | string | null;
};

export function applicationUnitAvailabilityLabel(status?: DerivedUnitStatus | string | null): string {
	switch (status) {
		case 'Vacant':
			return 'Available';
		case 'Reserved':
			return 'Reserved';
		case 'Occupied':
			return 'Currently occupied';
		case 'Offline':
			return 'Unavailable';
		default:
			return 'Availability unknown';
	}
}

export function applicationUnitOptionLabel(unit: ApplicationUnitOption): string {
	return `Unit ${unit.unitNumber} - ${applicationUnitAvailabilityLabel(unit.status)}`;
}
