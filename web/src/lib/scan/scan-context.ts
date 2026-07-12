export const SCAN_DOC_TYPES = ['Expense', 'Payment', 'WorkOrder', 'Lease', 'Application', 'Loan'] as const;
export type ScanDocType = (typeof SCAN_DOC_TYPES)[number];

export interface ScanContext {
	type?: ScanDocType;
	propertyId?: number;
	unitId?: number;
	leaseId?: number;
	workOrderId?: number;
	returnTo?: string;
}

const DOC_TYPE_SET = new Set<string>(SCAN_DOC_TYPES);

function parsePositiveInt(value: string | null): number | undefined {
	if (!value) return undefined;
	const n = Number(value);
	return Number.isInteger(n) && n > 0 ? n : undefined;
}

function safeReturnTo(value: string | null | undefined): string | undefined {
	const trimmed = value?.trim();
	if (!trimmed?.startsWith('/')) return undefined;
	if (trimmed.startsWith('//')) return undefined;
	return trimmed;
}

export function parseScanContext(searchParams: URLSearchParams): ScanContext {
	const typeParam = searchParams.get('type');
	const type = typeParam && DOC_TYPE_SET.has(typeParam) ? (typeParam as ScanDocType) : undefined;
	const propertyId = parsePositiveInt(searchParams.get('propertyId'));
	const unitId = parsePositiveInt(searchParams.get('unitId'));
	const leaseId = parsePositiveInt(searchParams.get('leaseId'));
	const workOrderId = parsePositiveInt(searchParams.get('workOrderId'));
	const returnTo = safeReturnTo(searchParams.get('returnTo'));
	return {
		...(type ? { type } : {}),
		...(propertyId ? { propertyId } : {}),
		...(unitId ? { unitId } : {}),
		...(leaseId ? { leaseId } : {}),
		...(workOrderId ? { workOrderId } : {}),
		...(returnTo ? { returnTo } : {})
	};
}

export function scanHref(context: ScanContext = {}): string {
	return appendScanContext('/scan', context);
}

export function appendScanContext(href: string, context: ScanContext = {}): string {
	const url = new URL(href, 'https://rentalcommand.local');
	url.searchParams.delete('type');
	url.searchParams.delete('propertyId');
	url.searchParams.delete('unitId');
	url.searchParams.delete('leaseId');
	url.searchParams.delete('workOrderId');
	url.searchParams.delete('returnTo');

	if (context.type) url.searchParams.set('type', context.type);
	if (context.propertyId) url.searchParams.set('propertyId', String(context.propertyId));
	if (context.unitId) url.searchParams.set('unitId', String(context.unitId));
	if (context.leaseId) url.searchParams.set('leaseId', String(context.leaseId));
	if (context.workOrderId) url.searchParams.set('workOrderId', String(context.workOrderId));
	const returnTo = safeReturnTo(context.returnTo);
	if (returnTo) url.searchParams.set('returnTo', returnTo);

	const query = url.searchParams.toString();
	return `${url.pathname}${query ? `?${query}` : ''}${url.hash}`;
}

function setIfMissing(overrides: Record<string, unknown>, key: string, value: number | undefined): void {
	if (!value) return;
	const current = overrides[key];
	if (current !== undefined && current !== null && current !== '') return;
	overrides[key] = value;
}

export function applyScanContextOverrides(
	overrides: Record<string, unknown>,
	context: ScanContext,
	targetEntityType: ScanDocType
): void {
	if (targetEntityType === 'Expense') {
		setIfMissing(overrides, 'propertyId', context.propertyId);
		setIfMissing(overrides, 'unitId', context.unitId);
		setIfMissing(overrides, 'workOrderId', context.workOrderId);
		return;
	}

	if (targetEntityType === 'WorkOrder') {
		setIfMissing(overrides, 'propertyId', context.propertyId);
		setIfMissing(overrides, 'unitId', context.unitId);
		return;
	}

	if (targetEntityType === 'Payment') {
		// Payment confirmation selects a canonical tenant account in the review UI.
		// A historical agreement id is not an accounting target.
		return;
	}

	if (targetEntityType === 'Application') {
		setIfMissing(overrides, 'propertyId', context.propertyId);
		setIfMissing(overrides, 'unitId', context.unitId);
		return;
	}

	if (targetEntityType === 'Loan') {
		// The property the loan attaches to comes from the deep-link the landlord launched the scan from.
		setIfMissing(overrides, 'propertyId', context.propertyId);
	}
}
