import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
	capabilityKeysForExperience,
	userFromAccessEnvelope,
	type AccessEnvelope
} from '$lib/types/user';

const access: AccessEnvelope = {
	identity: { userId: 17, displayName: 'Morgan Manager', email: 'morgan@example.test' },
	selectedContext: {
		accessContextId: 42,
		portfolioId: 8,
		workspaceName: 'Lakeview Rentals',
		accessRevision: 9,
		activeExperience: 'Management'
	},
	defaultExperience: 'Management',
	availableExperiences: ['Management', 'Maintenance'],
	assignments: [],
	navigation: [
		{ experience: 'Management', capabilityKeys: ['rentals.read', 'money.balances.read'] },
		{ experience: 'Maintenance', capabilityKeys: ['maintenance.assigned-work.read'] }
	]
};

describe('canonical access envelope', () => {
	it('derives shell identity without recreating legacy roles', () => {
		const user = userFromAccessEnvelope(access);
		assert.equal(user.id, 17);
		assert.equal(user.portfolioId, 8);
		assert.deepEqual(user.roles, []);
	});

	it('keeps navigation capabilities isolated by active experience', () => {
		assert.deepEqual([...capabilityKeysForExperience(access, 'Management')], [
			'rentals.read',
			'money.balances.read'
		]);
		assert.deepEqual([...capabilityKeysForExperience(access, 'Maintenance')], [
			'maintenance.assigned-work.read'
		]);
	});
});
