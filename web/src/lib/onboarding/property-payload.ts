import { ownerEntityIdForOnboarding } from './owner-selection.ts';

type OwnerLike = { id: number | string };
type PropertyLike = {
	id?: number | string;
	name?: string | null;
	type?: string | null;
	ownerEntityId?: number | string | null;
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
	ownerEntityId: number | null;
	clearOwnerEntity: boolean;
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
		addressLine1: propertyForm.addressLine1,
		addressLine2: propertyForm.addressLine2,
		city: propertyForm.city,
		state: propertyForm.state,
		postalCode: propertyForm.postalCode,
		status: propertyForm.status?.trim() || 'Active',
		ownerEntityId: hasOwnerEntity ? ownerEntityId : null,
		clearOwnerEntity: !hasOwnerEntity
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
		ownerEntityId: property.ownerEntityId == null ? '' : String(property.ownerEntityId),
	};
}
