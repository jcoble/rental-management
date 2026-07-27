import type { PropertyStatus, PropertyType, RentalStructure } from '$lib/types';
import { propertyStatusOptions, propertyTypeOptions } from './property-labels.ts';

export type PropertyFormDraft = {
	name: string;
	type: PropertyType;
	rentalStructure: RentalStructure | '';
	status: PropertyStatus;
	addressLine1: string;
	addressLine2: string;
	city: string;
	state: string;
	postalCode: string;
	ownerEntityId: string;
	yearBuilt: string;
	managementFeePercent: string;
	notes: string;
	purchasePrice: string;
	landValue: string;
	inServiceDate: string;
	manualAnnualDepreciation: string;
};

export type PropertiesEmptyStateCopy = {
	message: string;
	description: string;
	actionLabel: string;
};

function isPropertyType(value: string | null | undefined): value is PropertyType {
	return propertyTypeOptions.some((option) => option.value === value);
}

function isPropertyStatus(value: string | null | undefined): value is PropertyStatus {
	return propertyStatusOptions.some((option) => option.value === value);
}

export function createEmptyPropertyDraft(filters: {
	typeFilter?: string | null;
	statusFilter?: string | null;
} = {}): PropertyFormDraft {
	return {
		name: '',
		type: isPropertyType(filters.typeFilter) ? filters.typeFilter : 'MultiFamily',
		rentalStructure: '',
		status: isPropertyStatus(filters.statusFilter) ? filters.statusFilter : 'Active',
		addressLine1: '',
		addressLine2: '',
		city: '',
		state: '',
		postalCode: '',
		ownerEntityId: '',
		yearBuilt: '',
		managementFeePercent: '',
		notes: '',
		purchasePrice: '',
		landValue: '',
		inServiceDate: '',
		manualAnnualDepreciation: '',
	};
}

export function getPropertiesEmptyStateCopy({
	hasActiveFilters,
}: {
	hasActiveFilters: boolean;
}): PropertiesEmptyStateCopy {
	if (hasActiveFilters) {
		return {
			message: 'No properties match your filters',
			description: 'Try adjusting search or filters, or add a property that matches this view.',
			actionLabel: 'Add property',
		};
	}

	return {
		message: 'No rentals yet',
		description: 'A property is one building or address. Add your first to get started.',
		actionLabel: 'Add your first property',
	};
}
