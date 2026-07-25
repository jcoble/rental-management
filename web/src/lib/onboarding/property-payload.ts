import { ownerEntityIdForOnboarding } from './owner-selection.ts';
import type { RentalStructure } from '../types/index.ts';

type OwnerLike = { id: number | string };
type PropertyLike = {
	id?: number | string;
	name?: string | null;
	type?: string | null;
	rentalStructure?: RentalStructure | null;
	ownerships?: readonly { ownerEntityId: number | string }[] | null;
	addressLine1?: string | null;
	addressLine2?: string | null;
	city?: string | null;
	state?: string | null;
	postalCode?: string | null;
};

export const NEW_ONBOARDING_PROPERTY_VALUE = '__new_property__';

export type OnboardingPropertyForm = {
	name: string;
	type: string;
	rentalStructure: RentalStructure | '';
	addressLine1: string;
	addressLine2: string;
	city: string;
	state: string;
	postalCode: string;
	ownerEntityId: string;
	status?: string | null;
};

export function buildOnboardingPropertyPayload(input: {
	propertyForm: OnboardingPropertyForm;
	selectedOwnerId?: string | null;
	createdOwner: OwnerLike | null | undefined;
	existingOwners: readonly OwnerLike[] | null | undefined;
}): Omit<OnboardingPropertyForm, 'ownerEntityId'> & {
	status: string;
	ownerships: Array<{ ownerEntityId: number; ownershipSharePercent: number }>;
	clearOwnership: boolean;
} {
	const { propertyForm, selectedOwnerId, createdOwner, existingOwners } = input;
	const selectedOwnerEntityId = propertyForm.ownerEntityId?.trim();
	const fallbackOwnerEntityId =
		propertyForm.ownerEntityId == null
			? ownerEntityIdForOnboarding({ selectedOwnerId, createdOwner, existingOwners })
			: '';
	const ownerEntityId = selectedOwnerEntityId
		? Number(selectedOwnerEntityId)
		: fallbackOwnerEntityId
			? Number(fallbackOwnerEntityId)
			: NaN;
	const hasOwnerEntity = Number.isFinite(ownerEntityId) && ownerEntityId > 0;

	return {
		name: propertyForm.name,
		type: propertyForm.type,
		rentalStructure: propertyForm.rentalStructure,
		addressLine1: propertyForm.addressLine1,
		addressLine2: propertyForm.addressLine2,
		city: propertyForm.city,
		state: propertyForm.state,
		postalCode: propertyForm.postalCode,
		status: propertyForm.status?.trim() || 'Active',
		ownerships: hasOwnerEntity
			? [{ ownerEntityId, ownershipSharePercent: 100 }]
			: [],
		clearOwnership: !hasOwnerEntity
	};
}

export function onboardingPropertyRecordOptions({
	createdProperty,
	existingProperties
}: {
	createdProperty: PropertyLike | null | undefined;
	existingProperties: readonly PropertyLike[] | null | undefined;
}): PropertyLike[] {
	const options = [...(existingProperties ?? [])];
	if (
		createdProperty &&
		createdProperty.id != null &&
		!options.some((property) => String(property.id) === String(createdProperty.id))
	) {
		options.push(createdProperty);
	}
	return options;
}

export function onboardingPropertyFormFromProperty(property: PropertyLike): OnboardingPropertyForm {
	return {
		name: property.name ?? '',
		type: property.type ?? 'SingleFamily',
		rentalStructure: property.rentalStructure ?? '',
		addressLine1: property.addressLine1 ?? '',
		addressLine2: property.addressLine2 ?? '',
		city: property.city ?? '',
		state: property.state ?? '',
		postalCode: property.postalCode ?? '',
		ownerEntityId:
			property.ownerships?.length === 1
				? String(property.ownerships[0].ownerEntityId)
				: '',
	};
}
