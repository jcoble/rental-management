import assert from 'node:assert/strict';
import { test } from 'node:test';
import { depositDetailHref } from './deposit-navigation.ts';

test('S18-POT-1 uses the tenant-account key understood by the deposit detail route', () => {
	assert.equal(
		depositDetailHref({ tenantAccountId: 42, securityDepositAccountId: 9001 }),
		'/deposits/42'
	);
});
