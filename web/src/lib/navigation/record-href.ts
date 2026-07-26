export type RecordType = 'leaseManagement' | 'workOrder' | 'expense' | 'payment' | 'application';
export type RecordRef = {
	id: number;
	unitId?: number | null;
	tenantAccountId?: number | null;
};

const UNIT_TAB: Record<RecordType, { tab: string; param: string; view?: string }> = {
	leaseManagement: {
		tab: 'tenant-lease',
		view: 'agreements',
		param: 'leaseManagement',
	},
	workOrder: { tab: 'maintenance', view: 'work-orders', param: 'wo' },
	expense: { tab: 'money', view: 'operating-costs', param: 'expense' },
	payment: { tab: 'money', view: 'tenant-account', param: 'payment' },
	application: { tab: 'leasing', view: 'applications', param: 'app' },
};

const GENERIC: Record<RecordType, (id: number) => string> = {
	leaseManagement: (id) => `/leases/${id}`,
	workOrder: (id) => `/maintenance/${id}`,
	expense: (id) => `/accounting/expenses/${id}`,
	payment: () => '/accounting',
	application: (id) => `/applications/${id}`,
};

/** Unit-tied record → its unit Command Center tab; otherwise the generic detail page. */
export function recordHref(type: RecordType, rec: RecordRef): string {
	if (rec.unitId && rec.unitId > 0) {
		const { tab, param, view } = UNIT_TAB[type];
		const viewParam = view ? `&view=${view}` : '';
		return `/units/${rec.unitId}?tab=${tab}${viewParam}&${param}=${rec.id}`;
	}
	if (type === 'payment' && rec.tenantAccountId && rec.tenantAccountId > 0) {
		return `/tenant-accounts/${rec.tenantAccountId}/entries/${rec.id}`;
	}
	return GENERIC[type](rec.id);
}
