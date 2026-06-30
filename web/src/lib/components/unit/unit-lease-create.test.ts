import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	buildSimpleTenantPayload,
	createSimpleTenantForm,
	createUnitLeaseForm,
	validateSimpleTenantForm,
} from './unit-lease-create.ts';

const unitContext = {
	unit: {
		id: 23,
		propertyId: 7,
		unitNumber: '202',
		marketRent: 1600,
	},
	propertyName: '123 Main St',
};

describe('unit-scoped lease creation helpers', () => {
	it('locks new lease defaults to the current unit and property', () => {
		const form = createUnitLeaseForm(unitContext);

		assert.equal(form.propertyId, '7');
		assert.equal(form.unitId, '23');
		assert.equal(form.monthlyRent, '1600');
		assert.equal(form.status, 'Draft');
		assert.equal(form.rentTrackingStartMode, 'ForwardOnly');
		assert.equal(form.openingBalanceAmount, '');
		assert.equal(form.openingBalanceAsOfDate, '');
		assert.equal(form.openingBalanceNote, '');
	});

	it('requires the simple inline tenant fields and omits emergency contact', () => {
		const empty = createSimpleTenantForm();

		assert.deepEqual(validateSimpleTenantForm(empty), {
			firstName: 'First name is required',
			lastName: 'Last name is required',
			email: 'Email is required',
			phone: 'Phone is required',
		});

		const payload = buildSimpleTenantPayload(3, {
			firstName: ' Jesse ',
			lastName: ' Coble ',
			email: ' jesse@example.com ',
			phone: ' 330-555-1212 ',
		});

		assert.deepEqual(payload, {
			portfolioId: 3,
			firstName: 'Jesse',
			lastName: 'Coble',
			email: 'jesse@example.com',
			phone: '330-555-1212',
		});
		assert.equal(Object.hasOwn(payload, 'emergencyContact'), false);
	});
});
