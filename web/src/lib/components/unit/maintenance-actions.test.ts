import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	defaultWorkOrderReceiptScanContext,
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
	});
});
