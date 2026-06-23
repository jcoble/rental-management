import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { getLeaseStatusOptions, getLeasesEmptyStateCopy } from './lease-list-state.ts';

describe('lease list state', () => {
	it('keeps first-run empty copy when no filters are active', () => {
		assert.deepEqual(getLeasesEmptyStateCopy({ hasActiveFilters: false }), {
			message: 'No leases yet',
			description: 'A lease ties a tenant to a unit and sets the rent and dates. Add your first to start tracking rent.',
			actionLabel: 'Add your first lease',
		});
	});

	it('shows filter-aware empty copy when search or status filters are active', () => {
		assert.deepEqual(getLeasesEmptyStateCopy({ hasActiveFilters: true }), {
			message: 'No leases match your filters',
			description: 'Try adjusting search or filters, or add a lease that matches this view.',
			actionLabel: 'Add lease',
		});
	});

	it('formats compound lease status options for people', () => {
		assert.deepEqual(getLeaseStatusOptions().find((option) => option.value === 'NoticeGiven'), {
			value: 'NoticeGiven',
			label: 'Notice given',
		});
	});
});
