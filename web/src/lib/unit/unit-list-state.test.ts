import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { getUnitsEmptyStateCopy } from './unit-list-state.ts';

describe('unit list state', () => {
	it('keeps first-run empty copy when no filters are active', () => {
		assert.deepEqual(getUnitsEmptyStateCopy({ hasActiveFilters: false }), {
			message: 'No units yet',
			description: 'Units live under a property. Add a property and its units to start managing them here.',
		});
	});

	it('shows filter-aware empty copy when filters are active', () => {
		assert.deepEqual(getUnitsEmptyStateCopy({ hasActiveFilters: true }), {
			message: 'No units match your filters',
			description: 'Try adjusting search or filters, or add a unit that matches this view.',
		});
	});
});
