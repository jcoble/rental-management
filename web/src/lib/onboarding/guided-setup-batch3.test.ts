import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const onboarding = readFileSync(
	new URL('../../routes/(protected)/onboarding/+page.svelte', import.meta.url),
	'utf8'
);
const gettingStarted = readFileSync(
	new URL('../../routes/(protected)/get-started/+page.svelte', import.meta.url),
	'utf8'
);
const tasks = readFileSync(new URL('./getting-started-tasks.ts', import.meta.url), 'utf8');
const stateSelect = readFileSync(
	new URL('../components/shared/StateSelect.svelte', import.meta.url),
	'utf8'
);
const datePicker = readFileSync(
	new URL('../components/shared/DatePicker.svelte', import.meta.url),
	'utf8'
);
const simClock = readFileSync(new URL('../dev/SimClockPanel.svelte', import.meta.url), 'utf8');

describe('guided setup batch 3 contracts', () => {
	test('alert completion uses durable preferences on both surfaces and reloads after save', () => {
		assert.match(onboarding, /isPersonalAlertSetupComplete\(gettingStartedSignalsQuery\.data\?\.hasNotificationEmail\)/);
		assert.doesNotMatch(onboarding, /isPersonalAlertSetupComplete\(myAlertsQuery\.data\)/);
		assert.doesNotMatch(onboarding, /notificationsSaved/);
		assert.match(onboarding, /\['getting-started', portfolioId\]/);
		assert.match(onboarding, /myAlertsQuery\.refetch\(\)/);
		assert.match(onboarding, /gettingStartedSignalsQuery\.refetch\(\)/);
		assert.match(tasks, /isPersonalAlertSetupComplete\(s\.hasPersonalAlerts\)/);
	});

	test('optional wizard group has a semantic word boundary', () => {
		assert.match(onboarding, /\{group\.label\}\{#if groupOptional\}\s*\{' '\}/);
	});

	test('wizard tab changes replace the deep-link step in the URL', () => {
		assert.match(onboarding, /function syncStepUrl\(key: WizardStepKey\)/);
		assert.match(onboarding, /url\.searchParams\.set\('step', key\)/);
		assert.match(onboarding, /goto\(`\$\{url\.pathname\}\$\{url\.search\}`/);
		assert.match(onboarding, /stepIndex \+= 1;[\s\S]*?syncStepUrl\(STEPS\[stepIndex\]\.key\)/);
		assert.match(onboarding, /stepIndex -= 1;[\s\S]*?syncStepUrl\(STEPS\[stepIndex\]\.key\)/);
	});

	test('manual lease routing explains that no lease was created before opening Applications', () => {
		assert.match(onboarding, /showError\('This guided step cannot create a lease without an approved application or imported agreement\./);
		assert.match(onboarding, /goto\('\/applications'\)/);
		assert.doesNotMatch(onboarding, /showSuccess\('Choose an approved application/);
	});

	test('setup polish keeps controls visible, compact, and contextual', () => {
		assert.match(stateSelect, /scrollIntoView\(\{\s*block:\s*'center'/);
		assert.match(onboarding, /<DatePicker[\s\S]*?id="ob-lease-end"[\s\S]*?showToday=\{false\}/);
		assert.match(onboarding, /data-testid="onboarding-unit-more-details"/);
		assert.match(gettingStarted, /import LoadingState from '\$lib\/components\/shared\/LoadingState\.svelte'/);
		assert.match(gettingStarted, /<LoadingState[\s\S]*?testid="get-started-loading"/);
		assert.match(simClock, /position:\s*fixed;[\s\S]*?top:\s*calc\(3\.5rem \+ 12px\);[\s\S]*?right:\s*12px;/);
		assert.match(datePicker, /showToday = true/);
	});
});
