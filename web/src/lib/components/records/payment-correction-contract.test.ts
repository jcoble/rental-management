import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { describe, it } from 'node:test';
import {
	PAYMENT_CORRECTION_REASON,
	canCorrectPayment,
	paymentCorrectionContext
} from '../unit/money.ts';
import type {
	RefundTenantPaymentRequest,
	TenantMoneyCommandResponse,
	TenantPaymentRefundResult
} from '../../api/endpoints/tenant-money.ts';

const clientStub =
	'data:text/javascript,export const calls=[];export const api={get(){throw new Error("Unexpected API call")},post(path,body,options){calls.push({path,body,options})}};export function fetchApi(){throw new Error("Unexpected API call")}';

registerHooks({
	resolve(specifier, context, nextResolve) {
		if (
			specifier === '../client' &&
			context.parentURL?.endsWith('/api/endpoints/tenant-money.ts')
		) {
			return { url: clientStub, shortCircuit: true };
		}
		return nextResolve(specifier, context);
	}
});

const { calls } = (await import(clientStub)) as {
	calls: Array<{ path: string; body: RefundTenantPaymentRequest; options: RequestInit }>;
};
const { isTenantPaymentRefundConflict, linkedTenantPaymentRefund, tenantMoney } = await import(
	'../../api/endpoints/tenant-money.ts'
);

const originalReceipt = {
	tenantAccountId: 41,
	tenantLedgerEntryId: 812,
	accountNumber: 'TA-0041',
	propertyName: 'Maple Court',
	unitNumber: '2B',
	tenantName: '  Jordan Lee  ',
	amount: 1275,
	effectiveOn: '2026-07-01',
	description: 'July rent',
	providerAttempt: {
		paymentMethodSummary: '  Check 1042  ',
		providerReference: '  payout-778  '
	},
	sourceStoredFileId: 99
} as const;

function refundBody(): RefundTenantPaymentRequest {
	const correction = paymentCorrectionContext(originalReceipt);
	return {
		paymentEntryId: correction.tenantLedgerEntryId,
		effectiveOn: correction.effectiveOn,
		reason: correction.reason,
		paymentMethodSummary: correction.paymentMethodSummary,
		externalReference: correction.externalReference,
		sourceStoredFileId: correction.sourceStoredFileId
	};
}

describe('payment correction contract', () => {
	it('keeps original payment immutable', () => {
		const before = structuredClone(originalReceipt);
		const capabilities = new Set(['money.payments.manage']);
		tenantMoney.refundPayment(41, 'correction-812', refundBody());
		const request = calls.at(-1);
		assert.ok(request);

		assert.equal(canCorrectPayment(capabilities, 'PaymentReceipt'), true);
		assert.equal(canCorrectPayment(new Set(), 'PaymentReceipt'), false);
		assert.equal(canCorrectPayment(capabilities, 'Charge'), false);
		assert.equal(request.path, '/tenant-accounts/41/refunds');
		assert.equal(request.path.includes('reversals'), false);
		assert.deepEqual(originalReceipt, before);
		assert.equal(request.body.paymentEntryId, 812);
	});

	it('prefills correction context and reason', () => {
		const correction = paymentCorrectionContext(originalReceipt, '2027-02-28T00:00:00Z');

		assert.equal(PAYMENT_CORRECTION_REASON, 'Correction of original payment receipt');
		assert.deepEqual(correction, {
			tenantAccountId: 41,
			tenantLedgerEntryId: 812,
			accountNumber: 'TA-0041',
			propertyName: 'Maple Court',
			unitNumber: '2B',
			tenantName: 'Jordan Lee',
			amount: 1275,
			effectiveOn: '2027-02-28',
			reason: `${PAYMENT_CORRECTION_REASON}: July rent`,
			paymentMethodSummary: 'Check 1042',
			externalReference: 'payout-778',
			sourceStoredFileId: 99
		});
	});

	it('falls back to the local calendar date when no business date is supplied', () => {
		const correction = paymentCorrectionContext(originalReceipt);
		assert.match(correction.effectiveOn, /^\d{4}-\d{2}-\d{2}$/);
	});

	it('appends linked correction', () => {
		const body = refundBody();
		tenantMoney.refundPayment(41, 'correction-812', body);
		tenantMoney.refundPayment(41, 'correction-812', body);
		const [firstRequest, replayRequest] = calls.slice(-2);
		assert.ok(firstRequest);
		assert.ok(replayRequest);
		const linkedResult: TenantMoneyCommandResponse<TenantPaymentRefundResult> = {
			value: {
				found: true,
				applied: true,
				outcome: 'Refunded',
				tenantAccountId: 41,
				paymentEntryId: 812,
				refundEntryId: 913,
				providerPaymentAttemptId: null,
				amount: 1275,
				compensatedAllocationAmount: 1275,
				compensatedAllocationCount: 2,
				error: null
			},
			replayed: false
		};
		const replayedResult = { ...linkedResult, replayed: true };
		const conflict = {
			outcome: 'AlreadyRefunded',
			error: 'Payment receipt already has a linked refund.'
		};

		assert.deepEqual(firstRequest, replayRequest);
		assert.equal(new Headers(firstRequest.options.headers).get('Idempotency-Key'), 'correction-812');
		assert.deepEqual(linkedTenantPaymentRefund(linkedResult), {
			refundEntryId: 913,
			compensatedAllocationAmount: 1275,
			compensatedAllocationCount: 2,
			replayed: false
		});
		assert.deepEqual(linkedTenantPaymentRefund(replayedResult), {
			refundEntryId: 913,
			compensatedAllocationAmount: 1275,
			compensatedAllocationCount: 2,
			replayed: true
		});
		assert.equal(isTenantPaymentRefundConflict(conflict), true);
		assert.equal(
			linkedTenantPaymentRefund({
				value: {
					...linkedResult.value,
					applied: false,
					outcome: 'AlreadyRefunded',
					refundEntryId: null,
					compensatedAllocationAmount: 0,
					compensatedAllocationCount: 0
				},
				replayed: false
			}),
			null
		);
	});
});
