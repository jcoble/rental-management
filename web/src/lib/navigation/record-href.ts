export type RecordType = 'leaseManagement' | 'workOrder' | 'expense' | 'payment' | 'application';
export type RecordRef = { id: number; unitId?: number | null; tenantAccountId?: number | null };

const UNIT_TAB: Record<RecordType, { tab: string; param: string; ledger?: 'rent' | 'expenses' }> = {
	leaseManagement: { tab: 'lease', param: 'leaseManagement' },
	workOrder: { tab: 'maintenance', param: 'wo' },
	expense: { tab: 'ledger', ledger: 'expenses', param: 'expense' },
	payment: { tab: 'ledger', ledger: 'rent', param: 'payment' },
	application: { tab: 'applications', param: 'app' }
};

const GENERIC: Record<RecordType, (id: number) => string> = {
	leaseManagement: (id) => `/leases/${id}`,
	workOrder: (id) => `/maintenance/${id}`,
	expense: (id) => `/accounting/expenses/${id}`,
	payment: () => '/accounting',
	application: (id) => `/applications/${id}`
};

/** Unit-tied record → its unit Command Center tab; otherwise the generic detail page. */
export function recordHref(type: RecordType, rec: RecordRef): string {
	if (type === 'payment' && rec.tenantAccountId && rec.tenantAccountId > 0) {
		return `/tenant-accounts/${rec.tenantAccountId}/entries/${rec.id}`;
	}
	if (rec.unitId && rec.unitId > 0) {
		const { tab, param, ledger } = UNIT_TAB[type];
		const ledgerParam = ledger ? `&ledger=${ledger}` : '';
		return `/units/${rec.unitId}?tab=${tab}${ledgerParam}&${param}=${rec.id}`;
	}
	return GENERIC[type](rec.id);
}
