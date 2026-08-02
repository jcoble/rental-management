import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./tenant-money.ts', import.meta.url), 'utf8');

describe('tenant money API contract', () => {
	it('binds receipt, category-aware charge, targeted credit, adjustment, and correction bodies', () => {
		for (const field of [
			'paymentMethodSummary',
			'targetChargeEntryId',
			'allocateOldestCharges',
			'incomeLedgerAccountId',
			'servicePeriodStartOn',
			'servicePeriodEndOn',
			'direction',
			'paymentEntryId',
			'externalReference'
		]) {
			assert.match(source, new RegExp(`\\b${field}\\b`));
		}
		assert.match(source, /direction: TenantLedgerDirection/);
		assert.match(source, /targetChargeEntryId\?: number \| null/);
		assert.match(source, /incomeLedgerAccountId\?: number \| null/);
	});

	it('uses the canonical tenant-account routes and forwards an idempotency key on every write', () => {
		for (const route of [
			'/receipts',
			'/charges',
			'/charges/\\$\\{chargeEntryId\\}/reversals',
			'/credits',
			'/adjustments',
			'/reversals',
			'/refunds'
		]) {
			assert.match(source, new RegExp(`tenant-accounts/\\$\\{tenantAccountId\\}${route}`));
		}
		assert.match(source, /function mutationOptions\(operationKey: string\)/);
		assert.match(source, /'Idempotency-Key': operationKey/);
		for (const mutation of [
			'recordReceipt',
			'postCharge',
			'reverseCharge',
			'postCredit',
			'postAdjustment',
			'reverseLedgerEntry',
			'refundPayment'
		]) {
			assert.match(source, new RegExp(`${mutation}:[\\s\\S]*mutationOptions\\(operationKey\\)`));
		}
		assert.match(source, /TenantMoneyCommandResponse<RecordTenantReceiptResult>/);
		assert.match(source, /TenantMoneyCommandResponse<TenantPaymentRefundResult>/);
	});
});
