import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

describe('portal payments page', () => {
	it('uses autopay status to show unavailable online payments before setup POSTs', () => {
		const pageSource = readFileSync(
			new URL('../../routes/(portal)/portal/payments/+page.svelte', import.meta.url),
			'utf8'
		);
		const endpointSource = readFileSync(
			new URL('../api/endpoints/portal.ts', import.meta.url),
			'utf8'
		);

		assert.match(endpointSource, /onlinePaymentsAvailable: boolean/);
		assert.match(pageSource, /autopayQuery\.data\?\.onlinePaymentsAvailable === false/);
		assert.match(pageSource, /data-testid="portal-autopay-unavailable"/);
		assert.match(pageSource, /disabled=\{onlinePaymentsUnavailable \|\| enrollMutation\.isPending \|\| leaseId == null\}/);
	});

	it('disables row-level pay now actions when online payments are unavailable', () => {
		const pageSource = readFileSync(
			new URL('../../routes/(portal)/portal/payments/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /data-testid="portal-payment-unavailable"/);
		assert.match(pageSource, /disabled=\{onlinePaymentsUnavailable \|\| \(payMutation\.isPending && payingId === payment\.id\)\}/);
		assert.match(pageSource, /onlinePaymentsUnavailable \? 'Pay unavailable' :/);
	});
});
