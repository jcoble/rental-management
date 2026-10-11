export interface RentalListingLinkContext {
	propertyId: number;
	unitId: number;
}

export interface ApplyHomeSelection {
	propertyValue: string;
	unitValue: string;
}

export interface ApplicationLinkProperty {
	id: number;
	units?: { id: number }[];
}

function readPositiveInt(params: URLSearchParams, key: string): number | null {
	const raw = params.get(key);
	if (!raw) return null;
	const value = Number(raw);
	return Number.isInteger(value) && value > 0 ? value : null;
}

export function readRentalListingLinkContext(params: URLSearchParams): RentalListingLinkContext | null {
	if (params.get('action') !== 'list-unit') return null;
	const propertyId = readPositiveInt(params, 'propertyId');
	const unitId = readPositiveInt(params, 'unitId');
	if (!propertyId || !unitId) return null;
	return { propertyId, unitId };
}

export function buildApplicationLinkUrl(
	origin: string,
	applyPath: string,
	context: RentalListingLinkContext | null
): string {
	const fallbackOrigin = 'http://localhost';
	const url = new URL(applyPath, origin || fallbackOrigin);
	if (context) {
		url.searchParams.set('propertyId', String(context.propertyId));
		url.searchParams.set('unitId', String(context.unitId));
	}
	const path = `${url.pathname}${url.search}${url.hash}`;
	return origin ? `${url.origin}${path}` : path;
}

export function resolveApplyHomeSelection(
	params: URLSearchParams,
	properties: ApplicationLinkProperty[],
	noPreferenceValue: string
): ApplyHomeSelection | null {
	const propertyId = readPositiveInt(params, 'propertyId');
	if (!propertyId) return null;
	const property = properties.find((p) => p.id === propertyId);
	if (!property) return null;

	const unitId = readPositiveInt(params, 'unitId');
	const hasUnit = unitId != null && (property.units ?? []).some((u) => u.id === unitId);
	return {
		propertyValue: String(propertyId),
		unitValue: hasUnit ? String(unitId) : noPreferenceValue,
	};
}
