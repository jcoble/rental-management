import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { getTenantsEmptyStateCopy } from './tenant-list-state.ts';

describe('tenant list state', () => {
	it('keeps first-run empty copy when no filters are active', () => {
		assert.deepEqual(getTenantsEmptyStateCopy({ hasActiveFilters: false }), {
			message: 'No tenants yet',
			description: 'Tenants are the people who rent from you. Add your first to start tracking leases and rent.',
			actionLabel: 'Add your first tenant',
		});
	});

	it('shows filter-aware empty copy when search or filters are active', () => {
		assert.deepEqual(getTenantsEmptyStateCopy({ hasActiveFilters: true }), {
			message: 'No tenants match your search',
			description: 'Try adjusting the search, or add a tenant that matches this view.',
			actionLabel: 'Add tenant',
		});
	});
});
