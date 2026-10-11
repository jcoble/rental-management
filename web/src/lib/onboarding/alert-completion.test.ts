import assert from 'node:assert/strict';
import { test } from 'node:test';
import { isPersonalAlertSetupComplete } from './alert-completion.ts';

test('personal alerts are complete only when the durable email preference is enabled', () => {
	assert.equal(isPersonalAlertSetupComplete({ enableEmail: true }), true);
	assert.equal(isPersonalAlertSetupComplete({ enableEmail: false }), false);
	assert.equal(isPersonalAlertSetupComplete({ hasNotificationEmail: true }), true);
	assert.equal(isPersonalAlertSetupComplete(false), false);
});
