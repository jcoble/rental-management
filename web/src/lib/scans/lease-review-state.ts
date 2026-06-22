export function shouldSeedLeaseReviewState(status: string | null | undefined, fieldCount: number): boolean {
	return (status === 'Reviewing' || status === 'Confirmed') && fieldCount > 0;
}

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
