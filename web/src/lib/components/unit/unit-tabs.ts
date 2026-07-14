export const UNIT_TABS = ['summary', 'leasing', 'tenant-lease', 'money', 'maintenance', 'documents-history'] as const;

export type UnitTab = (typeof UNIT_TABS)[number];

export type UnitView =
	| 'listing'
	| 'applications'
	| 'agreements'
	| 'residents'
	| 'work-orders'
	| 'turnover'
	| 'documents'
	| 'history';

export interface UnitDestination {
	tab: UnitTab;
	view?: UnitView;
}

const DEFAULT_VIEWS: Partial<Record<UnitTab, UnitView>> = {
	leasing: 'listing',
	'tenant-lease': 'agreements',
	maintenance: 'work-orders',
	'documents-history': 'documents',
};

const VALID_VIEWS: Partial<Record<UnitTab, readonly UnitView[]>> = {
	leasing: ['listing', 'applications'],
	'tenant-lease': ['agreements', 'residents'],
	maintenance: ['work-orders', 'turnover'],
	'documents-history': ['documents', 'history'],
};

const LEGACY_DESTINATIONS: Record<string, UnitDestination> = {
	overview: { tab: 'summary' },
	listing: { tab: 'leasing', view: 'listing' },
	application: { tab: 'leasing', view: 'applications' },
	applications: { tab: 'leasing', view: 'applications' },
	lease: { tab: 'tenant-lease', view: 'agreements' },
	leases: { tab: 'tenant-lease', view: 'agreements' },
	tenant: { tab: 'tenant-lease', view: 'residents' },
	tenants: { tab: 'tenant-lease', view: 'residents' },
	ledger: { tab: 'money' },
	rent: { tab: 'money' },
	payments: { tab: 'money' },
	expenses: { tab: 'money' },
	work: { tab: 'maintenance', view: 'work-orders' },
	'work-orders': { tab: 'maintenance', view: 'work-orders' },
	turnover: { tab: 'maintenance', view: 'turnover' },
	'make-ready': { tab: 'maintenance', view: 'turnover' },
	makeready: { tab: 'maintenance', view: 'turnover' },
	'move-out': { tab: 'maintenance', view: 'turnover' },
	moveout: { tab: 'maintenance', view: 'turnover' },
	documents: { tab: 'documents-history', view: 'documents' },
	timeline: { tab: 'documents-history', view: 'history' },
	history: { tab: 'documents-history', view: 'history' },
};

export function resolveUnitDestination(
	tabValue: string | undefined | null,
	viewValue?: string | undefined | null,
): UnitDestination {
	const normalizedTab = tabValue?.trim().toLowerCase();
	const normalizedView = viewValue?.trim().toLowerCase() as UnitView | undefined;
	const legacy = normalizedTab ? LEGACY_DESTINATIONS[normalizedTab] : undefined;
	const tab = legacy?.tab ?? (UNIT_TABS.includes(normalizedTab as UnitTab) ? (normalizedTab as UnitTab) : 'summary');
	const validViews = VALID_VIEWS[tab];
	const requestedView = normalizedView && validViews?.includes(normalizedView) ? normalizedView : legacy?.view;
	const view = requestedView && validViews?.includes(requestedView) ? requestedView : DEFAULT_VIEWS[tab];
	return view ? { tab, view } : { tab };
}

export function resolveUnitTab(value: string | undefined | null): UnitTab {
	return resolveUnitDestination(value).tab;
}
