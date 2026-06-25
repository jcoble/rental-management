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
});
