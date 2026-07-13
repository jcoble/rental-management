import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpointSource = readFileSync(new URL('./tenant-accounts.ts', import.meta.url), 'utf8');
const paymentSource = readFileSync(new URL('./payments.ts', import.meta.url), 'utf8');
const scanReviewSource = readFileSync(
	new URL('../../../routes/(protected)/scan/[draftId]/+page.svelte', import.meta.url),
	'utf8'
);

describe('staff tenant-account read contract', () => {
	it('uses server-paged canonical account and ledger routes', () => {
		assert.match(endpointSource, /\/tenant-accounts\/page/);
		assert.match(endpointSource, /\/tenant-accounts\/\$\{tenantAccountId\}`/);
		assert.match(endpointSource, /\/tenant-accounts\/entries\/page/);
		assert.match(endpointSource, /\/tenant-accounts\/\$\{tenantAccountId\}\/entries\/\$\{tenantLedgerEntryId\}/);
	});

	it('keeps the payments endpoint module command-only', () => {
		assert.doesNotMatch(paymentSource, /api\.get/);
		assert.doesNotMatch(paymentSource, /\/payments(?:\/|`|'|")/);
	});

	it('merges one exact contextual account into the current server page', () => {
		assert.match(scanReviewSource, /tenantAccounts\.get\(contextualTenantAccountId/);
		assert.match(scanReviewSource, /choices\.unshift\(contextualAccount\)/);
		assert.match(scanReviewSource, /retry: false/);
	});
});
