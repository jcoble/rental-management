import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { recordHref } from './record-href.ts';

describe('recordHref', () => {
	it('routes a unit-tied work order to the unit Maintenance tab', () => {
		assert.equal(recordHref('workOrder', { id: 12, unitId: 3 }), '/units/3?tab=maintenance&view=work-orders&wo=12');
	});
	it('routes a property-only work order (no unit) to the generic page', () => {
		assert.equal(recordHref('workOrder', { id: 12, unitId: null }), '/maintenance/12');
	});
	it('routes lease management / expense / payment / application to the right tab + param', () => {
		assert.equal(
			recordHref('leaseManagement', { id: 5, unitId: 3 }),
			'/units/3?tab=tenant-lease&view=agreements&leaseManagement=5',
		);
		assert.equal(recordHref('expense', { id: 7, unitId: 3 }), '/units/3?tab=money&view=operating-costs&expense=7');
		assert.equal(recordHref('payment', { id: 9, unitId: 3 }), '/units/3?tab=money&view=tenant-account&payment=9');
		assert.equal(recordHref('application', { id: 2, unitId: 3 }), '/units/3?tab=leasing&view=applications&app=2');
	});
	it('falls back to the generic page when unitId is missing/0', () => {
		assert.equal(recordHref('expense', { id: 7, unitId: 0 }), '/accounting/expenses/7');
		assert.equal(recordHref('payment', { id: 9, tenantAccountId: 7 }), '/tenant-accounts/7/entries/9');
		assert.equal(recordHref('payment', { id: 9 }), '/accounting');
		assert.equal(recordHref('application', { id: 2, unitId: null }), '/applications/2');
	});
	it('prefers the Unit Command Center when account and unit context are both present', () => {
		assert.equal(recordHref('payment', { id: 9, unitId: 3, tenantAccountId: 7 }), '/units/3?tab=money&view=tenant-account&payment=9');
	});
});
