import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	ONBOARDING_IMPORT_PHASES,
	onboardingImportPhaseProgress,
	onboardingResumePhase
} from './onboarding-import-plan.ts';

describe('guided onboarding import plan', () => {
	it('breaks the portfolio import into small, explainable phases', () => {
		assert.deepEqual(
			ONBOARDING_IMPORT_PHASES.map((phase) => phase.key),
			['welcome', 'portfolio', 'properties', 'people', 'leases', 'money', 'automation', 'review']
		);
		assert.ok(ONBOARDING_IMPORT_PHASES.every((phase) => phase.explanation.length > 40));
		assert.ok(ONBOARDING_IMPORT_PHASES.every((phase) => phase.rewardLabel.length > 0));
	});

	it('resumes at the first incomplete phase and reports DB-backed progress numbers without client aggregation', () => {
		assert.equal(
			onboardingResumePhase({
				welcome: true,
				portfolio: true,
				properties: false,
				people: false,
				leases: false,
				money: false,
				automation: false,
				review: false
			}),
			'properties'
		);

		assert.deepEqual(
			onboardingImportPhaseProgress({
				completedPhaseKeys: ['welcome', 'portfolio', 'properties', 'people'],
				totalPhaseCount: 8
			}),
			{ completed: 4, total: 8, percent: 50 }
		);
	});
});
