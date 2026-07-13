import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./tabs/LeaseTab.svelte', import.meta.url), 'utf8');

describe('unit lease tab create action', () => {
	it('offers only the canonical Prepare move-in and agreement-import entry points', () => {
		assert.match(source, /onclick=\{onScan\}[^>]*>[^<]*<ScanLine[^>]*\/> Import agreement/s);
		assert.match(source, /href="\/applications"[^>]*>[^<]*<Users[^>]*\/> Prepare move-in/s);
		assert.doesNotMatch(source, /unit-lease-create-dialog/);
		assert.doesNotMatch(source, /leases\.create/);
		assert.doesNotMatch(source, /tenants\.create/);
	});

	it('does not retain the removed client-side occupancy gate or modal lifecycle', () => {
		assert.doesNotMatch(source, /hasCurrentOccupyingLease/);
		assert.doesNotMatch(source, /hasOccupyingLease/);
		assert.doesNotMatch(source, /onInteractOutside/);
		assert.doesNotMatch(source, /onEscapeKeydown/);
	});

	it('loads canonical unit-scoped tenant relationships and agreement summaries', () => {
		assert.match(source, /queryKey:\s*\['lease-managements',\s*'unit',\s*dashboard\.unit\.id\]/);
		assert.match(source, /leaseManagements\.listPage\(\{/);
		assert.match(source, /unitId:\s*dashboard\.unit\.id/);
		assert.match(source, /sort:\s*'-updatedAtUtc'/);
		assert.match(source, /href=\{`\/leases\/\$\{relationship\.leaseManagementId\}`\}/);
		assert.match(source, /relationship\.agreementNumber/);
		assert.match(source, /relationship\.agreementStatus/);
	});

	it('uses precise LeaseManagement identifiers without an ambiguous lease id', () => {
		assert.match(source, /relationship\.leaseManagementId/);
		assert.doesNotMatch(source, /relationship\.leaseId/);
		assert.doesNotMatch(source, /selectedLeaseId/);
	});
});
