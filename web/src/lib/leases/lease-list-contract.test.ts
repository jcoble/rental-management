import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const source = readFileSync(
	new URL('../../routes/(protected)/leases/+page.svelte', import.meta.url),
	'utf8'
);
const leaseDetailRouteSource = readFileSync(
	new URL('../../routes/(protected)/leases/[id]/+page.svelte', import.meta.url),
	'utf8'
);
const unitDetailRouteSource = readFileSync(
	new URL('../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
	'utf8'
);

test('lease list uses canonical lifecycle projection values', () => {
	for (const lifecycle of [
		'Preparing',
		'Upcoming',
		'Occupied',
		'Ending',
		'AccountingCloseout',
		'Closed',
		'Canceled'
	]) {
		assert.match(source, new RegExp(`value="${lifecycle}"`));
	}

	assert.doesNotMatch(source, /value="(?:Planned|MoveOutPlanned|PossessionReturned)"/);
});

test('lease list surfaces upcoming agreement and reconciliation state', () => {
	assert.match(source, /upcomingLeaseAgreementId/);
	assert.match(source, /hasReconciliationException/);
});

test('generic and Unit lease detail routes share one LeaseManagementDetail component', () => {
	assert.match(leaseDetailRouteSource, /import LeaseManagementDetail from '\$lib\/components\/leases\/LeaseManagementDetail\.svelte'/);
	assert.match(unitDetailRouteSource, /import LeaseManagementDetail from '\$lib\/components\/leases\/LeaseManagementDetail\.svelte'/);
	assert.match(leaseDetailRouteSource, /<LeaseManagementDetail \{leaseManagementId\} \/>/);
	assert.match(
		unitDetailRouteSource,
		/<LeaseManagementDetail leaseManagementId=\{selectedLeaseManagementId\} \/>/,
	);
	assert.doesNotMatch(leaseDetailRouteSource, /leaseManagements\.get\(/);
	assert.doesNotMatch(unitDetailRouteSource, /leaseManagements\.get\(/);
});
