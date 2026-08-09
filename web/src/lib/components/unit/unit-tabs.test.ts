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
});
