import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const source = readFileSync('src/routes/(protected)/settings/+page.svelte', 'utf8');

test('notification settings exposes the full staff channel matrix including mobile push', () => {
	assert.match(source, /const channelColumns/);
	assert.match(source, /enablePush/);
	assert.match(source, /Mobile push/);
	assert.match(source, /testSuffix: 'push'/);
	assert.match(source, /settings-channel-\$\{row\.type\}-\$\{column\.testSuffix\}/);
	assert.doesNotMatch(source, /enablePush is round-tripped \(not yet shown in this UI\)/);
});

test('notification settings makes tenant delivery explicit for tenant-facing automations', () => {
	assert.match(source, /tenantFacingNotificationTypes/);
	assert.match(source, /'RentCharge'/);
	assert.match(source, /'LateFee'/);
	assert.match(source, /settings-tenant-delivery-toggle/);
	assert.match(source, /settings-audience-row-\$\{row\.type\}-staff/);
	assert.match(source, /settings-audience-row-\$\{row\.type\}-tenant/);
});

test('notification settings copy distinguishes mobile app push from unsupported browser web push', () => {
	assert.match(source, /registered mobile app devices/);
	assert.match(source, /Browser web push is not available yet/);
});
