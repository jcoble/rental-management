import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const alertsSource = readFileSync(new URL('./MyAlertsSection.svelte', import.meta.url), 'utf8');
const teamSource = readFileSync(new URL('./TeamRoutingSection.svelte', import.meta.url), 'utf8');
const tenantSource = readFileSync(new URL('./TenantNoticesSection.svelte', import.meta.url), 'utf8');

describe('notification settings sections', () => {
	it('keeps each area as one anchored section', () => {
		assert.match(alertsSource, /id="my-alerts"/);
		assert.match(teamSource, /id="team-routing"/);
		assert.match(tenantSource, /id="tenant-notices"/);
		for (const source of [alertsSource, teamSource, tenantSource]) {
			assert.match(source, /NotificationHelpAction/);
			assert.match(source, /hasUnsavedChanges/);
		}
	});

	it('uses published help destinations', () => {
		assert.match(teamSource, /href="\/docs\/daily-briefing"/);
		assert.match(tenantSource, /href="\/docs\/notices"/);
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
