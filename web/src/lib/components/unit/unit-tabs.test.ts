import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { resolveUnitDestination, resolveUnitTab, UNIT_TABS } from './unit-tabs.ts';

describe('unit tab routing', () => {
	it('exposes the six approved Unit Command Center areas', () => {
		assert.deepEqual(UNIT_TABS, ['summary', 'leasing', 'tenant-lease', 'money', 'maintenance', 'documents-history']);
		for (const tab of UNIT_TABS) assert.equal(resolveUnitTab(tab), tab);
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
	});

	it('maps existing deep links into the consolidated areas', () => {
		assert.deepEqual(resolveUnitDestination('applications'), {
			tab: 'leasing',
			view: 'applications',
		});
		assert.deepEqual(resolveUnitDestination('lease'), {
			tab: 'tenant-lease',
			view: 'agreements',
		});
		assert.deepEqual(resolveUnitDestination('expenses'), { tab: 'money' });
		assert.deepEqual(resolveUnitDestination('make-ready'), {
			tab: 'maintenance',
			view: 'turnover',
		});
		assert.deepEqual(resolveUnitDestination('timeline'), {
			tab: 'documents-history',
			view: 'history',
		});
	});

	it('falls back to Summary for missing or invalid destinations', () => {
		assert.deepEqual(resolveUnitDestination(null), { tab: 'summary' });
		assert.deepEqual(resolveUnitDestination('unknown'), { tab: 'summary' });
	});
});
