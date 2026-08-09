import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	createUnitTabNavigationHandler,
	resolveUnitPageDestination,
	synchronizeUnitTabState,
	type UnitTabNavigationState,
} from './unit-tab-navigation.ts';

type NavigationContainer = {
	url: URL;
	state: UnitTabNavigationState;
	history: Array<{ url: URL; state: UnitTabNavigationState }>;
	cursor: number;
};

function createContainer(url: string, state: UnitTabNavigationState): NavigationContainer {
	const initialUrl = new URL(url);
	return {
		url: initialUrl,
		state,
		history: [{ url: new URL(initialUrl), state }],
		cursor: 0,
	};
}

function connect(container: NavigationContainer) {
	return createUnitTabNavigationHandler({
		getCurrent: () => ({ url: container.url, state: container.state }),
		pushState: (url, state) => {
			container.url = new URL(url, container.url);
			container.state = state;
			container.history = [
				...container.history.slice(0, container.cursor + 1),
				{ url: new URL(container.url), state },
			];
			container.cursor += 1;
		},
		replaceState: (_url, state) => {
			container.state = state;
			container.history[container.cursor] = {
				url: new URL(container.url),
				state,
			};
		},
	});
}

function moveHistoryCursor(container: NavigationContainer, offset: number) {
	const nextCursor = container.cursor + offset;
	assert.ok(nextCursor >= 0 && nextCursor < container.history.length, `history cursor ${nextCursor} is out of range`);
	container.cursor = nextCursor;
	const entry = container.history[container.cursor];
	container.url = new URL(entry.url);
	container.state = entry.state;
}

function tryMoveHistoryCursor(container: NavigationContainer, offset: number) {
	const nextCursor = container.cursor + offset;
	if (nextCursor < 0 || nextCursor >= container.history.length) return false;
	moveHistoryCursor(container, offset);
	return true;
}

describe('unit tab page navigation integration', () => {
	it('initializes a cold deep link when the browser has no shallow page state yet', () => {
		const url = new URL('https://rental.local/units/10?tab=money&view=tenant-account&tenantAccount=11');
		const coldState = undefined as unknown as UnitTabNavigationState;

		assert.doesNotThrow(() => synchronizeUnitTabState(url, coldState));
		assert.deepEqual(synchronizeUnitTabState(url, coldState), {
			unitPathname: '/units/10',
			unitTab: 'money',
			unitView: 'tenant-account',
			unitViewByPath: { '/units/10': { money: 'tenant-account' } },
			unitPaymentId: null,
			unitExpenseId: null,
		});
	});

	it('restores Applications when the page state retains the nested view but the URL does not', () => {
		const container = createContainer('https://rental.local/units/10?tab=leasing&view=applications&app=7', {
			unitPathname: '/units/10',
			unitTab: 'leasing',
			unitView: 'applications',
		});
		const setTab = connect(container);

		// This models the live failure boundary: SvelteKit's current URL has been
		// normalized to the primary tab while its shallow state still knows the view.
		container.url = new URL('https://rental.local/units/10?tab=leasing&app=7');
		setTab('summary');
		assert.equal(container.url.searchParams.get('view'), null);
		assert.equal(container.state.unitView, null);

		setTab('leasing');
		assert.equal(container.url.searchParams.get('view'), 'applications');
		assert.equal(container.state.unitTab, 'leasing');
		assert.equal(container.state.unitView, 'applications');
		assert.equal(container.url.searchParams.get('app'), '7');
	});

	it('keeps the ten contextual parameters and clears only an explicitly selected record key', () => {
		const container = createContainer(
			'https://rental.local/units/10?tab=leasing&view=applications&wo=8&app=7&payment=9&expense=10&tenantAccount=11&leaseManagement=12&agreement=13&ledger=14&action=15',
			{ unitPathname: '/units/10', unitTab: 'leasing', unitView: 'applications' },
		);
		const setTab = connect(container);

		setTab('maintenance');
		setTab('leasing', 'applications');

		for (const [key, value] of Object.entries({
			view: 'applications',
			wo: '8',
			app: null,
			payment: '9',
			expense: '10',
			tenantAccount: '11',
			leaseManagement: '12',
			agreement: '13',
			ledger: '14',
			action: '15',
		})) {
			assert.equal(container.url.searchParams.get(key), value, `unexpected ${key} value`);
		}
	});

	it('restores Maintenance Inspections after a round-trip through Leasing Applications', () => {
		const container = createContainer('https://rental.local/units/10?tab=maintenance&view=inspections', {
			unitPathname: '/units/10',
			unitTab: 'maintenance',
			unitView: 'inspections',
			unitViewByPath: {
				'/units/10': { maintenance: 'inspections' },
			},
		});
		const setTab = connect(container);

		setTab('leasing', 'applications');
		setTab('maintenance');

		assert.equal(container.url.searchParams.get('tab'), 'maintenance');
		assert.equal(container.url.searchParams.get('view'), 'inspections');
		assert.equal(container.state.unitViewByPath?.['/units/10']?.maintenance, 'inspections');
	});

	it('does not reuse a remembered nested view for a different unit path', () => {
		const container = createContainer('https://rental.local/units/10?tab=leasing&view=applications&app=7', {
			unitPathname: '/units/10',
			unitTab: 'leasing',
			unitView: 'applications',
		});
		const setTab = connect(container);

		setTab('summary');
		container.url = new URL('https://rental.local/units/11?tab=summary');
		setTab('leasing');

		assert.equal(container.url.pathname, '/units/11');
		assert.equal(container.url.searchParams.get('view'), 'listing');
		assert.equal(container.url.searchParams.get('app'), null);
		assert.equal(container.state.unitView, 'listing');
	});

	it('lets an explicit URL view replace stale Applications state and memory', () => {
		const container = createContainer('https://rental.local/units/10?tab=leasing&view=listing', {
			unitTab: 'leasing',
			unitView: 'applications',
			unitViewByPath: {
				'/units/10': { leasing: 'applications' },
			},
		});
		const setTab = connect(container);

		// Explicit URL context must remain authoritative even when shallow state
		// still points at Applications.
		assert.deepEqual(
			resolveUnitPageDestination(container.url, container.state),
			{ tab: 'leasing', view: 'listing' },
		);

		setTab('leasing');
		assert.equal(container.state.unitViewByPath?.['/units/10']?.leasing, 'listing');

		setTab('summary');
		assert.equal(container.state.unitViewByPath?.['/units/10']?.leasing, 'listing');
	});

	it('starts a different unit on its default Leasing view and traverses exact history URLs with a cursor', () => {
		const container = createContainer('https://rental.local/units/10?tab=leasing&view=applications&app=7', {
			unitPathname: '/units/10',
			unitTab: 'leasing',
			unitView: 'applications',
			unitViewByPath: {
				'/units/10': { leasing: 'applications' },
			},
		});
		const setTab = connect(container);

		// Direct unit navigation can carry the previous page's shallow state;
		// it must not inherit Unit 10's Applications view or record.
		container.url = new URL('https://rental.local/units/11?tab=leasing');
		setTab('leasing');
		assert.equal(container.url.searchParams.get('view'), 'listing');
		assert.equal(container.url.searchParams.get('app'), null);

		setTab('summary');
		assert.equal(container.history.length, 3);
		moveHistoryCursor(container, -1);
		assert.equal(`${container.url.pathname}${container.url.search}`, '/units/11?tab=leasing&view=listing');
		moveHistoryCursor(container, -1);
		assert.equal(`${container.url.pathname}${container.url.search}`, '/units/10?tab=leasing&view=applications&app=7');
		moveHistoryCursor(container, 2);
		assert.equal(`${container.url.pathname}${container.url.search}`, '/units/11?tab=summary');
	});

	it('truncates forward history when a new primary-tab navigation is pushed from a back entry', () => {
		const container = createContainer('https://rental.local/units/10?tab=summary', {
			unitPathname: '/units/10',
			unitTab: 'summary',
			unitView: null,
		});
		const setTab = connect(container);

		setTab('leasing');
		setTab('maintenance');
		assert.equal(container.history.length, 3);
		moveHistoryCursor(container, -1);
		assert.equal(`${container.url.pathname}${container.url.search}`, '/units/10?tab=leasing&view=listing');

		setTab('money');
		assert.equal(container.history.length, 3);
		assert.equal(container.cursor, 2);
		assert.equal(`${container.url.pathname}${container.url.search}`, '/units/10?tab=money&view=tenant-account');
		assert.equal(tryMoveHistoryCursor(container, 1), false);
		assert.equal(`${container.url.pathname}${container.url.search}`, '/units/10?tab=money&view=tenant-account');
	});
});
