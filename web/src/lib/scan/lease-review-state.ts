export function shouldSeedLeaseReviewState(status: string | null | undefined, fieldCount: number): boolean {
	return (status === 'Reviewing' || status === 'Confirmed') && fieldCount > 0;
}

export const LEASE_REVIEW_NEW_UNIT_DETAIL_FIELDS = [
	{ name: 'unit_bedrooms', label: 'Bedrooms', placeholder: '2' },
	{ name: 'unit_bathrooms', label: 'Bathrooms', placeholder: '1.5' },
	{ name: 'unit_square_feet', label: 'Square feet', placeholder: '950' }
] as const;

export interface LeaseUnitProposalSeed {
	action: string;
	existingId: number | null;
}

export function seedLeaseUnitId(
	extractedUnitId: string | null | undefined,
	unitProposal: LeaseUnitProposalSeed | null | undefined,
	isCreatingProperty: boolean
): string {
	if (isCreatingProperty) return '';

	const extracted = (extractedUnitId ?? '').trim();
	if (extracted) return extracted;

	if (unitProposal?.action === 'link' && unitProposal.existingId != null) {
		return String(unitProposal.existingId);
	}

	return '';
}
