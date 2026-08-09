import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	createUnitTabNavigationHandler,
	type UnitTabNavigationState,
} from './unit-tab-navigation.ts';

type NavigationContainer = {
	url: URL;
	state: UnitTabNavigationState;
	history: Array<{ url: URL; state: UnitTabNavigationState }>;
};

function createContainer(url: string, state: UnitTabNavigationState): NavigationContainer {
	return {
		url: new URL(url),
		state,
		history: [],
	};
}

function connect(container: NavigationContainer) {
	return createUnitTabNavigationHandler({
		getCurrent: () => ({ url: container.url, state: container.state }),
		pushState: (url, state) => {
			container.url = new URL(url, container.url);
			container.state = state;
			container.history.push({ url: container.url, state });
		},
	});
}

describe('unit tab page navigation integration', () => {
	it('restores Applications when the page state retains the nested view but the URL does not', () => {
		const container = createContainer('https://rental.local/units/10?tab=leasing&view=applications&app=7', {
			unitTab: 'leasing',
			unitView: 'applications',
		});
		const setTab = connect(container);

		// This models the live failure boundary: SvelteKit's current URL has been
		// normalized to the primary tab while its shallow state still knows the view.
		container.url = new URL('https://rental.local/units/10?tab=leasing&app=7');
		setTab('summary');
		assert.equal(container.url.searchParams.get('view'), 'applications');
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
			{ unitTab: 'leasing', unitView: 'applications' },
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

	it('does not reuse a remembered nested view for a different unit path', () => {
		const container = createContainer('https://rental.local/units/10?tab=leasing&view=applications&app=7', {
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
});
