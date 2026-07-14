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

export function resolveUnitDestination(
	tabValue: string | undefined | null,
	viewValue?: string | undefined | null,
): UnitDestination {
	const normalizedTab = tabValue?.trim().toLowerCase();
	const normalizedView = viewValue?.trim().toLowerCase() as UnitView | undefined;
	const tab = UNIT_TABS.includes(normalizedTab as UnitTab) ? (normalizedTab as UnitTab) : 'summary';
	const validViews = VALID_VIEWS[tab];
	const requestedView = normalizedView && validViews?.includes(normalizedView) ? normalizedView : undefined;
	const view = requestedView && validViews?.includes(requestedView) ? requestedView : DEFAULT_VIEWS[tab];
	return view ? { tab, view } : { tab };
}

export function resolveUnitTab(value: string | undefined | null): UnitTab {
	return resolveUnitDestination(value).tab;
}
