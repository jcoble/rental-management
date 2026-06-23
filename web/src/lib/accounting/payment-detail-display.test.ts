import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { formatPaymentMoney } from './payment-detail-display.ts';

describe('payment detail display', () => {
	it('formats payment amounts with separators and cents', () => {
		assert.equal(formatPaymentMoney(1275), '$1,275.00');
		assert.equal(formatPaymentMoney('1275.5'), '$1,275.50');
	});
});
