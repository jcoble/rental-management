import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./LeaseDetail.svelte', import.meta.url), 'utf8');

describe('lease detail context links', () => {
	it('renders the header context as navigable property, unit, and tenant links', () => {
		assert.match(source, /data-testid="lease-detail-context-links"/);
		assert.match(source, /data-testid="lease-detail-header-property-link"/);
		assert.ok(source.includes('href="/properties/{lease.propertyId}"'));
		assert.match(source, /data-testid="lease-detail-header-unit-link"/);
		assert.ok(source.includes('href="/units/{lease.unitId}?tab=lease"'));
		assert.match(source, /data-testid="lease-detail-header-tenant-link"/);
		assert.ok(source.includes('href="/tenants/{tenant.id}"'));
		assert.ok(source.includes('href="/tenants/{lease.tenantId}"'));
	});
});
