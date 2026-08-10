import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const pageSource = readFileSync(
	new URL('../../../routes/(protected)/accounting/past-due/+page.svelte', import.meta.url),
	'utf8'
);
const dialogSource = readFileSync(
		new URL('../../components/accounting/PastDuePaymentDialog.svelte', import.meta.url),
		'utf8'
);
const previewSource = readFileSync(new URL('../../accounting/past-due-preview.ts', import.meta.url), 'utf8');
const typeSource = readFileSync(new URL('../../types/index.ts', import.meta.url), 'utf8');

describe('past-due partial receipt contract', () => {
	it('exposes a server-owned total open balance and explicit oldest-first receipt mode', () => {
		assert.match(typeSource, /interface PastDueLease[\s\S]*totalOpenBalance: number;/);
		assert.match(pageSource, /amount: data\.amount/);
		assert.match(pageSource, /allocateOldestCharges: true/);
	});

	it('renders the editable amount, visible validation, and server-ordered allocation preview', () => {
		assert.match(dialogSource, /past-due-mark-paid-amount-input/);
		assert.match(dialogSource, /past-due-mark-paid-amount-error/);
		assert.match(dialogSource, /past-due-allocation-preview/);
		assert.match(pageSource, /loadAllOpenCharges\(tenantAccountId as number\)/);
		assert.match(previewSource, /openOnly: true/);
		assert.match(previewSource, /sort: 'oldestDueOn'/);
	});
});
