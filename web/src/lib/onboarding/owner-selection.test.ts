import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
	onboardingOwnerFormFromOwner,
	onboardingOwnerRecordOptions,
	ownerEntityIdForOnboarding
} from './owner-selection.ts';

test('uses the owner created during the current onboarding session first', () => {
	assert.equal(
		ownerEntityIdForOnboarding({
			selectedOwnerId: '',
			createdOwner: { id: 9 },
			existingOwners: [{ id: 1 }]
		}),
		'9'
	);
});

test('falls back to the first existing owner when onboarding resumes after refresh', () => {
	assert.equal(
		ownerEntityIdForOnboarding({
			selectedOwnerId: '',
			createdOwner: null,
			existingOwners: [{ id: 1 }]
		}),
		'1'
	);
});

test('returns empty when no owner exists yet', () => {
	assert.equal(
		ownerEntityIdForOnboarding({
			selectedOwnerId: '',
			createdOwner: null,
			existingOwners: []
		}),
		''
	);
});

test('selected existing owner wins over the current-session owner', () => {
	assert.equal(
		ownerEntityIdForOnboarding({
			selectedOwnerId: '3',
			createdOwner: { id: 9 },
			existingOwners: [{ id: 1 }, { id: 3 }]
		}),
		'3'
	);
});

test('selected owner fills the editable onboarding owner fields', () => {
	assert.deepEqual(
		onboardingOwnerFormFromOwner({
			id: 3,
			name: 'Maple River Holdings',
			ownerEntityType: 'LLC',
			email: 'books@mapleriver.example',
			taxId: '12-3456789'
		}),
		{
			name: 'Maple River Holdings',
			ownerEntityType: 'LLC',
			email: 'books@mapleriver.example',
			taxId: '12-3456789'
		}
	);
});

test('owner record options include current-session owners before the query refreshes', () => {
	assert.deepEqual(
		onboardingOwnerRecordOptions({
			createdOwner: { id: 7, name: 'Coble Holdings LLC' },
			existingOwners: [
				{ id: 1, name: 'Emily Chen' },
				{ id: 2, name: 'Maple River Holdings' }
			]
		}).map((owner) => owner.name),
		['Emily Chen', 'Maple River Holdings', 'Coble Holdings LLC']
	);
});

test('owner record options do not duplicate a created owner after the query refreshes', () => {
	assert.deepEqual(
		onboardingOwnerRecordOptions({
			createdOwner: { id: 2, name: 'Maple River Holdings' },
			existingOwners: [
				{ id: 1, name: 'Emily Chen' },
				{ id: 2, name: 'Maple River Holdings' }
			]
		}).map((owner) => owner.id),
		[1, 2]
	);
});
