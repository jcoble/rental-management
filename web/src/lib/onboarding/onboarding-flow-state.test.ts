import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

import {
	resolveInitialOnboardingState,
	shouldFinishAfterOptionalStep,
	type OnboardingStepDone,
} from './onboarding-flow-state.ts';
import { WIZARD_STEPS } from './wizard-steps.ts';

function stepDone(): OnboardingStepDone {
	return Object.fromEntries(
		WIZARD_STEPS.map((step) => [step.key, false])
	) as OnboardingStepDone;
}

function completedCore(overrides: Partial<OnboardingStepDone> = {}): OnboardingStepDone {
	return {
		...stepDone(),
		portfolio: true,
		owner: true,
		import: true,
		property: true,
		tenants: true,
		lease: true,
		...overrides,
	};
}

describe('onboarding flow state', () => {
	it('opens the finished state instead of the create-lease step when core records already exist', () => {
		assert.deepEqual(
			resolveInitialOnboardingState({
				requested: null,
				persisted: null,
				stepDone: completedCore(),
			}),
			{ stepKey: 'lease', finished: true }
		);
	});

	it('ignores a completed persisted lease step when deciding where to resume', () => {
		assert.deepEqual(
			resolveInitialOnboardingState({
				requested: null,
				persisted: 'lease',
				stepDone: completedCore(),
			}),
			{ stepKey: 'lease', finished: true }
		);
	});

	it('still honors explicit settings deep links into optional steps', () => {
		assert.deepEqual(
			resolveInitialOnboardingState({
				requested: 'notifications',
				persisted: null,
				stepDone: completedCore({ notifications: false }),
			}),
			{ stepKey: 'notifications', finished: false }
		);
	});

	it('returns to the finished screen after optional setup launched from the finished screen', () => {
		assert.equal(
			shouldFinishAfterOptionalStep({
				currentStepKey: 'notifications',
				returnToFinishAfterOptional: true,
			}),
			true
		);
		assert.equal(
			shouldFinishAfterOptionalStep({
				currentStepKey: 'lease',
				returnToFinishAfterOptional: true,
			}),
			false
		);
	});

	it('wires the onboarding page through the shared flow-state helpers', () => {
		const source = readFileSync(
			new URL('../../routes/(protected)/onboarding/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /resolveInitialOnboardingState\(/);
		assert.match(source, /shouldFinishAfterOptionalStep\(/);
		assert.match(source, /openOptionalStepFromFinished\('notifications'\)/);
		assert.match(source, /autoAdvanced && !finished && currentStep/);
	});

	it('does not expose editable setup steps before existing-record detection settles', () => {
		const source = readFileSync(
			new URL('../../routes/(protected)/onboarding/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /data-testid="onboarding-detection-loading"/);
		assert.match(source, /\{:else if !detectionReady \|\| !autoAdvanced\}/);
		assert.match(source, /data-testid="onboarding-detection-error"/);
		assert.match(source, /retryExistingDataDetection/);
		assert.match(
			source,
			/const detectionReady = \$derived\(\s*\[\s*portfolioQuery\.isSuccess,\s*ownersQuery\.isSuccess,\s*propertiesQuery\.isSuccess,\s*tenantsQuery\.isSuccess,\s*leasesQuery\.isSuccess\s*\]\.every\(Boolean\)\s*\)/s,
			'all query status properties must be read eagerly so TanStack tracks every result before any query settles'
		);
		assert.doesNotMatch(
			source,
			/portfolioQuery\.isSuccess\s*&&/,
			'short-circuiting skips subscriptions for faster queries and can leave detection pending forever'
		);
	});

	it('keeps the guided import center reachable from the finished screen', () => {
		const source = readFileSync(
			new URL('../../routes/(protected)/onboarding/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /data-testid="onboarding-return-import"/);
		assert.match(source, /openImportCenterFromFinished\(\)/);
	});

	it('does not bounce example-data accounts away from guided setup', () => {
		const source = readFileSync(
			new URL('../../routes/(protected)/onboarding/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.doesNotMatch(source, /redirectedFromSandbox/);
		assert.doesNotMatch(source, /Setup runs on a live account/);
		assert.doesNotMatch(source, /sandboxQuery\.data\?\.isSandbox === true\) return/);
	});

	it('makes lease-first scan a first-class onboarding step', () => {
		const source = readFileSync(
			new URL('../../routes/(protected)/onboarding/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /currentStep\.key === 'import'/);
		assert.match(source, /<LeaseFirstImport[^>]*oncomplete=\{handleLeaseImportComplete\}/s);
		assert.match(source, /leaseImportCreatedSpine/);
		assert.match(source, /if \(!result\.leaseManagementId \|\| !result\.agreementId\)/);
		assert.match(source, /queryKey: \['lease-managements'\]/);
		assert.doesNotMatch(source, /result\.leaseId/);
	});
});
