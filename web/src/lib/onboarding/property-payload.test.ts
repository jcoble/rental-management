import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
	buildOnboardingPropertyPayload,
	onboardingPropertyFormFromProperty,
	onboardingPropertyPrefillCandidate,
	onboardingPropertyRecordOptions
} from './property-payload.ts';

const propertyForm = {
	name: 'Maple Grove Duplex',
	type: 'MultiFamily',
	rentalStructure: 'MultiRental' as const,
	addressLine1: '1100 Maple Ave',
	addressLine2: '',
	city: 'Columbus',
	state: 'OH',
	postalCode: '43215',
	ownerEntityId: '1'
};

test('onboarding property payload supplies hidden status and canonical ownership', () => {
	assert.deepEqual(
		buildOnboardingPropertyPayload({
			propertyForm,
			selectedOwnerId: '',
			createdOwner: null,
			existingOwners: [{ id: 1 }]
		}),
		{
			name: propertyForm.name,
			type: propertyForm.type,
			rentalStructure: propertyForm.rentalStructure,
			addressLine1: propertyForm.addressLine1,
			addressLine2: propertyForm.addressLine2,
			city: propertyForm.city,
			state: propertyForm.state,
			postalCode: propertyForm.postalCode,
			status: 'Active',
			ownerships: [{ ownerEntityId: 1, ownershipSharePercent: 100 }],
			clearOwnership: false
		}
	);
});

test('current-session owner can prefill the property owner field', () => {
	assert.equal(
		buildOnboardingPropertyPayload({
			propertyForm: { ...propertyForm, ownerEntityId: '7' },
			selectedOwnerId: '',
			createdOwner: { id: 7 },
			existingOwners: [{ id: 1 }]
		}).ownerships[0]?.ownerEntityId,
		7
	);
});

test('selected property owner field is used for the property payload', () => {
	assert.equal(
		buildOnboardingPropertyPayload({
			propertyForm: { ...propertyForm, ownerEntityId: '3' },
			selectedOwnerId: '3',
			createdOwner: { id: 7 },
			existingOwners: [{ id: 1 }, { id: 3 }]
		}).ownerships[0]?.ownerEntityId,
		3
	);
});

test('property owner field can explicitly clear the owner assignment', () => {
	assert.deepEqual(
		buildOnboardingPropertyPayload({
			propertyForm: { ...propertyForm, ownerEntityId: '' },
			selectedOwnerId: '3',
			createdOwner: { id: 7 },
			existingOwners: [{ id: 1 }, { id: 3 }]
		}),
		{
			name: propertyForm.name,
			type: propertyForm.type,
			rentalStructure: propertyForm.rentalStructure,
			addressLine1: propertyForm.addressLine1,
			addressLine2: propertyForm.addressLine2,
			city: propertyForm.city,
			state: propertyForm.state,
			postalCode: propertyForm.postalCode,
			status: 'Active',
			ownerships: [],
			clearOwnership: true
		}
	);
});

test('property owner field wins over the owner-step default', () => {
	assert.equal(
		buildOnboardingPropertyPayload({
			propertyForm: { ...propertyForm, ownerEntityId: '11' },
			selectedOwnerId: '3',
			createdOwner: { id: 7 },
			existingOwners: [{ id: 1 }, { id: 3 }, { id: 11 }]
		}).ownerships[0]?.ownerEntityId,
		11
	);
});

test('selected property fills the editable onboarding property fields', () => {
	assert.deepEqual(
		onboardingPropertyFormFromProperty({
			id: 11,
			name: 'Clintonville Townhome',
			type: 'Townhome',
			rentalStructure: 'SingleRental',
			addressLine1: '88 Maple Ave',
			addressLine2: 'Unit Main',
			city: 'Columbus',
			state: 'OH',
			postalCode: '43201',
			ownerships: [{ ownerEntityId: 4 }]
		}),
		{
			name: 'Clintonville Townhome',
			type: 'Townhome',
			rentalStructure: 'SingleRental',
			addressLine1: '88 Maple Ave',
			addressLine2: 'Unit Main',
			city: 'Columbus',
			state: 'OH',
			postalCode: '43201',
			ownerEntityId: '4'
		}
	);
});

test('property record options include current-session properties before the query refreshes', () => {
	assert.deepEqual(
		onboardingPropertyRecordOptions({
			createdProperty: { id: 11, name: 'Clintonville Townhome' },
			existingProperties: [
				{ id: 8, name: 'Eastland 8-Plex' },
				{ id: 9, name: 'Westview Four-Plex' }
			]
		}).map((property) => property.name),
		['Eastland 8-Plex', 'Westview Four-Plex', 'Clintonville Townhome']
	);
});

test('property record options do not duplicate a created property after the query refreshes', () => {
	assert.deepEqual(
		onboardingPropertyRecordOptions({
			createdProperty: { id: 9, name: 'Westview Four-Plex' },
			existingProperties: [
				{ id: 8, name: 'Eastland 8-Plex' },
				{ id: 9, name: 'Westview Four-Plex' }
			]
		}).map((property) => property.id),
		[8, 9]
	);
});

test('property prefill chooses the first available property before the user touches selection', () => {
	assert.deepEqual(
		onboardingPropertyPrefillCandidate({
			createdProperty: null,
			propertyRecordOptions: [
				{ id: 8, name: 'Eastland 8-Plex' },
				{ id: 9, name: 'Westview Four-Plex' }
			],
			selectionTouched: false
		}),
		{ id: 8, name: 'Eastland 8-Plex' }
	);
});

test('property prefill preserves an explicit new-property selection while options load', () => {
	assert.equal(
		onboardingPropertyPrefillCandidate({
			createdProperty: null,
			propertyRecordOptions: [
				{ id: 8, name: 'Eastland 8-Plex' },
				{ id: 9, name: 'Westview Four-Plex' }
			],
			selectionTouched: true
		}),
		null
	);
});
