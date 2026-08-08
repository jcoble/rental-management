import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const detail = readFileSync(new URL('./PaymentDetail.svelte', import.meta.url), 'utf8');

describe('tenant account entry detail', () => {
	test('branches charge, payment, and credit records into correct detail copy and accounting sources', () => {
		assert.match(detail, /'RentCharge', 'AddendumCharge', 'LateFeeCharge', 'DepositCharge', 'ManualCharge'/);
		assert.match(detail, /title: 'Charge detail'/);
		assert.match(detail, /sourceType: 'TenantCharge'/);
		assert.match(detail, /title: 'Payment receipt'/);
		assert.match(detail, /sourceType: 'TenantReceipt'/);
		assert.match(detail, /title: 'Credit detail'/);
		assert.match(detail, /sourceType: 'TenantConcession'/);
		assert.match(detail, /<AccountingImpactCard sourceType=\{detailCopy\.sourceType\}/);
	});
});
