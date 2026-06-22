import type { PropertyStatus, PropertyType } from '$lib/types';

export const PROPERTY_TYPE_OPTIONS: { value: PropertyType; label: string }[] = [
	{ value: 'SingleFamily', label: 'Single-family' },
	{ value: 'MultiFamily', label: 'Multi-family' },
	{ value: 'Condo', label: 'Condo' },
	{ value: 'Townhome', label: 'Townhome' },
	{ value: 'Commercial', label: 'Commercial' },
	{ value: 'MixedUse', label: 'Mixed-use' },
];

export const PROPERTY_STATUS_OPTIONS: { value: PropertyStatus; label: string }[] = [
	{ value: 'Active', label: 'Active' },
	{ value: 'UnderMaintenance', label: 'Under maintenance' },
	{ value: 'Inactive', label: 'Inactive' },
];

const typeLabels = new Map<string, string>(PROPERTY_TYPE_OPTIONS.map((o) => [o.value, o.label]));
const statusLabels = new Map<string, string>(PROPERTY_STATUS_OPTIONS.map((o) => [o.value, o.label]));

function fallbackLabel(value: string | undefined | null): string {
	if (!value) return '';
	return value
		.replace(/[_-]+/g, ' ')
		.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
		.replace(/\s+/g, ' ')
		.trim()
		.replace(/\b\w/g, (ch) => ch.toUpperCase());
}

export function formatPropertyType(value: string | undefined | null): string {
	return typeLabels.get(value ?? '') ?? fallbackLabel(value);
}

export function formatPropertyStatus(value: string | undefined | null): string {
	return statusLabels.get(value ?? '') ?? fallbackLabel(value);
}

export const propertyTypeOptions = PROPERTY_TYPE_OPTIONS;
export const propertyStatusOptions = PROPERTY_STATUS_OPTIONS;
