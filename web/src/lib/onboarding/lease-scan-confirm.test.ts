import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	buildOnboardingLeaseScanOverrides,
	buildOnboardingManualLeaseRequest,
} from './lease-scan-confirm.ts';

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
				rentDueDay: 1,
				reviewDisposition: 'AlreadyFullySigned',
				documentTemplateId: null
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
			rentDueDay: 1,
			reviewDisposition: 'AlreadyFullySigned'
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
				rentDueDay: 5,
				reviewDisposition: 'NeedsSignatures',
				documentTemplateId: 42
			})
		);

		assert.equal(overrides.lateFee, 65);
		assert.equal('lateFeeAmount' in overrides, false);
		assert.equal(overrides.reviewDisposition, 'NeedsSignatures');
		assert.equal(overrides.documentTemplateId, 42);
	});

	it('does not send a template id for an already executed agreement', () => {
		const overrides = JSON.parse(
			buildOnboardingLeaseScanOverrides({
				leaseNumber: 'L-2026-003',
				propertyId: 1,
				unitId: 2,
				tenantId: 3,
				startDate: '2026-07-01',
				endDate: '2027-07-01',
				monthlyRent: 1200,
				securityDeposit: 1200,
				lateFeeAmount: 65,
				rentDueDay: 5,
				reviewDisposition: 'AlreadyFullySigned',
				documentTemplateId: 42
			})
		);

		assert.equal(overrides.reviewDisposition, 'AlreadyFullySigned');
		assert.equal('documentTemplateId' in overrides, false);
	});

	it('maps manual Guided Setup terms to the canonical manual lease request', () => {
		assert.deepEqual(
			buildOnboardingManualLeaseRequest({
				leaseNumber: 'MANUAL-834',
				propertyId: 12,
				unitId: 34,
				tenantId: 56,
				startDate: '2026-09-01',
				endDate: '2027-08-31',
				monthlyRent: 1450,
				securityDeposit: 1450,
				lateFeeAmount: 75,
				rentDueDay: 1,
				reviewDisposition: 'NeedsSignatures',
				documentTemplateId: null,
			}),
			{
				propertyId: 12,
				unitId: 34,
				tenantId: 56,
				leaseNumber: 'MANUAL-834',
				startDate: '2026-09-01',
				endDate: '2027-08-31',
				monthlyRent: 1450,
				securityDeposit: 1450,
				lateFee: 75,
				rentDueDay: 1,
			}
		);
	});

});
