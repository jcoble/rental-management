import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	buildCreateTeamMemberBody,
	inviteMemberSubmitDisabled,
	type InviteMemberForm,
} from './invite-member-form.ts';

const baseForm: InviteMemberForm = {
	email: 'manager@example.local',
	displayName: 'Team Manager',
	role: 'Manager',
	temporaryPassword: '',
};

describe('admin user invite form', () => {
	it('builds a create body from the trimmed form fields', () => {
		assert.deepEqual(buildCreateTeamMemberBody(baseForm), {
			email: 'manager@example.local',
			displayName: 'Team Manager',
			role: 'Manager',
		});
	});

	it('omits a blank display name and temporary password', () => {
		assert.deepEqual(
			buildCreateTeamMemberBody({ ...baseForm, displayName: '   ', temporaryPassword: '  ' }),
			{ email: 'manager@example.local', role: 'Manager' }
		);
	});

	it('includes a supplied temporary password', () => {
		assert.deepEqual(buildCreateTeamMemberBody({ ...baseForm, temporaryPassword: 'Secret123!' }), {
			email: 'manager@example.local',
			displayName: 'Team Manager',
			role: 'Manager',
			temporaryPassword: 'Secret123!',
		});
	});

	it('requires an email before submitting', () => {
		assert.equal(inviteMemberSubmitDisabled({ ...baseForm, email: '' }, false), true);
		assert.equal(inviteMemberSubmitDisabled(baseForm, false), false);
	});

	it('is disabled while a submit is pending', () => {
		assert.equal(inviteMemberSubmitDisabled(baseForm, true), true);
	});
});
