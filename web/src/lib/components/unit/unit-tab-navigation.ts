import {
	buildUnitTabNavigation,
	resolveUnitDestination,
	resolveUnitUrlDestination,
	type UnitTab,
	type UnitView,
} from './unit-tabs.ts';

/**
 * The shallow state written by the Unit page alongside the URL. The memory is
 * keyed by pathname so a browser history entry for another unit can never
 * restore a nested view from this unit.
 */
export type UnitTabNavigationState = Record<string, unknown> & {
	/** Pathname of the Unit that owns the scalar shallow state below. */
	unitPathname?: string | null;
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
	replaceState?: (url: string, state: UnitTabNavigationState) => void;
};

/**
 * Resolves the visible destination for the Unit page. Explicit URL context is
 * authoritative; shallow state is only eligible when it was written for this
 * exact Unit pathname.
 */
export function resolveUnitPageDestination(
	url: URL,
	state: UnitTabNavigationState,
) {
	const urlResolution = resolveUnitUrlDestination(url);
	if (urlResolution.hasExplicitViewOrRecord || state.unitPathname !== url.pathname) {
		return urlResolution.destination;
	}

	return resolveUnitDestination(
		state.unitTab ?? url.searchParams.get('tab'),
		state.unitView ?? url.searchParams.get('view'),
	);
}

/**
 * Synchronizes URL-owned destination context into the keyed shallow state.
 * `replaceState` callers use this to overwrite stale memory without adding a
 * browser-history entry. A different Unit is reset to its URL destination.
 */
export function synchronizeUnitTabState(
	url: URL,
	state: UnitTabNavigationState,
): UnitTabNavigationState | null {
	const urlResolution = resolveUnitUrlDestination(url);
	const stateBelongsToUnit = state.unitPathname === url.pathname;
	if (stateBelongsToUnit && !urlResolution.hasExplicitViewOrRecord) return null;

	const destination = urlResolution.destination;
	const existingMemory = state.unitViewByPath?.[url.pathname] ?? {};
	const stateAlreadyMatchesUrl =
		stateBelongsToUnit
		&& state.unitTab === destination.tab
		&& (state.unitView ?? null) === (destination.view ?? null)
		&& (!destination.view || existingMemory[destination.tab] === destination.view)
		&& state.unitPaymentId == null
		&& state.unitExpenseId == null;
	if (stateAlreadyMatchesUrl) return null;

	const unitViewByPath = { ...(state.unitViewByPath ?? {}) };
	const memoryForPath = { ...(unitViewByPath[url.pathname] ?? {}) };
	if (destination.view) memoryForPath[destination.tab] = destination.view;
	unitViewByPath[url.pathname] = memoryForPath;

	return {
		...state,
		unitPathname: url.pathname,
		unitTab: destination.tab,
		unitView: destination.view ?? null,
		unitViewByPath,
		unitPaymentId: null,
		unitExpenseId: null,
	};
}

/**
 * Creates the page's tab click handler around the same URL/state container
 * SvelteKit uses for shallow routing. Keeping this boundary outside the
 * component makes it possible to exercise the real handler with a history
 * container instead of testing only the URL helper in isolation.
 */
export function createUnitTabNavigationHandler({
	getCurrent,
	pushState,
	replaceState,
}: UnitTabNavigationAdapter): (tab: string, view?: UnitView) => void {
	return (tab: string, view?: UnitView) => {
		const current = getCurrent();
		const urlResolution = resolveUnitUrlDestination(current.url);
		const synchronizedState = synchronizeUnitTabState(current.url, current.state);
		const baseState = synchronizedState ?? current.state;
		const stateBelongsToUnit = baseState.unitPathname === current.url.pathname;
		const currentDestination = resolveUnitPageDestination(current.url, baseState);
		const memoryForPath = { ...(baseState.unitViewByPath?.[current.url.pathname] ?? {}) };
		if (currentDestination.view && (urlResolution.hasExplicitViewOrRecord || stateBelongsToUnit)) {
			memoryForPath[currentDestination.tab] = currentDestination.view;
		}

		// The page state can still carry the active nested view when SvelteKit has
		// normalized it out of the visible URL. Put it back before building a
		// primary-tab destination so the contextual `view` key is not lost.
		const currentUrl = new URL(current.url);
		if (
			view === undefined
			&& stateBelongsToUnit
			&& currentDestination.view
			&& currentUrl.searchParams.get('view') !== currentDestination.view
		) {
			currentUrl.searchParams.set('view', currentDestination.view);
		}

		const requestedDestination = resolveUnitDestination(tab, view);
		const navigation = buildUnitTabNavigation(currentUrl, tab, view);
		const nextUrl = navigation.url;
		let destination = navigation.destination;

		// A primary-tab return uses the last view selected for that tab. This is
		// separate from explicit sub-navigation: an explicit list click is a
		// re-entry and keeps the helper's own-record cleanup semantics.
		const incomingHasExplicitView = current.url.searchParams.has('view') || urlResolution.hasExplicitViewOrRecord;
		if (view === undefined && requestedDestination.tab !== 'summary' && !incomingHasExplicitView) {
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
			...baseState,
			unitPathname: current.url.pathname,
			unitTab: destination.tab,
			unitView: destination.view ?? null,
			unitViewByPath: nextMemory,
			unitPaymentId: null,
			unitExpenseId: null,
		};

		if (`${nextUrl.pathname}${nextUrl.search}` === `${current.url.pathname}${current.url.search}`) {
			if (synchronizedState && replaceState) {
				replaceState(`${nextUrl.pathname}${nextUrl.search}`, nextState);
			}
			return;
		}
		pushState(`${nextUrl.pathname}${nextUrl.search}`, nextState);
	};
}
