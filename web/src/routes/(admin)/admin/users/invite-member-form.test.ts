import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	buildCreateTeamMemberBody,
	inviteMemberSubmitDisabled,
	type InviteMemberForm,
} from './invite-member-form.ts';

const baseForm: InviteMemberForm = {
	email: 'tenant.portal@example.local',
	displayName: 'Tenant Portal',
	role: 'Tenant',
	tenantId: '10',
	temporaryPassword: '',
};

describe('admin user invite form', () => {
	it('includes tenantId when creating a tenant portal user', () => {
		assert.deepEqual(buildCreateTeamMemberBody(baseForm), {
			email: 'tenant.portal@example.local',
			displayName: 'Tenant Portal',
			role: 'Tenant',
			tenantId: 10,
		});
	});

	it('requires a tenant selection before submitting a tenant portal user', () => {
		assert.equal(inviteMemberSubmitDisabled({ ...baseForm, tenantId: '' }, false), true);
		assert.equal(inviteMemberSubmitDisabled(baseForm, false), false);
	});

	it('does not submit stale tenant linkage for staff users', () => {
		assert.deepEqual(buildCreateTeamMemberBody({ ...baseForm, role: 'Manager' }), {
			email: 'tenant.portal@example.local',
			displayName: 'Tenant Portal',
			role: 'Manager',
		});
	});
});
