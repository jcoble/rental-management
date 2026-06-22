import { test } from 'node:test';
import assert from 'node:assert/strict';

import { ownerEntityIdForOnboarding } from './owner-selection.ts';

test('uses the owner created during the current onboarding session first', () => {
	assert.equal(
		ownerEntityIdForOnboarding({
			createdOwner: { id: 9 },
			existingOwners: [{ id: 1 }]
		}),
		'9'
	);
});

test('falls back to the first existing owner when onboarding resumes after refresh', () => {
	assert.equal(
		ownerEntityIdForOnboarding({
			createdOwner: null,
			existingOwners: [{ id: 1 }]
		}),
		'1'
	);
});

test('returns empty when no owner exists yet', () => {
	assert.equal(
		ownerEntityIdForOnboarding({
			createdOwner: null,
			existingOwners: []
		}),
		''
	);
});
