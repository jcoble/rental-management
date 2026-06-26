import type { UnitStatus } from '$lib/types';

export type ApplicationUnitOption = {
	unitNumber: string;
	status?: UnitStatus | string | null;
};

export function applicationUnitAvailabilityLabel(status?: UnitStatus | string | null): string {
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
