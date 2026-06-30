import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { buildOnboardingLeaseScanOverrides } from './lease-scan-confirm.ts';

describe('buildOnboardingLeaseScanOverrides', () => {
	it('sends the selected property, unit, tenant, and reviewed lease terms to scan confirmation', () => {
		const overrides = JSON.parse(
			buildOnboardingLeaseScanOverrides({
				leaseNumber: 'L-2026-001',
				propertyId: 12,
				unitId: 34,
				tenantId: 56,
				startDate: '2026-06-22',
				endDate: '2027-06-22',
				monthlyRent: 975,
				securityDeposit: 975,
				lateFeeAmount: 50,
				rentDueDay: 1
			})
		);

		assert.deepEqual(overrides, {
			propertyId: 12,
			unitId: 34,
			tenantId: 56,
			leaseNumber: 'L-2026-001',
			startDate: '2026-06-22',
			endDate: '2027-06-22',
			monthlyRent: 975,
			securityDeposit: 975,
			lateFee: 50,
			rentDueDay: 1
		});
	});

	it('maps the form lateFeeAmount field to the scan API lateFee override', () => {
		const overrides = JSON.parse(
			buildOnboardingLeaseScanOverrides({
				leaseNumber: 'L-2026-002',
				propertyId: 1,
				unitId: 2,
				tenantId: 3,
				startDate: '2026-07-01',
				endDate: '2027-07-01',
				monthlyRent: 1200,
				securityDeposit: 1200,
				lateFeeAmount: 65,
				rentDueDay: 5
			})
		);

		assert.equal(overrides.lateFee, 65);
		assert.equal('lateFeeAmount' in overrides, false);
	});

	it('includes opening-balance import choices when supplied', () => {
		const overrides = JSON.parse(
			buildOnboardingLeaseScanOverrides({
				leaseNumber: 'L-2026-003',
				propertyId: 1,
				unitId: 2,
				tenantId: 3,
				startDate: '2026-01-01',
				endDate: '2026-12-31',
				monthlyRent: 1200,
				securityDeposit: 1200,
				lateFeeAmount: 75,
				rentDueDay: 1,
				rentTrackingStartMode: 'OpeningBalanceOnly',
				openingBalanceAmount: 2400,
				openingBalanceAsOfDate: '2026-06-30',
				openingBalanceNote: 'Imported current balance'
			})
		);

		assert.equal(overrides.rentTrackingStartMode, 'OpeningBalanceOnly');
		assert.equal(overrides.openingBalanceAmount, 2400);
		assert.equal(overrides.openingBalanceAsOfDate, '2026-06-30');
		assert.equal(overrides.openingBalanceNote, 'Imported current balance');
	});
});
