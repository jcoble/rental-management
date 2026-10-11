import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	defaultWorkOrderReceiptScanContext,
	UNIT_MAINTENANCE_SUBJECTS,
	unitMaintenanceReturnTo,
	workOrderReceiptScanContext,
} from './maintenance-actions.ts';

describe('unit maintenance actions', () => {
	it('builds job-scoped receipt scan context', () => {
		assert.deepEqual(workOrderReceiptScanContext(7, 42), {
			type: 'Expense',
			workOrderId: 42,
			returnTo: '/units/7?tab=maintenance&view=work-orders',
		});
	});

	it('only defaults the shared receipt action to a work order when unambiguous', () => {
		assert.deepEqual(defaultWorkOrderReceiptScanContext(7, [42]), {
			type: 'Expense',
			workOrderId: 42,
			returnTo: '/units/7?tab=maintenance&view=work-orders',
		});
		assert.deepEqual(defaultWorkOrderReceiptScanContext(7, [42, 43]), {
			type: 'Expense',
			workOrderId: undefined,
			returnTo: '/units/7?tab=maintenance&view=work-orders',
		});
	});

	it('returns the unit maintenance tab return target', () => {
		assert.equal(unitMaintenanceReturnTo(7), '/units/7?tab=maintenance&view=work-orders');
		assert.equal(unitMaintenanceReturnTo(7, 'inspections'), '/units/7?tab=maintenance&view=inspections');
		assert.equal(unitMaintenanceReturnTo(7, 'recurring'), '/units/7?tab=maintenance&view=recurring');
		assert.equal(unitMaintenanceReturnTo(7, 'turnover'), '/units/7?tab=maintenance&view=turnover');
		assert.deepEqual(
			UNIT_MAINTENANCE_SUBJECTS.map(({ label }) => label),
			['Repairs', 'Inspections', 'Recurring maintenance', 'Turnover/make-ready'],
		);
	});
});
