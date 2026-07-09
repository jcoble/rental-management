import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(
	new URL('../components/records/PaymentDetail.svelte', import.meta.url),
	'utf8'
);

describe('payment detail notice action', () => {
	it('opens the shared notice dialog scoped to the selected payment', () => {
		assert.match(source, /import TenantNoticeDialog from '\$lib\/components\/notices\/TenantNoticeDialog\.svelte';/);
		assert.match(source, /Notice \/ reminder/);
		assert.match(source, /function noticeTypeForPayment/);
		assert.match(source, /return 'RentReminder'/);
		assert.match(source, /return 'LateRentNotice'/);
		assert.match(source, /bind:open=\{showPaymentNoticeDialog\}/);
		assert.match(source, /leaseId=\{payment\.leaseId \?\? undefined\}/);
		assert.match(source, /paymentId=\{payment\.id\}/);
		assert.match(source, /initialNoticeType=\{selectedPaymentNoticeType\}/);
	});
});
