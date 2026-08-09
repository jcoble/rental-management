import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	buildUnitTabNavigation,
	resolveUnitDestination,
	resolveUnitTab,
	UNIT_SUBNAV_ITEM_CLASS,
	UNIT_SUBNAV_LIST_CLASS,
	UNIT_CONTEXTUAL_PARAMS,
	UNIT_TABS,
} from './unit-tabs.ts';

type UnitHistoryState = {
	unitTab: string;
	unitView: string | null;
	unitPaymentId: number | null;
	unitExpenseId: number | null;
};

type UnitHistoryEntry = {
	url: URL;
	state: UnitHistoryState;
};

function historyEntry(input: string | URL): UnitHistoryEntry {
	const url = typeof input === 'string' ? new URL(input) : new URL(input.href);
	const destination = resolveUnitDestination(url.searchParams.get('tab'), url.searchParams.get('view'));
	return {
		url,
		state: {
			unitTab: destination.tab,
			unitView: destination.view ?? null,
			unitPaymentId: null,
			unitExpenseId: null,
		},
	};
}

function reduceUnitTab(entry: UnitHistoryEntry, tab: string, view?: Parameters<typeof buildUnitTabNavigation>[2]): UnitHistoryEntry {
	const { url, destination } = buildUnitTabNavigation(entry.url, tab, view);
	return {
		url,
		state: {
			...entry.state,
			unitTab: destination.tab,
			unitView: destination.view ?? null,
			unitPaymentId: null,
			unitExpenseId: null,
		},
	};
}

function activeDestination(entry: UnitHistoryEntry) {
	return resolveUnitDestination(
		entry.state.unitTab ?? entry.url.searchParams.get('tab'),
		entry.state.unitView ?? entry.url.searchParams.get('view'),
	);
}

describe('unit tab routing', () => {
	it('exposes the six approved Unit Command Center areas', () => {
		assert.deepEqual(UNIT_TABS, ['summary', 'leasing', 'tenant-lease', 'money', 'maintenance', 'documents-history']);
		for (const tab of UNIT_TABS) assert.equal(resolveUnitTab(tab), tab);
		assert.doesNotMatch(UNIT_SUBNAV_LIST_CLASS, /m3-tabs-list/);
		assert.match(UNIT_SUBNAV_LIST_CLASS, /bg-muted/);
		assert.match(UNIT_SUBNAV_ITEM_CLASS, /text-sm/);
		assert.match(UNIT_SUBNAV_ITEM_CLASS, /text-muted-foreground/);
		assert.match(UNIT_SUBNAV_ITEM_CLASS, /aria-\[current=page\]:text-foreground/);
	});

	it('restores a meaningful subsection from canonical URLs', () => {
		assert.deepEqual(resolveUnitDestination('leasing', 'applications'), {
			tab: 'leasing',
			view: 'applications',
		});
		assert.deepEqual(resolveUnitDestination('maintenance', 'turnover'), {
			tab: 'maintenance',
			view: 'turnover',
		});
		for (const view of ['work-orders', 'inspections', 'recurring', 'turnover'] as const) {
			assert.deepEqual(resolveUnitDestination('maintenance', view), {
				tab: 'maintenance',
				view,
			});
		}
		assert.deepEqual(resolveUnitDestination('maintenance', 'invalid'), {
			tab: 'maintenance',
			view: 'work-orders',
		});
	});

	it('does not preserve retired tab aliases', () => {
		for (const retired of ['overview', 'applications', 'lease', 'expenses', 'make-ready', 'timeline']) {
			assert.deepEqual(resolveUnitDestination(retired), { tab: 'summary' });
		}
	});

	it('falls back to Summary for missing or invalid destinations', () => {
		assert.deepEqual(resolveUnitDestination(null), { tab: 'summary' });
		assert.deepEqual(resolveUnitDestination('unknown'), { tab: 'summary' });
	});

	it('keeps every contextual key through a primary-tab round trip', () => {
		const values = Object.fromEntries(UNIT_CONTEXTUAL_PARAMS.map((param) => [param, `value-${param}`]));
		values.view = 'applications';
		const startingUrl = new URL('https://rental.local/units/42?tab=leasing');
		for (const [param, value] of Object.entries(values)) startingUrl.searchParams.set(param, value);

		const summary = buildUnitTabNavigation(startingUrl, 'summary');
		for (const param of UNIT_CONTEXTUAL_PARAMS) {
			assert.equal(summary.url.searchParams.get(param), values[param]);
		}

		const leasing = buildUnitTabNavigation(summary.url, 'leasing');
		assert.deepEqual(leasing.destination, { tab: 'leasing', view: 'applications' });
		for (const param of UNIT_CONTEXTUAL_PARAMS) {
			assert.equal(leasing.url.searchParams.get(param), values[param]);
		}
	});

	it('clears only the selected view record when a secondary list is explicitly re-entered', () => {
		const url = new URL('https://rental.local/units/42?tab=summary&view=applications&app=7&wo=8');
		const navigation = buildUnitTabNavigation(url, 'leasing', 'applications');

		assert.equal(navigation.url.searchParams.get('app'), null);
		assert.equal(navigation.url.searchParams.get('wo'), '8');
		assert.deepEqual(navigation.destination, { tab: 'leasing', view: 'applications' });
	});

	it('restores an Applications detail after visiting another primary tab', () => {
		const applications = historyEntry('https://rental.local/units/42?tab=leasing&view=applications&app=7');
		const maintenance = reduceUnitTab(applications, 'maintenance');
		assert.deepEqual(activeDestination(maintenance), { tab: 'maintenance', view: 'work-orders' });

		const restored = reduceUnitTab(maintenance, 'leasing');
		assert.deepEqual(activeDestination(restored), { tab: 'leasing', view: 'applications' });
		assert.equal(restored.url.searchParams.get('view'), 'applications');
		assert.equal(restored.url.searchParams.get('app'), '7');
		assert.equal(restored.state.unitPaymentId, null);
		assert.equal(restored.state.unitExpenseId, null);
	});

	it('does not carry contextual params into a different unit URL', () => {
		const previousUnit = historyEntry(
			'https://rental.local/units/42?tab=leasing&view=applications&app=7&wo=8&payment=9&expense=10&tenantAccount=11&leaseManagement=12&agreement=13&ledger=14&action=15',
		);
		const differentUnit = historyEntry(new URL('/units/43?tab=summary', previousUnit.url));
		const leasing = reduceUnitTab(differentUnit, 'leasing');

		assert.equal(leasing.url.pathname, '/units/43');
		assert.deepEqual(activeDestination(leasing), { tab: 'leasing', view: 'listing' });
		for (const param of UNIT_CONTEXTUAL_PARAMS) {
			assert.equal(
				leasing.url.searchParams.get(param),
				param === 'view' ? 'listing' : null,
				`different-unit navigation leaked ${param}`,
			);
		}
	});

	it('keeps URL/state history entries coherent for reducer-level back and forward', () => {
		const applications = historyEntry('https://rental.local/units/42?tab=leasing&view=applications&app=7');
		const maintenance = reduceUnitTab(applications, 'maintenance');
		const restored = reduceUnitTab(maintenance, 'leasing');
		const history = [applications, maintenance, restored];
		let cursor = history.length - 1;

		const back = history[--cursor];
		assert.deepEqual(activeDestination(back), { tab: 'maintenance', view: 'work-orders' });
		assert.equal(back.url.searchParams.get('app'), '7');
		assert.equal(back.state.unitTab, 'maintenance');
		assert.equal(back.state.unitView, 'work-orders');

		const forward = history[++cursor];
		assert.deepEqual(activeDestination(forward), { tab: 'leasing', view: 'applications' });
		assert.equal(forward.url.searchParams.get('app'), '7');
		assert.equal(forward.state.unitTab, 'leasing');
		assert.equal(forward.state.unitView, 'applications');
	});
});
