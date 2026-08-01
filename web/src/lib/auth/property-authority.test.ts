import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { hasAllPropertiesRentalsManageAuthority } from './property-authority.ts';
import type { AccessEnvelope } from '$lib/types/user';

function access({
	scopeKind,
	roleProfileKey = 'property-manager',
	capabilityKeys = ['rentals.manage'],
	activeExperience = 'Management',
}: {
	scopeKind: string;
	roleProfileKey?: string;
	capabilityKeys?: string[];
	activeExperience?: AccessEnvelope['selectedContext']['activeExperience'];
}): AccessEnvelope {
	return {
		identity: { userId: 7, displayName: 'Pat Manager', email: 'pat@example.com' },
		selectedContext: {
			accessContextId: 70,
			portfolioId: 1,
			workspaceName: 'Portfolio',
			accessRevision: 12,
			activeExperience,
		},
		defaultExperience: activeExperience,
		availableExperiences: [activeExperience],
		assignments: [
			{
				assignmentId: 7,
				roleProfileKey,
				roleProfileName: 'Property Manager',
				status: 'Active',
				scope: {
					kind: scopeKind,
					selectedPropertyCount: scopeKind === 'SelectedProperties' ? 4 : 0,
					selectedProperties: scopeKind === 'SelectedProperties'
						? [
								{ propertyId: 1, name: 'Cedar' },
								{ propertyId: 21, name: 'Maple' },
							]
						: [],
				},
			},
		],
		navigation: [{ experience: activeExperience, capabilityKeys }],
	};
}

describe('property all-properties authority', () => {
	it('rejects selected-property rental managers for property creation', () => {
		assert.equal(
			hasAllPropertiesRentalsManageAuthority(access({ scopeKind: 'SelectedProperties' })),
			false
		);
	});

	it('allows all-properties rental managers for property creation', () => {
		assert.equal(
			hasAllPropertiesRentalsManageAuthority(access({ scopeKind: 'AllProperties' })),
			true
		);
	});

	it('requires the active management rentals-manage capability', () => {
		assert.equal(
			hasAllPropertiesRentalsManageAuthority(access({
				scopeKind: 'AllProperties',
				capabilityKeys: ['rentals.read'],
			})),
			false
		);
	});
});
