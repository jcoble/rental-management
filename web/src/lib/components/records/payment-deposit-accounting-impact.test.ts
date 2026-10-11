import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const paymentDetail = readFileSync(new URL('./PaymentDetail.svelte', import.meta.url), 'utf8');
const depositDetail = readFileSync(
	new URL('../../../routes/(protected)/deposits/[id]/+page.svelte', import.meta.url),
	'utf8'
);

describe('source-detail accounting impact contracts', () => {
	it('uses the entry-type accounting source identity below the tenant entry detail fields', () => {
		assert.match(
			paymentDetail,
			/import AccountingImpactCard from '\$lib\/components\/accounting\/AccountingImpactCard\.svelte';/
		);
		assert.match(
			paymentDetail,
			/<AccountingImpactCard sourceType=\{detailCopy\.sourceType\} sourceId=\{tenantLedgerEntryId\} \/>/
		);
		assert.ok(
			paymentDetail.indexOf('<AccountingImpactCard sourceType={detailCopy.sourceType}') >
				paymentDetail.indexOf('data-testid="payment-technical-details"')
		);
		assert.ok(
			paymentDetail.indexOf('<AccountingImpactCard sourceType={detailCopy.sourceType}') <
				paymentDetail.indexOf('data-testid="payment-history-section"')
		);
	});

	it('uses the security-deposit receipt source identity below the deposit detail fields', () => {
		assert.match(
			depositDetail,
			/import AccountingImpactCard from '\$lib\/components\/accounting\/AccountingImpactCard\.svelte';/
		);
		assert.match(
			depositDetail,
			/<AccountingImpactCard sourceType="SecurityDepositReceipt" sourceId=\{deposit\.securityDepositAccountId\} \/>/
		);
		assert.ok(
			depositDetail.indexOf('<AccountingImpactCard sourceType="SecurityDepositReceipt"') >
				depositDetail.indexOf('data-testid="deposit-technical-details"')
		);
		assert.ok(
			depositDetail.indexOf('<AccountingImpactCard sourceType="SecurityDepositReceipt"') <
				depositDetail.indexOf('data-testid="deposit-photos"')
		);
	});
});
