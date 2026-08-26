import assert from 'node:assert/strict';
import { test } from 'node:test';
import { defaultReplyChannels, notificationSections } from './notification-sections.ts';

test('the notifications page hides "Who gets told what" until there is a team member', () => {
	assert.deepEqual(notificationSections({ hasTeamMembers: false }), ['my-alerts', 'tenant-notices']);
	assert.deepEqual(notificationSections({ hasTeamMembers: true }), [
		'my-alerts',
		'tenant-notices',
		'team-routing'
	]);
});

test('a reply repeats how the last reply was delivered', () => {
	assert.deepEqual(defaultReplyChannels({ hasPhone: false, lastChannels: ['Portal', 'Email'] }), {
		portal: true,
		email: true,
		sms: false
	});
	assert.deepEqual(defaultReplyChannels({ hasPhone: true, lastChannels: ['Sms'] }), {
		portal: false,
		email: false,
		sms: true
	});
});

test('a first reply goes to the tenant app, and by text when we have a mobile number', () => {
	assert.deepEqual(defaultReplyChannels({ hasPhone: true }), {
		portal: true,
		email: false,
		sms: true
	});
	assert.deepEqual(defaultReplyChannels({ hasPhone: false, lastChannels: [] }), {
		portal: true,
		email: false,
		sms: false
	});
	assert.deepEqual(defaultReplyChannels({ hasPhone: false, lastChannels: ['Carrier-Pigeon'] }), {
		portal: true,
		email: false,
		sms: false
	});
});
