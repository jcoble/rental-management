import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const settingsSource = readFileSync('src/routes/(protected)/settings/+page.svelte', 'utf8');
const alertsSource = readFileSync(
	'src/routes/(protected)/settings/notifications/my-alerts/+page.svelte',
	'utf8',
);
const routingSource = readFileSync(
	'src/routes/(protected)/settings/notifications/team-routing/+page.svelte',
	'utf8',
);
const tenantSource = readFileSync(
	'src/routes/(protected)/settings/notifications/tenant-notices/+page.svelte',
	'utf8',
);
const endpointSource = readFileSync('src/lib/api/endpoints/notifications.ts', 'utf8');

test('notification settings is a three-area route-backed landing with no legacy mixed matrix', () => {
	assert.match(settingsSource, /\/settings\/notifications\/my-alerts/);
	assert.match(settingsSource, /\/settings\/notifications\/team-routing/);
	assert.match(settingsSource, /\/settings\/notifications\/tenant-notices/);
	assert.doesNotMatch(settingsSource, /settings-notification-audience-matrix/);
	assert.doesNotMatch(settingsSource, /Send tenant notices/);
	assert.doesNotMatch(settingsSource, /notifications\.getSettings/);
	assert.doesNotMatch(settingsSource, /notifications\.setSettings/);
});

test('My alerts is personal and exposes every canonical channel', () => {
	assert.match(alertsSource, /Configuring alerts for/);
	for (const channel of ['In-app', 'Mobile push', 'Email', 'SMS'])
		assert.match(alertsSource, new RegExp(channel));
	assert.match(alertsSource, /notifications\.myAlerts\.update/);
});

test('Team routing and Tenant notices use separate canonical persistence', () => {
	assert.match(routingSource, /notifications\.teamRouting\.replace/);
	assert.match(routingSource, /Fall back to Workspace Administrators/);
	assert.match(routingSource, /namedRecipientSummary/);
	assert.match(routingSource, /routingExplanation/);
	assert.doesNotMatch(routingSource, /\.filter\(/);
	assert.match(routingSource, /Morning Briefing schedule/);
	assert.match(routingSource, /notifications\.morningBriefing\.update/);
	assert.match(routingSource, /topic: 'MorningBriefing'/);
	assert.match(tenantSource, /\{ value: 'Off'/);
	assert.match(tenantSource, /\{ value: 'Draft'/);
	assert.match(tenantSource, /\{ value: 'Auto'/);
	assert.match(tenantSource, /notifications\.tenantNotices\.updatePolicy/);
	assert.match(tenantSource, /Save and use new template version/);
	assert.match(tenantSource, /templateProvenance/);
	assert.match(tenantSource, /Restore and use current supplied default/);
	assert.doesNotMatch(tenantSource, /seedTemplates/);
	for (const status of ['Queued', 'Accepted', 'Retrying', 'Sent', 'PermanentlyFailed'])
		assert.match(tenantSource, new RegExp(status));
	assert.doesNotMatch(tenantSource, /'Delivered'/);
	assert.doesNotMatch(tenantSource, /'Failed'/);
});

test('notification API clients do not preserve the broad legacy settings path', () => {
	assert.match(endpointSource, /"\/my-alerts"/);
	assert.match(endpointSource, /"\/team-routing"/);
	assert.match(endpointSource, /"\/tenant-notices"/);
	assert.doesNotMatch(endpointSource, /\/notification-settings\//);
});
