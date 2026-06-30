import { test } from 'node:test';
import assert from 'node:assert/strict';

import { buildOnboardingPropertyPayload, onboardingPropertyFormFromProperty } from './property-payload.ts';

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
			selectedOwnerId: '',
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
			selectedOwnerId: '',
			createdOwner: { id: 7 },
			existingOwners: [{ id: 1 }]
		}).ownerEntityId,
		'7'
	);
});

test('selected existing owner is used for the property payload', () => {
	assert.equal(
		buildOnboardingPropertyPayload({
			propertyForm,
			selectedOwnerId: '3',
			createdOwner: { id: 7 },
			existingOwners: [{ id: 1 }, { id: 3 }]
		}).ownerEntityId,
		'3'
	);
});

test('selected property fills the editable onboarding property fields', () => {
	assert.deepEqual(
		onboardingPropertyFormFromProperty({
			id: 11,
			name: 'Clintonville Townhome',
			type: 'Townhome',
			addressLine1: '88 Maple Ave',
			addressLine2: 'Unit Main',
			city: 'Columbus',
			state: 'OH',
			postalCode: '43201'
		}),
		{
			name: 'Clintonville Townhome',
			type: 'Townhome',
			addressLine1: '88 Maple Ave',
			addressLine2: 'Unit Main',
			city: 'Columbus',
			state: 'OH',
			postalCode: '43201'
		}
	);
});
