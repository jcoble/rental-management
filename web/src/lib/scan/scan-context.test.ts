import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	appendScanContext,
	applyScanContextOverrides,
	parseScanContext,
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
});
