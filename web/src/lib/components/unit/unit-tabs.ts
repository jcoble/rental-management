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

/**
 * Query-string context carried by Unit workflows. These names stay centralized
 * so URL resolution and the user-tab boundary apply the same ownership map.
 * Deep-link resolution preserves this context; a user leaving a primary tab
 * clears records owned by another tab while retaining the selected tab's own
 * record context.
 */
export const UNIT_CONTEXTUAL_PARAMS = [
	'view',
	'wo',
	'app',
	'payment',
	'expense',
	'tenantAccount',
	'leaseManagement',
	'agreement',
	'ledger',
	'action',
	'inspection',
] as const;

type UnitContextOwner = Pick<UnitDestination, 'tab' | 'view'>;

const CONTEXT_OWNER_BY_PARAM: Partial<Record<(typeof UNIT_CONTEXTUAL_PARAMS)[number], UnitContextOwner>> = {
	wo: { tab: 'maintenance', view: 'work-orders' },
	app: { tab: 'leasing', view: 'applications' },
	payment: { tab: 'money', view: 'tenant-account' },
	expense: { tab: 'money', view: 'operating-costs' },
	tenantAccount: { tab: 'money', view: 'tenant-account' },
	leaseManagement: { tab: 'tenant-lease', view: 'agreements' },
	agreement: { tab: 'tenant-lease', view: 'agreements' },
	ledger: { tab: 'documents-history', view: 'history' },
	action: { tab: 'tenant-lease', view: 'agreements' },
	inspection: { tab: 'maintenance', view: 'inspections' },
};

export interface UnitUrlDestination {
	destination: UnitDestination;
	hasExplicitViewOrRecord: boolean;
}

/**
 * Removes record context owned by another primary tab before a user leaves the
 * current surface. Nested-view memory is kept separately by the navigation
 * handler, so this intentionally drops only URL record ids.
 */
export function clearForeignUnitRecordParams(url: URL, destinationTab: UnitTab): void {
	for (const [param, owner] of Object.entries(CONTEXT_OWNER_BY_PARAM)) {
		if (owner?.tab !== destinationTab) url.searchParams.delete(param);
	}
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

function resolveUnitViewOwner(viewValue: string | null | undefined): UnitDestination | null {
	const normalizedView = viewValue?.trim().toLowerCase() as UnitView | undefined;
	if (!normalizedView) return null;

	for (const [tab, views] of Object.entries(VALID_VIEWS)) {
		if (views?.includes(normalizedView)) {
			return { tab: tab as UnitTab, view: normalizedView };
		}
	}

	return null;
}

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

/**
 * Resolves a Unit destination from URL context without consulting shallow
 * state. A view or record parameter is an explicit deep link and therefore
 * always outranks remembered tab state. Record parameters also supply the
 * canonical nested view when `view` is omitted.
 */
export function resolveUnitUrlDestination(url: URL): UnitUrlDestination {
	const tabValue = url.searchParams.get('tab');
	const explicitView = url.searchParams.get('view');
	// Deep links can retain a stale primary tab while carrying a valid nested view
	// (for example, a work order opened from the Money surface). The nested view's
	// owning tab is authoritative so URL and shallow state can converge.
	const urlDestination = resolveUnitViewOwner(explicitView) ?? resolveUnitDestination(tabValue, explicitView);
	if (url.searchParams.has('view')) {
		return { destination: urlDestination, hasExplicitViewOrRecord: true };
	}

	const explicitTab = tabValue?.trim().toLowerCase();
	const owner = Object.entries(CONTEXT_OWNER_BY_PARAM).find(([param, candidate]) => {
		if (param === 'view' || !candidate || !url.searchParams.has(param)) return false;
		if (!explicitTab) return true;
		return candidate.tab === urlDestination.tab;
	})?.[1];
	if (owner) {
		return {
			destination: { tab: owner.tab, view: owner.view },
			hasExplicitViewOrRecord: true,
		};
	}

	return { destination: urlDestination, hasExplicitViewOrRecord: false };
}

/**
 * Builds the shallow-routing destination for a Unit tab.
 *
 * An explicit secondary-view click is a list re-entry, so only the selected
 * view's own record key is cleared. A primary-tab round trip keeps the current
 * valid view and every other contextual key, allowing a nested detail to be
 * restored after visiting another tab.
 */
export function buildUnitTabNavigation(
	currentUrl: URL,
	tabValue: string,
	viewValue?: UnitView,
): { url: URL; destination: UnitDestination } {
	const requestedDestination = resolveUnitDestination(tabValue, viewValue);
	const url = new URL(currentUrl);
	url.searchParams.set('tab', requestedDestination.tab);

	if (viewValue !== undefined) {
		if (requestedDestination.view) url.searchParams.set('view', requestedDestination.view);
		for (const [param, owner] of Object.entries(CONTEXT_OWNER_BY_PARAM)) {
			if (owner?.tab === requestedDestination.tab && owner.view === requestedDestination.view) {
				url.searchParams.delete(param);
			}
		}
	} else if (!url.searchParams.has('view') && requestedDestination.view) {
		url.searchParams.set('view', requestedDestination.view);
	}

	return {
		url,
		destination: resolveUnitDestination(requestedDestination.tab, url.searchParams.get('view')),
	};
}

export function resolveUnitTab(value: string | undefined | null): UnitTab {
	return resolveUnitDestination(value).tab;
}
