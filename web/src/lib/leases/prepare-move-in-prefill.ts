export type PrepareMoveInPrefill = {
	tenantId: string;
	applicationId: string;
	unitId: string;
};

function positiveIntegerString(value: string | null): string {
	if (!value) return '';
	const trimmed = value.trim();
	return /^[1-9]\d*$/.test(trimmed) ? trimmed : '';
}

export function prepareMoveInHrefForApprovedTenant(
	tenantId: number | string,
	applicationId: number | string = '',
	unitId: number | string = ''
): string {
	const params = new URLSearchParams({ prepareMoveIn: '1' });
	const normalizedTenantId = positiveIntegerString(String(tenantId));
	if (normalizedTenantId) params.set('tenantId', normalizedTenantId);
	const normalizedApplicationId = positiveIntegerString(String(applicationId));
	if (normalizedApplicationId) params.set('applicationId', normalizedApplicationId);
	const normalizedUnitId = positiveIntegerString(String(unitId));
	if (normalizedUnitId) params.set('unitId', normalizedUnitId);
	return `/applications?${params.toString()}`;
}

export function readPrepareMoveInPrefill(params: URLSearchParams): PrepareMoveInPrefill | null {
	if (params.get('prepareMoveIn') !== '1') return null;
	return {
		tenantId: positiveIntegerString(params.get('tenantId')),
		applicationId: positiveIntegerString(params.get('applicationId')),
		unitId: positiveIntegerString(params.get('unitId'))
	};
}
