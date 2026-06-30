type OwnerEntityType = 'Person' | 'LLC' | 'Trust';

type OwnerLike = {
	id: number | string;
	name?: string | null;
	ownerEntityType?: OwnerEntityType | string | null;
	email?: string | null;
	taxId?: string | null;
};

export const NEW_ONBOARDING_OWNER_VALUE = '__new_owner__';

export type OnboardingOwnerForm = {
	name: string;
	ownerEntityType: OwnerEntityType;
	email: string;
	taxId: string;
};

export function ownerEntityIdForOnboarding({
	selectedOwnerId,
	createdOwner,
	existingOwners
}: {
	selectedOwnerId?: string | null;
	createdOwner: OwnerLike | null | undefined;
	existingOwners: readonly OwnerLike[] | null | undefined;
}): string {
	if (selectedOwnerId && selectedOwnerId !== NEW_ONBOARDING_OWNER_VALUE) {
		return selectedOwnerId;
	}

	const owner = createdOwner ?? existingOwners?.[0] ?? null;
	return owner ? String(owner.id) : '';
}

export function onboardingOwnerFormFromOwner(owner: OwnerLike): OnboardingOwnerForm {
	const ownerEntityType = owner.ownerEntityType;

	return {
		name: owner.name ?? '',
		ownerEntityType:
			ownerEntityType === 'LLC' || ownerEntityType === 'Trust' || ownerEntityType === 'Person'
				? ownerEntityType
				: 'Person',
		email: owner.email ?? '',
		taxId: owner.taxId ?? '',
	};
}
