import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { leaseAgreementStatusLabel, leaseLifecycleLabel } from './lease-list-labels.ts';

const source = readFileSync(
	new URL('../../routes/(protected)/leases/+page.svelte', import.meta.url),
	'utf8'
);
const labelsSource = readFileSync(new URL('./lease-list-labels.ts', import.meta.url), 'utf8');
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

test('lease list uses plain status language', () => {
	assert.doesNotMatch(source, /No governing agreement/);
	assert.doesNotMatch(source, /\bLifecycle\b/);
	assert.doesNotMatch(source, /\brelationship\b/i);
	assert.doesNotMatch(labelsSource, /No governing agreement|\bLifecycle\b|\brelationship\b/i);
	assert.match(source, /title: 'Status'/);
	assert.match(source, /title: 'Lease'/);
	assert.match(source, /leaseAgreementStatusLabel/);
	assert.match(source, /leaseLifecycleLabel/);
});

test('lease list keeps missing, draft, signature, and signed lease states distinct', () => {
	const expectedLabels = new Map<string | null, string>([
		[null, 'No signed lease yet'],
		['Draft', 'Draft lease'],
		['AwaitingSignatures', 'Waiting for signatures'],
		['Upcoming', 'Signed lease starts soon'],
		['Active', 'Signed lease'],
		['Expired', 'Lease ended'],
		['Superseded', 'Replaced lease'],
		['Void', 'Voided lease'],
		['Canceled', 'Canceled draft'],
		['MissingEvidence', 'Lease record needs review']
	]);

	for (const [status, label] of expectedLabels) {
		assert.equal(leaseAgreementStatusLabel(status), label, `label for ${status ?? 'missing evidence'}`);
	}

	assert.equal(new Set(expectedLabels.values()).size, expectedLabels.size);
	assert.notEqual(leaseAgreementStatusLabel('Draft'), leaseAgreementStatusLabel('Active'));
});

test('lease list keeps API lifecycle values while showing plain labels', () => {
	const expectedLabels = new Map([
		['Preparing', 'Preparing move-in'],
		['Upcoming', 'Starting soon'],
		['Occupied', 'Occupied'],
		['Ending', 'Ending soon'],
		['AccountingCloseout', 'Closing out'],
		['Closed', 'Closed'],
		['Canceled', 'Canceled']
	]);

	for (const [status, label] of expectedLabels) {
		assert.equal(leaseLifecycleLabel(status), label, `label for ${status}`);
		assert.match(source, new RegExp(`value="${status}"`));
	}
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
