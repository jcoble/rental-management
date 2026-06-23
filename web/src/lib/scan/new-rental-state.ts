export interface NewRentalUnitProposal {
	action?: string | null;
	existingId?: number | null;
}

export interface NewRentalUnitOption {
	id: number | string;
	unitNumber?: string | null;
}

export interface NewRentalPropertyForm {
	name: string;
	type: string;
	status: string;
	addressLine1: string;
	addressLine2: string;
	city: string;
	state: string;
	postalCode: string;
	ownerEntityId: string;
}

export function parseNewRentalDraftId(searchParams: Pick<URLSearchParams, 'get'>): number | null {
	const raw = searchParams.get('draftId')?.trim();
	if (!raw) return null;

	const parsed = Number(raw);
	if (!Number.isInteger(parsed) || parsed <= 0) return null;

	return parsed;
}

export function newRentalDraftUrl(draftId: number): string {
	return `/scan/new-rental?draftId=${draftId}`;
}

export function createNewRentalPropertyForm(): NewRentalPropertyForm {
	return {
		name: '',
		type: 'SingleFamily',
		status: 'Active',
		addressLine1: '',
		addressLine2: '',
		city: '',
		state: '',
		postalCode: '',
		ownerEntityId: ''
	};
}

export function findNewRentalExistingUnitId(
	extractedUnitNumber: string | null | undefined,
	unitProposal: NewRentalUnitProposal | null | undefined,
	units: NewRentalUnitOption[] | null | undefined
): string {
	const loadedUnits = units ?? [];
	if (unitProposal?.action === 'link' && unitProposal.existingId != null) {
		const proposedId = String(unitProposal.existingId);
		if (loadedUnits.some((unit) => String(unit.id) === proposedId)) {
			return proposedId;
		}
	}

	const normalizedUnitNumber = (extractedUnitNumber ?? '').trim().toLowerCase();
	if (!normalizedUnitNumber) return '';

	const matches = loadedUnits.filter((unit) => {
		return (unit.unitNumber ?? '').trim().toLowerCase() === normalizedUnitNumber;
	});

	return matches.length === 1 ? String(matches[0].id) : '';
}

export function seedNewRentalLateFeeAmount(extractedLateFee: string | null | undefined): string {
	const value = (extractedLateFee ?? '').trim();
	return value || '0';
}

export function formatNewRentalStepLabel(step: number, stepLabels: readonly string[]): string {
	const totalSteps = stepLabels.length + 1;
	if (step < stepLabels.length) {
		return `Step ${step + 1} of ${totalSteps} · ${stepLabels[step]}`;
	}

	return `Step ${totalSteps} of ${totalSteps} · Review & confirm`;
}

export function formatNewRentalStepPosition(step: number, stepLabels: readonly string[]): string {
	const totalSteps = stepLabels.length + 1;
	const currentStep = Math.min(Math.max(step + 1, 1), totalSteps);
	return `${currentStep}/${totalSteps}`;
}
