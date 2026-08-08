export const UNIT_TABS = ['summary', 'leasing', 'tenant-lease', 'money', 'maintenance', 'documents-history'] as const;

export const UNIT_SUBNAV_LIST_CLASS =
	'inline-flex min-w-max items-center gap-0.5 rounded-lg bg-muted/55 p-1';

export const UNIT_SUBNAV_ITEM_CLASS =
	'min-h-9 rounded-md px-3 py-1.5 text-sm font-medium text-muted-foreground transition-[background-color,color,box-shadow,transform] duration-150 hover:bg-background/70 hover:text-foreground active:translate-y-px focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50 aria-[current=page]:bg-background aria-[current=page]:text-foreground aria-[current=page]:shadow-sm';

export type UnitTab = (typeof UNIT_TABS)[number];

export type UnitView =
	| 'listing'
	| 'applications'
	| 'agreements'
	| 'residents'
	| 'tenant-account'
	| 'operating-costs'
	| 'work-orders'
	| 'inspections'
	| 'recurring'
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
	money: 'tenant-account',
	maintenance: 'work-orders',
	'documents-history': 'documents',
};

const VALID_VIEWS: Partial<Record<UnitTab, readonly UnitView[]>> = {
	leasing: ['listing', 'applications'],
	'tenant-lease': ['agreements', 'residents'],
	money: ['tenant-account', 'operating-costs'],
	maintenance: ['work-orders', 'inspections', 'recurring', 'turnover'],
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
