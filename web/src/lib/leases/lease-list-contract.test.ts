import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const source = readFileSync(
	new URL('../../routes/(protected)/leases/+page.svelte', import.meta.url),
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
