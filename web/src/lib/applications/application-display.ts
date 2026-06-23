export interface ApplicationAddressParts {
	currentAddress?: string | null;
	currentAddressLine1?: string | null;
	currentAddressLine2?: string | null;
	currentCity?: string | null;
	currentState?: string | null;
	currentPostalCode?: string | null;
}

export interface ApplicationHomeParts {
	propertyId?: number | null;
	unitId?: number | null;
	propertyName?: string | null;
	unitNumber?: string | null;
}

export function formatApplicationAddress(parts: ApplicationAddressParts): string {
	const structured = composeDeduplicated([
		parts.currentAddressLine1,
		parts.currentAddressLine2,
		parts.currentCity,
		joinNonEmpty([parts.currentState, parts.currentPostalCode], ' '),
	]);

	return structured || clean(parts.currentAddress) || '—';
}

export function formatRequestedProperty(parts: ApplicationHomeParts): string {
	return clean(parts.propertyName) || (parts.propertyId != null ? `#${parts.propertyId}` : 'No preference');
}

export function formatRequestedUnit(parts: ApplicationHomeParts): string {
	return clean(parts.unitNumber) || (parts.unitId != null ? `#${parts.unitId}` : 'No preference');
}

export function formatApplicationsEmptyMessage(
	search: string | null | undefined,
	statusValue: string | null | undefined,
	allStatusesValue = 'all',
): string {
	const hasSearch = Boolean(search?.trim());
	const hasStatusFilter = Boolean(statusValue && statusValue !== allStatusesValue);

	return hasSearch || hasStatusFilter
		? 'No applications match your filters.'
		: 'No applications yet. Share your application link to get started.';
}

export function canRunApplicationScreening(
	status: string | null | undefined,
	consentGiven: boolean | null | undefined,
): boolean {
	return consentGiven === true && (status === 'Submitted' || status === 'UnderReview');
}

function composeDeduplicated(values: Array<string | null | undefined>): string | null {
	const parts: string[] = [];
	for (const value of values) {
		const part = clean(value);
		if (!part || isCoveredByExistingPart(parts, part)) continue;
		parts.push(part);
	}

	return parts.length > 0 ? parts.join(', ') : null;
}

function isCoveredByExistingPart(existingParts: string[], candidate: string): boolean {
	const normalizedCandidate = normalizeForContainment(candidate);
	if (!normalizedCandidate) return true;

	return existingParts.some((existing) => {
		const normalizedExisting = normalizeForContainment(existing);
		return containsTokenSequence(normalizedExisting, normalizedCandidate) ||
			containsTokenSequence(normalizedCandidate, normalizedExisting);
	});
}

function containsTokenSequence(value: string, candidate: string): boolean {
	return Boolean(value && candidate && ` ${value} `.includes(` ${candidate} `));
}

function normalizeForContainment(value: string): string {
	return value
		.toLowerCase()
		.replace(/[^a-z0-9]+/g, ' ')
		.trim()
		.replace(/\s+/g, ' ');
}

function joinNonEmpty(values: Array<string | null | undefined>, separator: string): string | null {
	const parts = values.map(clean).filter((value): value is string => Boolean(value));
	return parts.length > 0 ? parts.join(separator) : null;
}

function clean(value: string | null | undefined): string | null {
	const trimmed = value?.trim();
	return trimmed ? trimmed : null;
}
