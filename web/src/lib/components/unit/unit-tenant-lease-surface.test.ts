import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

import { resolveUnitDestination, UNIT_TABS } from './unit-tabs.ts';

const unitPageSource = readFileSync(
	new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
	'utf8',
);

describe('Unit Tenant & lease surface', () => {
	it('keeps the six approved top-level Unit destinations with a subordinate tenant section switcher', () => {
		assert.equal(UNIT_TABS.length, 6);
		assert.match(unitPageSource, /aria-label="Tenant and lease sections"/);
		assert.match(unitPageSource, /data-testid="unit-tenant-lease-subnav"/);
		assert.match(unitPageSource, /class=\{UNIT_SUBNAV_LIST_CLASS\}/);
		assert.match(unitPageSource, /aria-current=\{activeView === 'agreements' \? 'page' : undefined\}/);
		assert.doesNotMatch(unitPageSource, /unit-tenant-lease-tabs/);
		assert.equal(unitPageSource.match(/m3-tabs-list/g)?.length, 1);
		assert.equal(unitPageSource.match(/class="m3-tabs-trigger"/g)?.length, UNIT_TABS.length);
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
