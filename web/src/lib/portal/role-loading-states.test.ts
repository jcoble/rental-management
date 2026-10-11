import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const leasePage = readFileSync(
	new URL('../../routes/(portal)/portal/lease/+page.svelte', import.meta.url),
	'utf8'
);

describe('tenant role loading states', () => {
	test('distinguishes lease loading and failure from an empty account', () => {
		assert.match(leasePage, /LoadingState/);
		assert.match(leasePage, /portal-lease-loading/);
		assert.match(leasePage, /portal-lease-error/);
		assert.match(leasePage, /leasesQuery\.refetch\(\)/);
	});
});
