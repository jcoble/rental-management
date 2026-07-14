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

	it('does not preserve retired tab aliases', () => {
		for (const retired of ['overview', 'applications', 'lease', 'expenses', 'make-ready', 'timeline']) {
			assert.deepEqual(resolveUnitDestination(retired), { tab: 'summary' });
		}
	});

	it('falls back to Summary for missing or invalid destinations', () => {
		assert.deepEqual(resolveUnitDestination(null), { tab: 'summary' });
		assert.deepEqual(resolveUnitDestination('unknown'), { tab: 'summary' });
	});
});
