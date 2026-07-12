import { describe, expect, it } from 'vitest';
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
		expect(userFromAccessEnvelope(access)).toMatchObject({
			id: 17,
			portfolioId: 8,
			roles: []
		});
	});

	it('keeps navigation capabilities isolated by active experience', () => {
		expect([...capabilityKeysForExperience(access, 'Management')]).toEqual([
			'rentals.read',
			'money.balances.read'
		]);
		expect([...capabilityKeysForExperience(access, 'Maintenance')]).toEqual([
			'maintenance.assigned-work.read'
		]);
	});
});
