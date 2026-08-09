import {
	buildUnitTabNavigation,
	resolveUnitDestination,
	type UnitTab,
	type UnitView,
} from './unit-tabs.ts';

/**
 * The shallow state written by the Unit page alongside the URL. The memory is
 * keyed by pathname so a browser history entry for another unit can never
 * restore a nested view from this unit.
 */
export type UnitTabNavigationState = Record<string, unknown> & {
	unitTab?: string | null;
	unitView?: string | null;
	unitViewByPath?: Record<string, Partial<Record<UnitTab, UnitView>>>;
	unitPaymentId?: number | null;
	unitExpenseId?: number | null;
};

type UnitTabNavigationSnapshot = {
	url: URL;
	state: UnitTabNavigationState;
};

type UnitTabNavigationAdapter = {
	getCurrent: () => UnitTabNavigationSnapshot;
	pushState: (url: string, state: UnitTabNavigationState) => void;
};

/**
 * Creates the page's tab click handler around the same URL/state container
 * SvelteKit uses for shallow routing. Keeping this boundary outside the
 * component makes it possible to exercise the real handler with a history
 * container instead of testing only the URL helper in isolation.
 */
export function createUnitTabNavigationHandler({
	getCurrent,
	pushState,
}: UnitTabNavigationAdapter): (tab: string, view?: UnitView) => void {
	return (tab: string, view?: UnitView) => {
		const current = getCurrent();
		const currentDestination = resolveUnitDestination(
			current.state.unitTab ?? current.url.searchParams.get('tab'),
			current.state.unitView ?? current.url.searchParams.get('view'),
		);
		const memoryForPath = { ...(current.state.unitViewByPath?.[current.url.pathname] ?? {}) };
		if (currentDestination.view) memoryForPath[currentDestination.tab] = currentDestination.view;

		// The page state can still carry the active nested view when SvelteKit has
		// normalized it out of the visible URL. Put it back before building a
		// primary-tab destination so the contextual `view` key is not lost.
		const currentUrl = new URL(current.url);
		if (view === undefined && currentDestination.view && currentUrl.searchParams.get('view') !== currentDestination.view) {
			currentUrl.searchParams.set('view', currentDestination.view);
		}

		const requestedDestination = resolveUnitDestination(tab, view);
		const navigation = buildUnitTabNavigation(currentUrl, tab, view);
		const nextUrl = navigation.url;
		let destination = navigation.destination;

		// A primary-tab return uses the last view selected for that tab. This is
		// separate from explicit sub-navigation: an explicit list click is a
		// re-entry and keeps the helper's own-record cleanup semantics.
		if (view === undefined && requestedDestination.tab !== 'summary') {
			const rememberedView = memoryForPath[requestedDestination.tab];
			if (rememberedView) {
				nextUrl.searchParams.set('view', rememberedView);
				destination = resolveUnitDestination(requestedDestination.tab, rememberedView);
			}
		} else if (view !== undefined && destination.view) {
			memoryForPath[destination.tab] = destination.view;
		}

		const nextMemory = {
			...(current.state.unitViewByPath ?? {}),
			[current.url.pathname]: memoryForPath,
		};
		const nextState: UnitTabNavigationState = {
			...current.state,
			unitTab: destination.tab,
			unitView: destination.view ?? null,
			unitViewByPath: nextMemory,
			unitPaymentId: null,
			unitExpenseId: null,
		};

		if (`${nextUrl.pathname}${nextUrl.search}` === `${current.url.pathname}${current.url.search}`) return;
		pushState(`${nextUrl.pathname}${nextUrl.search}`, nextState);
	};
}
