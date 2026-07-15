import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { CAPABILITY } from '../auth/experience-policy.ts';
import { canUseUnstructuredVoiceCapture, scanDocumentTypesForCapabilities } from './scan-access.ts';

describe('role-aware scanning', () => {
	it('keeps the complete scan-first workflow for a rental manager', () => {
		const types = scanDocumentTypesForCapabilities(new Set([
			CAPABILITY.rentalsManage,
			CAPABILITY.moneyExpensesManage,
			CAPABILITY.moneyPaymentsManage,
			CAPABILITY.workManage,
			CAPABILITY.leasingApplicationsManage
		]));
		assert.deepEqual(types, ['Expense', 'Payment', 'WorkOrder', 'LeaseAgreement', 'Application', 'Loan']);
		assert.equal(canUseUnstructuredVoiceCapture(types), true);
	});

	it('offers leasing commands without exposing money or maintenance document commands', () => {
		const types = scanDocumentTypesForCapabilities(new Set([
			CAPABILITY.leasingAgreementsPrepare,
			CAPABILITY.leasingApplicationsManage
		]));
		assert.deepEqual(types, ['LeaseAgreement', 'Application']);
		assert.equal(canUseUnstructuredVoiceCapture(types), false);
	});

	it('limits a technician to an assigned-work draft', () => {
		assert.deepEqual(
			scanDocumentTypesForCapabilities(new Set([CAPABILITY.assignedWorkUpdate])),
			['WorkOrder']
		);
	});

	it('does not turn rental editing into unrelated money or work commands', () => {
		assert.deepEqual(
			scanDocumentTypesForCapabilities(new Set([CAPABILITY.rentalsManage])),
			['LeaseAgreement']
		);
	});

	it('fails closed when no scan command capability is present', () => {
		assert.deepEqual(scanDocumentTypesForCapabilities(new Set()), []);
	});
});
