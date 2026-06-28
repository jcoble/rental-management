import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./tabs/LeaseTab.svelte', import.meta.url), 'utf8');

describe('unit lease tab create action', () => {
	it('offers a unit-scoped Add Lease flow with existing and new tenant paths', () => {
		assert.match(source, /data-testid="unit-lease-add-button"/);
		assert.match(source, /data-testid="unit-lease-create-dialog"/);
		assert.match(source, /data-testid="unit-lease-existing-tenant-mode"/);
		assert.match(source, /data-testid="unit-lease-new-tenant-mode"/);
		assert.match(source, /tenants\.create/);
		assert.match(source, /leases\.create/);
		assert.match(source, /propertyId:\s*String\(dashboard\.unit\.propertyId\)/);
		assert.match(source, /unitId:\s*String\(dashboard\.unit\.id\)/);
		assert.doesNotMatch(source, /Emergency contact/);
	});

	it('loads unit-scoped lease history so prior leases stay selectable', () => {
		assert.match(source, /queryKey:\s*\['unit-leases',\s*portfolioId,\s*dashboard\.unit\.id\]/);
		assert.match(source, /leases\.listPage\(portfolioId,\s*\{/);
		assert.match(source, /unitId:\s*dashboard\.unit\.id/);
		assert.match(source, /sort:\s*'-startDate'/);
		assert.match(source, /data-testid="unit-lease-history"/);
		assert.match(source, /data-testid=\{`unit-lease-history-item-\$\{lease\.id\}`\}/);
	});
});
