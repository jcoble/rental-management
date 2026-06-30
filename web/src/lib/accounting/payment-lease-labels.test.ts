import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { formatLeasePickerLabel, formatPaymentLeaseDisplay } from './payment-detail-display.ts';

describe('payment lease labels', () => {
	it('leads picker labels with property and unit instead of the internal lease number', () => {
		assert.equal(
			formatLeasePickerLabel({
				propertyName: 'Maple Court',
				unitNumber: '1',
				tenantName: 'Marcus Williams',
				leaseNumber: 'L-1'
			}),
			'Maple Court · Unit 1 — Marcus Williams · L-1'
		);
	});

	it('uses property and unit for payment read-only display', () => {
		assert.equal(
			formatPaymentLeaseDisplay({
				propertyName: 'Maple Court',
				unitNumber: '1',
				tenantName: 'Marcus Williams',
				leaseNumber: 'L-1'
			}),
			'Maple Court · Unit 1'
		);
	});
});
