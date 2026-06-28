export type RecordType = 'lease' | 'workOrder' | 'expense' | 'payment' | 'application';
export type RecordRef = { id: number; unitId?: number | null };

const UNIT_TAB: Record<RecordType, { tab: string; param: string }> = {
	lease: { tab: 'lease', param: 'lease' },
	workOrder: { tab: 'maintenance', param: 'wo' },
	expense: { tab: 'expenses', param: 'expense' },
	payment: { tab: 'rent', param: 'payment' },
	application: { tab: 'applications', param: 'app' }
};

const GENERIC: Record<RecordType, (id: number) => string> = {
	lease: (id) => `/leases/${id}`,
	workOrder: (id) => `/maintenance/${id}`,
	expense: (id) => `/accounting/expenses/${id}`,
	payment: (id) => `/accounting/payments/${id}`,
	application: (id) => `/applications/${id}`
};

/** Unit-tied record → its unit Command Center tab; otherwise the generic detail page. */
export function recordHref(type: RecordType, rec: RecordRef): string {
	if (rec.unitId && rec.unitId > 0) {
		const { tab, param } = UNIT_TAB[type];
		return `/units/${rec.unitId}?tab=${tab}&${param}=${rec.id}`;
	}
	return GENERIC[type](rec.id);
}
