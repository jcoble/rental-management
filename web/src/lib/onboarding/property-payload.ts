import { ownerEntityIdForOnboarding } from './owner-selection.ts';

type OwnerLike = { id: number | string };
type PropertyLike = {
	id?: number | string;
	name?: string | null;
	type?: string | null;
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
	addressLine1: string;
	addressLine2: string;
	city: string;
	state: string;
	postalCode: string;
	status?: string | null;
};

export function buildOnboardingPropertyPayload({
	propertyForm,
	selectedOwnerId,
	createdOwner,
	existingOwners
}: {
	propertyForm: OnboardingPropertyForm;
	selectedOwnerId?: string | null;
	createdOwner: OwnerLike | null | undefined;
	existingOwners: readonly OwnerLike[] | null | undefined;
}): OnboardingPropertyForm & { status: string; ownerEntityId: string } {
	return {
		...propertyForm,
		status: propertyForm.status?.trim() || 'Active',
		ownerEntityId: ownerEntityIdForOnboarding({ selectedOwnerId, createdOwner, existingOwners })
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
		addressLine1: property.addressLine1 ?? '',
		addressLine2: property.addressLine2 ?? '',
		city: property.city ?? '',
		state: property.state ?? '',
		postalCode: property.postalCode ?? '',
	};
}
