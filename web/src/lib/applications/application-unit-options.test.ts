import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	applicationUnitAvailabilityLabel,
	applicationUnitOptionLabel,
} from './application-unit-options.ts';

describe('public application unit option labels', () => {
	it('labels occupied units as currently occupied', () => {
		assert.equal(applicationUnitAvailabilityLabel('Occupied'), 'Currently occupied');
		assert.equal(
			applicationUnitOptionLabel({ unitNumber: '2B', status: 'Occupied' }),
			'Unit 2B - Currently occupied'
		);
	});

	it('labels non-occupied availability states', () => {
		assert.equal(applicationUnitAvailabilityLabel('Vacant'), 'Available');
		assert.equal(applicationUnitAvailabilityLabel('Reserved'), 'Reserved');
		assert.equal(applicationUnitAvailabilityLabel('Offline'), 'Unavailable');
		assert.equal(applicationUnitAvailabilityLabel(null), 'Availability unknown');
	});
});
