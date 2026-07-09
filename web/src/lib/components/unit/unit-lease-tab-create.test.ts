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
		assert.doesNotMatch(source, /tenants\.create/);
		assert.match(source, /newTenant:\s*buildSimpleTenantPayload/);
		assert.match(source, /leases\.create/);
		assert.match(source, /propertyId:\s*String\(dashboard\.unit\.propertyId\)/);
		assert.match(source, /unitId:\s*String\(dashboard\.unit\.id\)/);
		assert.doesNotMatch(source, /Emergency contact/);
	});

	it('blocks duplicate active-lease entry before opening the form and prevents implicit dismissal', () => {
		assert.match(source, /disabled=\{isCheckingLeaseAvailability \|\| hasCurrentOccupyingLease\}/);
		assert.match(source, /dashboard\.unit\.status !== 'Vacant'/);
		assert.match(source, /unitLeases\.some\(\(lease\) => hasOccupyingLease\(lease\)\)/);
		assert.match(source, /data-testid="unit-lease-add-blocked-reason"/);
		assert.match(source, /onInteractOutside=\{\(event\) => event\.preventDefault\(\)\}/);
		assert.match(source, /onEscapeKeydown=\{\(event\) => event\.preventDefault\(\)\}/);
	});

	it('loads unit-scoped lease history so prior leases stay selectable', () => {
		assert.match(source, /queryKey:\s*\['unit-leases',\s*portfolioId,\s*dashboard\.unit\.id\]/);
		assert.match(source, /leases\.listPage\(portfolioId,\s*\{/);
		assert.match(source, /unitId:\s*dashboard\.unit\.id/);
		assert.match(source, /sort:\s*'-startDate'/);
		assert.match(source, /data-testid="unit-lease-history"/);
		assert.match(source, /data-testid=\{`unit-lease-history-item-\$\{lease\.id\}`\}/);
	});

	it('loads only tenants available for a new lease in the Add Lease picker', () => {
		assert.match(source, /queryKey:\s*\['tenants',\s*portfolioId,\s*'unit-lease-create',\s*'available-for-lease'\]/);
		assert.match(source, /tenants\.listPage\(portfolioId,\s*\{/);
		assert.match(source, /availableForLease:\s*true/);
		assert.match(source, /tenants=\{availableTenants\}/);
	});
});
