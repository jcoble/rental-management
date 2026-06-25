import {
	CORE_WIZARD_STEPS,
	WIZARD_STEPS,
	type WizardStepKey,
} from './wizard-steps.ts';

export type OnboardingStepDone = Record<WizardStepKey, boolean>;

export type InitialOnboardingState = {
	stepKey: WizardStepKey;
	finished: boolean;
};

export function resolveInitialOnboardingState({
	requested,
	persisted,
	stepDone,
}: {
	requested?: WizardStepKey | null;
	persisted?: WizardStepKey | null;
	stepDone: OnboardingStepDone;
}): InitialOnboardingState {
	if (requested && isWizardStep(requested)) {
		return { stepKey: requested, finished: false };
	}

	if (persisted && isWizardStep(persisted) && !stepDone[persisted]) {
		return { stepKey: persisted, finished: false };
	}

	const firstIncompleteCore = CORE_WIZARD_STEPS.find((step) => !stepDone[step.key]);
	if (firstIncompleteCore) {
		return { stepKey: firstIncompleteCore.key, finished: false };
	}

	return { stepKey: 'lease', finished: true };
}

export function shouldFinishAfterOptionalStep({
	currentStepKey,
	returnToFinishAfterOptional,
}: {
	currentStepKey: WizardStepKey;
	returnToFinishAfterOptional: boolean;
}): boolean {
	if (!returnToFinishAfterOptional) return false;
	const currentStep = WIZARD_STEPS.find((step) => step.key === currentStepKey);
	return currentStep?.core === false;
}

function isWizardStep(value: string): value is WizardStepKey {
	return WIZARD_STEPS.some((step) => step.key === value);
}
