import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { buildPrepareMoveInRequest, createPrepareMoveInForm } from './prepare-move-in-form.ts';

function completeForm() {
	return {
		...createPrepareMoveInForm({
			applicationId: '17',
			unitId: '9',
			tenantId: '42'
		}),
		plannedPossessionOn: '2026-09-01',
		partyEffectiveFrom: '2026-09-01',
		documentTemplateId: '8',
		termStartOn: '2026-09-01',
		termEndOn: '2027-08-31',
		baseRentAmount: '1875.50',
		rentDueDay: '3',
		securityDepositObligation: '1875.50',
		lateFeeAmount: '65',
		gracePeriodDays: '5',
		createSecurityDepositAccount: true,
		openingBalanceAmount: '240.25',
		openingBalanceEffectiveOn: '2026-08-31',
		openingBalanceNote: 'Signed carry-forward balance'
	};
}

describe('Prepare move-in form contract', () => {
	it('builds the complete atomic request with precise canonical identifiers', () => {
		const result = buildPrepareMoveInRequest(completeForm());

		assert.deepEqual(result.errors, {});
		assert.deepEqual(result.request, {
			applicationId: 17,
			unitId: 9,
			plannedPossessionAtUtc: '2026-09-01T00:00:00.000Z',
			partyEffectiveFrom: '2026-09-01',
			parties: [
				{
					tenantId: 42,
					role: 'PrimaryTenant',
					guarantorLegalNoticeEligible: false,
					changeReason: 'Approved application move-in',
					isAgreementSigner: true,
					signingOrder: 1,
					isRequiredSigner: true
				}
			],
			documentTemplateId: 8,
			termType: 'FixedTerm',
			termStartOn: '2026-09-01',
			termEndOn: '2027-08-31',
			baseRentAmount: 1875.5,
			rentDueDay: 3,
			securityDepositObligation: 1875.5,
			lateFeeAmount: 65,
			gracePeriodDays: 5,
			termsSchemaVersion: 1,
			termsPayload: {},
			createSecurityDepositAccount: true,
			openingBalanceAmount: 240.25,
			openingBalanceEffectiveOn: '2026-08-31',
			openingBalanceNote: 'Signed carry-forward balance'
		});
	});

	it('omits the term end and optional money for month-to-month preparation', () => {
		const form = completeForm();
		form.termType = 'MonthToMonth';
		form.openingBalanceAmount = '';
		form.openingBalanceEffectiveOn = '';
		form.openingBalanceNote = '';

		const result = buildPrepareMoveInRequest(form);

		assert.equal(result.request?.termEndOn, null);
		assert.equal(result.request?.openingBalanceAmount, null);
		assert.equal(result.request?.openingBalanceEffectiveOn, null);
	});

	it('rejects incomplete dates and half-specified opening balances', () => {
		const form = completeForm();
		form.termEndOn = '2026-08-31';
		form.openingBalanceEffectiveOn = '';

		const result = buildPrepareMoveInRequest(form);

		assert.equal(result.request, null);
		assert.match(result.errors.termEndOn ?? '', /cannot be before/);
		assert.match(result.errors.openingBalanceEffectiveOn ?? '', /together/);
	});
});
