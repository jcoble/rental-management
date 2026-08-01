import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const page = readFileSync(new URL('./+page.svelte', import.meta.url), 'utf8');
const api = readFileSync(new URL('../../../../lib/api/endpoints/portal.ts', import.meta.url), 'utf8');

describe('tenant account history contract', () => {
	test('uses one canonical history response and keeps checkout charge-specific', () => {
		assert.match(page, /portal\.tenantAccountHistory/);
		assert.doesNotMatch(page, /tenantAccountChargesPage/);
		assert.match(api, /\/history\$\{queryString\(params\)\}/);
		assert.match(api, /\/charges\/\$\{chargeLedgerEntryId\}\/checkout/);
	});

	test('renders current due, period, beginning, rows, closing, focus, and contextual actions', () => {
		for (const contract of [
			'portal-current-due',
			'portal-history-period',
			'portal-beginning-balance',
			'portal-history-row',
			'portal-closing-balance',
			'data-focused={entry.isFocused}',
			'scrollIntoView',
			'portal-payment-pay-now',
			'portal-autopay-enroll'
		]) {
			assert.ok(page.includes(contract), `missing ${contract}`);
		}
		assert.match(page, /Held security deposits are not included\./);
		assert.doesNotMatch(page, /\.sort\(/);
		assert.doesNotMatch(page, /\.filter\(/);
	});

	test('uses one row model for desktop columns and narrow stacked labels', () => {
		assert.match(page, /md:grid-cols-\[8rem_minmax\(0,1fr\)_9rem_9rem_7rem\]/);
		assert.match(page, /md:hidden/);
		assert.match(page, /entry\.signedAmount/);
		assert.match(page, /entry\.runningBalance/);
		assert.match(page, /entry\.displayType/);
	});
});
