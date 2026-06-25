export type LeaseCreatePrefill = {
	tenantId: string;
};

function positiveIntegerString(value: string | null): string {
	if (!value) return '';
	const trimmed = value.trim();
	return /^[1-9]\d*$/.test(trimmed) ? trimmed : '';
}

export function leaseCreateHrefForApprovedTenant(tenantId: number | string): string {
	const normalized = positiveIntegerString(String(tenantId));
	const params = new URLSearchParams({ create: '1' });
	if (normalized) params.set('tenantId', normalized);
	return `/leases?${params.toString()}`;
}

export function readLeaseCreatePrefill(params: URLSearchParams): LeaseCreatePrefill | null {
	if (params.get('create') !== '1') return null;
	return {
		tenantId: positiveIntegerString(params.get('tenantId')),
	};
}
