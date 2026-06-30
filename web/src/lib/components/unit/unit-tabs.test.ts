import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
	import { resolveUnitTab, UNIT_TABS } from './unit-tabs.ts';

describe('unit tab routing', () => {
	it('accepts every supported unit tab from the URL', () => {
		for (const tab of UNIT_TABS) {
			assert.equal(resolveUnitTab(tab), tab);
		}
	});

	it('includes listing as a first-class unit workflow tab', () => {
		assert.deepEqual(UNIT_TABS, [
			'overview',
			'listing',
			'lease',
			'applications',
			'rent',
			'maintenance',
			'documents',
			'expenses',
			'timeline'
		]);
	});

	it('falls back to overview for missing or invalid tab query values', () => {
		assert.equal(resolveUnitTab(null), 'overview');
		assert.equal(resolveUnitTab(''), 'overview');
		assert.equal(resolveUnitTab('unknown'), 'overview');
	});
});
