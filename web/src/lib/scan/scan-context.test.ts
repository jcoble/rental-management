import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	appendScanContext,
	applyScanContextOverrides,
	parseScanContext,
	resolvePaymentLeaseIdFromContext,
	scanHref
} from './scan-context.ts';

describe('scan context helpers', () => {
	it('builds and parses unit-scoped scan links', () => {
		const href = scanHref({
			type: 'Expense',
			propertyId: 10,
			unitId: 20,
			workOrderId: 30,
			returnTo: '/units/20?tab=maintenance'
		});

		assert.equal(
			href,
			'/scan?type=Expense&propertyId=10&unitId=20&workOrderId=30&returnTo=%2Funits%2F20%3Ftab%3Dmaintenance'
		);
		assert.deepEqual(parseScanContext(new URL(href, 'https://localhost').searchParams), {
			type: 'Expense',
			propertyId: 10,
			unitId: 20,
			workOrderId: 30,
			returnTo: '/units/20?tab=maintenance'
		});
	});

	it('preserves scan context when moving from upload to review', () => {
		assert.equal(
			appendScanContext('/scan/42', {
				type: 'WorkOrder',
				propertyId: 10,
				unitId: 20,
				returnTo: '/units/20?tab=maintenance'
			}),
			'/scan/42?type=WorkOrder&propertyId=10&unitId=20&returnTo=%2Funits%2F20%3Ftab%3Dmaintenance'
		);
	});

	it('applies expense and work-order context to confirm overrides without overwriting explicit property choices', () => {
		const expenseOverrides: Record<string, unknown> = { propertyId: 99, is_paid: true };
		applyScanContextOverrides(expenseOverrides, {
			propertyId: 10,
			unitId: 20,
			workOrderId: 30
		}, 'Expense');
		assert.deepEqual(expenseOverrides, {
			propertyId: 99,
			is_paid: true,
			unitId: 20,
			workOrderId: 30
		});

		const workOrderOverrides: Record<string, unknown> = {};
		applyScanContextOverrides(workOrderOverrides, { propertyId: 10, unitId: 20 }, 'WorkOrder');
		assert.deepEqual(workOrderOverrides, { propertyId: 10, unitId: 20 });
	});

	it('applies the deep-linked property to a loan confirm without overwriting an explicit choice', () => {
		// A loan attaches to the property the scan was launched from (deep-link propertyId).
		const fromContext: Record<string, unknown> = {};
		applyScanContextOverrides(fromContext, { propertyId: 10, unitId: 20 }, 'Loan');
		assert.deepEqual(fromContext, { propertyId: 10 });

		// An explicit property the reviewer already chose wins over the context fallback.
		const explicit: Record<string, unknown> = { propertyId: 99 };
		applyScanContextOverrides(explicit, { propertyId: 10 }, 'Loan');
		assert.deepEqual(explicit, { propertyId: 99 });
	});

	it('rejects unsafe return targets when parsing scan context', () => {
		assert.deepEqual(parseScanContext(new URLSearchParams('returnTo=https://evil.test/units/20')), {});
		assert.deepEqual(parseScanContext(new URLSearchParams('returnTo=//evil.test/units/20')), {});
		assert.deepEqual(parseScanContext(new URLSearchParams('returnTo=/units/20?tab=rent')), {
			returnTo: '/units/20?tab=rent'
		});
	});

	it('resolves payment lease selection from explicit lease or an unambiguous unit context', () => {
		const leases = [
			{ id: 1, unitId: 20, status: 'Expired' },
			{ id: 2, unitId: 20, status: 'Active' },
			{ id: 3, unitId: 30, status: 'Active' }
		];

		assert.equal(resolvePaymentLeaseIdFromContext({ leaseId: 3, unitId: 20 }, leases), 3);
		assert.equal(resolvePaymentLeaseIdFromContext({ unitId: 20 }, leases), 2);
		assert.equal(resolvePaymentLeaseIdFromContext({ unitId: 30 }, leases), 3);
		assert.equal(
			resolvePaymentLeaseIdFromContext({ unitId: 40 }, [
				{ id: 4, unitId: 40, status: 'Active' },
				{ id: 5, unitId: 40, status: 'Active' }
			]),
			undefined
		);
	});
});
