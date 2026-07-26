import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const alertsSource = readFileSync(
	new URL('../../../routes/(protected)/settings/notifications/my-alerts/+page.svelte', import.meta.url),
	'utf8'
);
const teamSource = readFileSync(
	new URL('../../../routes/(protected)/settings/notifications/team-routing/+page.svelte', import.meta.url),
	'utf8'
);
const tenantSource = readFileSync(
	new URL('../../../routes/(protected)/settings/notifications/tenant-notices/+page.svelte', import.meta.url),
	'utf8'
);

describe('notification setup journey pages', () => {
	it('keeps each canonical route as one journey step', () => {
		assert.match(alertsSource, /currentStep=\{1\}/);
		assert.match(teamSource, /currentStep=\{2\}/);
		assert.match(tenantSource, /currentStep=\{3\}/);
		for (const source of [alertsSource, teamSource, tenantSource]) {
			assert.match(source, /NotificationSetupJourney/);
			assert.match(source, /hasUnsavedChanges/);
		}
	});

	it('uses published help destinations', () => {
		assert.match(teamSource, /helpHref="\/docs\/daily-briefing"/);
		assert.match(tenantSource, /helpHref="\/docs\/notices"/);
	});

	it('keeps tenant policies closed until one key is selected', () => {
		assert.match(tenantSource, /let expandedAutomation = \$state<string \| null>\(null\)/);
		assert.match(tenantSource, /expandedAutomation = policy\.automationKey/);
		assert.match(tenantSource, /open=\{expandedAutomation === policy\.automationKey\}/);
		assert.match(tenantSource, /templateOpen = false/);
	});

	it('uses plain labels while preserving canonical save calls', () => {
		for (const label of [
			'Rent is due soon',
			'Rent is late',
			'Offer a lease renewal',
			'Offer month-to-month',
			'Lease will end'
		]) {
			assert.match(tenantSource, new RegExp(label));
		}
		assert.match(tenantSource, /notifications\.tenantNotices\.updatePolicy/);
		assert.match(teamSource, /notifications\.teamRouting\.replace/);
		assert.match(alertsSource, /notifications\.myAlerts\.update/);
	});
});
