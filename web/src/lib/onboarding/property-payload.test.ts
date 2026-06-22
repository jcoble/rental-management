import { test } from 'node:test';
import assert from 'node:assert/strict';

import { buildOnboardingPropertyPayload } from './property-payload.ts';

const propertyForm = {
	name: 'Maple Grove Duplex',
	type: 'MultiFamily',
	addressLine1: '1100 Maple Ave',
	addressLine2: '',
	city: 'Columbus',
	state: 'OH',
	postalCode: '43215'
};

test('onboarding property payload supplies hidden status and persisted owner id', () => {
	assert.deepEqual(
		buildOnboardingPropertyPayload({
			propertyForm,
			createdOwner: null,
			existingOwners: [{ id: 1 }]
		}),
		{
			...propertyForm,
			status: 'Active',
			ownerEntityId: '1'
		}
	);
});

test('current-session owner wins over the persisted owner list', () => {
	assert.equal(
		buildOnboardingPropertyPayload({
			propertyForm,
			createdOwner: { id: 7 },
			existingOwners: [{ id: 1 }]
		}).ownerEntityId,
		'7'
	);
});
