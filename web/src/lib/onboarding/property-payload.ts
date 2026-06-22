import { ownerEntityIdForOnboarding } from './owner-selection.ts';

type OwnerLike = { id: number | string };

export type OnboardingPropertyForm = {
	name: string;
	type: string;
	addressLine1: string;
	addressLine2?: string | null;
	city: string;
	state: string;
	postalCode: string;
	status?: string | null;
};

export function buildOnboardingPropertyPayload({
	propertyForm,
	createdOwner,
	existingOwners
}: {
	propertyForm: OnboardingPropertyForm;
	createdOwner: OwnerLike | null | undefined;
	existingOwners: readonly OwnerLike[] | null | undefined;
}): OnboardingPropertyForm & { status: string; ownerEntityId: string } {
	return {
		...propertyForm,
		status: propertyForm.status?.trim() || 'Active',
		ownerEntityId: ownerEntityIdForOnboarding({ createdOwner, existingOwners })
	};
}
