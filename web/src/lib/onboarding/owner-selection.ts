type OwnerLike = { id: number | string };

export function ownerEntityIdForOnboarding({
	createdOwner,
	existingOwners
}: {
	createdOwner: OwnerLike | null | undefined;
	existingOwners: readonly OwnerLike[] | null | undefined;
}): string {
	const owner = createdOwner ?? existingOwners?.[0] ?? null;
	return owner ? String(owner.id) : '';
}
