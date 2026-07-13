import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpointSource = readFileSync(new URL('./securityDeposits.ts', import.meta.url), 'utf8');
const listPageSource = readFileSync(
	new URL('../../../routes/(protected)/deposits/+page.svelte', import.meta.url),
	'utf8'
);
const detailPageSource = readFileSync(
	new URL('../../../routes/(protected)/deposits/[id]/+page.svelte', import.meta.url),
	'utf8'
);

describe('security deposit tenant-account read contract', () => {
	it('uses only canonical account-scoped list, detail, and statement reads', () => {
		assert.match(endpointSource, /\/tenant-accounts\/deposits\/page/);
		assert.match(endpointSource, /\/tenant-accounts\/\$\{tenantAccountId\}\/deposit`/);
		assert.match(
			endpointSource,
			/\/tenant-accounts\/\$\{tenantAccountId\}\/deposit\/move-out-statement/
		);
		assert.doesNotMatch(endpointSource, /\/security-deposits/);
		assert.doesNotMatch(endpointSource, /leaseManagementId:\s*params/);
	});

	it('navigates by tenant account while documents stay keyed by deposit account', () => {
		assert.match(listPageSource, /\/deposits\/\$\{account\.tenantAccountId\}/);
		assert.match(detailPageSource, /securityDeposits\.get\(tenantAccountId\)/);
		assert.match(detailPageSource, /documents\.list\(ENTITY_TYPE, securityDepositAccountId\)/);
		assert.match(
			detailPageSource,
			/documents\.upload\(ENTITY_TYPE, securityDepositAccountId, file/
		);
		assert.doesNotMatch(detailPageSource, /deposit\.id/);
	});

	it('sends canonical database sort fields from the server-side grid', () => {
		assert.match(listPageSource, /key: 'propertyName'/);
		assert.match(listPageSource, /key: 'heldBalance'/);
		assert.doesNotMatch(listPageSource, /key: 'account'/);
		assert.doesNotMatch(listPageSource, /key: 'amount'/);
	});
});
