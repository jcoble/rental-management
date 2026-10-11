import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { AccessEnvelope, WorkspaceExperience } from '../types/user.ts';
import { accessEnvelopeAuthorityChanged } from './access-envelope-change.ts';

function envelope(overrides: {
	accessContextId?: number;
	portfolioId?: number;
	accessRevision?: number;
	activeExperience?: WorkspaceExperience;
	displayName?: string;
} = {}): AccessEnvelope {
	return {
		identity: {
			userId: 7,
			displayName: overrides.displayName ?? 'Morgan Manager',
			email: 'morgan@example.test'
		},
		selectedContext: {
			accessContextId: overrides.accessContextId ?? 11,
			portfolioId: overrides.portfolioId ?? 3,
			workspaceName: 'Lakeview Rentals',
			accessRevision: overrides.accessRevision ?? 5,
			activeExperience: overrides.activeExperience ?? 'Management'
		},
		defaultExperience: 'Management',
		availableExperiences: ['Management', 'Maintenance'],
		assignments: [],
		navigation: []
	};
}

describe('refresh access-envelope authority comparison', () => {
	it('does not purge for display-only envelope changes', () => {
		assert.equal(accessEnvelopeAuthorityChanged(envelope(), envelope({ displayName: 'Morgan M.' })), false);
	});

	it('detects context, portfolio, revision, and experience changes', () => {
		const previous = envelope();
		assert.equal(accessEnvelopeAuthorityChanged(previous, envelope({ accessContextId: 12 })), true);
		assert.equal(accessEnvelopeAuthorityChanged(previous, envelope({ portfolioId: 4 })), true);
		assert.equal(accessEnvelopeAuthorityChanged(previous, envelope({ accessRevision: 6 })), true);
		assert.equal(
			accessEnvelopeAuthorityChanged(previous, envelope({ activeExperience: 'Maintenance' })),
			true
		);
	});
});
