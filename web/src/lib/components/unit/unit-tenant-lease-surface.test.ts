import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

import { resolveUnitDestination, UNIT_TABS } from './unit-tabs.ts';

const unitPageSource = readFileSync(
	new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
	'utf8',
);

describe('Unit Tenant & lease surface', () => {
	it('keeps the six approved top-level Unit destinations with a focused tenant sub-tab list', () => {
		assert.equal(UNIT_TABS.length, 6);
		assert.match(unitPageSource, /unit-tenant-lease-tabs/);
		assert.match(unitPageSource, /data-testid="unit-tenant-lease-surface"/);
	});

	it('renders only the selected Agreement or Residents view and preserves canonical entry views', () => {
		assert.match(unitPageSource, /data-testid="unit-agreement-section"/);
		assert.match(unitPageSource, /data-testid="unit-residents-section"/);
		assert.match(unitPageSource, /<LeaseTab/);
		assert.match(unitPageSource, /<ResidentsTab/);
		assert.match(unitPageSource, /\{#if activeView === 'residents'\}/);
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

	it('passes the unit dashboard into the listing tab so occupied units can link back to Tenant & lease', () => {
		assert.match(unitPageSource, /<ListingTab \{dashboard\} \/>/);
	});

	it('renders the selected lease management detail inside the Agreement view', () => {
		assert.match(unitPageSource, /page\.url\.searchParams\.get\('leaseManagement'\)/);
		assert.match(unitPageSource, /\{#if selectedLeaseManagementId\}/);
		assert.match(
			unitPageSource,
			/<LeaseManagementDetail leaseManagementId=\{selectedLeaseManagementId\} \/>/,
		);
	});

	it('returns from lease management detail to the relationship list without losing tab context', () => {
		assert.match(unitPageSource, /params\.delete\('leaseManagement'\)/);
		assert.match(unitPageSource, /href=\{leaseRelationshipListHref\}/);
		assert.match(unitPageSource, /\/> Back to tenant relationships/);
	});
});
