import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

import { resolveUnitDestination, UNIT_TABS } from './unit-tabs.ts';

const unitPageSource = readFileSync(
	new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
	'utf8',
);

describe('Unit Tenant & lease surface', () => {
	it('keeps the six approved top-level Unit destinations without a nested tenant tab list', () => {
		assert.equal(UNIT_TABS.length, 6);
		assert.doesNotMatch(unitPageSource, /unit-tenant-lease-tabs/);
		assert.match(unitPageSource, /data-testid="unit-tenant-lease-surface"/);
	});

	it('renders Agreement and Residents together and preserves canonical entry views', () => {
		assert.match(unitPageSource, /data-testid="unit-agreement-section"/);
		assert.match(unitPageSource, /data-testid="unit-residents-section"/);
		assert.match(unitPageSource, /<LeaseTab/);
		assert.match(unitPageSource, /<ResidentsTab/);
		assert.deepEqual(resolveUnitDestination('tenant-lease', 'agreements'), {
			tab: 'tenant-lease',
			view: 'agreements',
		});
		assert.deepEqual(resolveUnitDestination('tenant-lease', 'residents'), {
			tab: 'tenant-lease',
			view: 'residents',
		});
	});

	it('keeps independent relationship, account, and nullable agreement scan context', () => {
		assert.match(unitPageSource, /dashboard\.leaseManagementId \?\? undefined/);
		assert.match(unitPageSource, /dashboard\.tenantAccountId \?\? undefined/);
		assert.match(unitPageSource, /dashboard\.currentLease\?\.id \?\? undefined/);
	});
});
