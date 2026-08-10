import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';
import { createLeaseSubmissionCoordinator } from './lease-submission.ts';

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

	test('manual lease submission uses a stable manual operation and never navigates to applications', async () => {
		const manualCalls: Array<{ request: unknown; operationId: string }> = [];
		const confirmCalls: Array<{ draftId: number; overrides: string }> = [];
		const navigationCalls: string[] = [];
		let attempts = 0;
		const coordinator = createLeaseSubmissionCoordinator(
			{
				createManualLease: async (request, operationId) => {
					manualCalls.push({ request, operationId });
					attempts += 1;
					if (attempts === 1) throw new Error('retryable');
					return { status: 'confirmed' };
				},
				confirm: async (draftId, overrides) => {
					confirmCalls.push({ draftId, overrides });
					return { status: 'confirmed' };
				},
			},
			{ createOperationId: () => 'manual-operation-834', navigate: (url) => navigationCalls.push(url) }
		);
		const validLease = {
			leaseNumber: 'MANUAL-834',
			propertyId: 12,
			unitId: 34,
			tenantId: 56,
			startDate: '2026-09-01',
			endDate: '2027-08-31',
			monthlyRent: 1450,
			securityDeposit: 1450,
			lateFeeAmount: 75,
			rentDueDay: 1,
			reviewDisposition: 'NeedsSignatures' as const,
			documentTemplateId: null,
		};

		await assert.rejects(() => coordinator.submit(validLease, null), /retryable/);
		await coordinator.submit(validLease, null);
		assert.equal(manualCalls.length, 2);
		assert.deepEqual(manualCalls[0].request, {
			propertyId: 12,
			unitId: 34,
			tenantId: 56,
			leaseNumber: 'MANUAL-834',
			startDate: '2026-09-01',
			endDate: '2027-08-31',
			monthlyRent: 1450,
			securityDeposit: 1450,
			lateFee: 75,
			rentDueDay: 1,
		});
		assert.equal(manualCalls[0].operationId, 'manual-operation-834');
		assert.equal(manualCalls[1].operationId, manualCalls[0].operationId);
		assert.equal(confirmCalls.length, 0);
		assert.deepEqual(navigationCalls, []);

		await coordinator.submit(validLease, 77);
		assert.equal(confirmCalls.length, 1);
		assert.equal(confirmCalls[0].draftId, 77);
		assert.equal(JSON.parse(confirmCalls[0].overrides).leaseNumber, 'MANUAL-834');
	});

	test('setup polish keeps controls visible, compact, and contextual', () => {
		assert.match(stateSelect, /scrollIntoView\(\{\s*block:\s*'center'/);
		assert.match(onboarding, /<DatePicker[\s\S]*?id="ob-lease-end"[\s\S]*?showToday=\{false\}/);
		assert.match(onboarding, /data-testid="onboarding-unit-more-details"/);
		assert.match(gettingStarted, /import LoadingState from '\$lib\/components\/shared\/LoadingState\.svelte'/);
		assert.match(gettingStarted, /<LoadingState[\s\S]*?testid="get-started-loading"/);
		// Batch 5 docks the panel inside the top bar (absolute, vertically centered); the
		// detailed positioning contract lives in sim-clock-panel.test.ts.
		assert.match(simClock, /position:\s*absolute;[\s\S]*?top:\s*50%;[\s\S]*?right:\s*0;/);
		assert.match(datePicker, /showToday = true/);
	});
});
