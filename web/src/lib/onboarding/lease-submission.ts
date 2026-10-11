import type { ManualLeaseCreateRequest } from '$lib/api/scan';
import {
	buildOnboardingLeaseScanOverrides,
	buildOnboardingManualLeaseRequest,
	type OnboardingLeaseConfirmInput,
} from './lease-scan-confirm.ts';

export interface LeaseSubmissionScan {
	createManualLease: (request: ManualLeaseCreateRequest, operationId: string) => Promise<unknown>;
	confirm: (draftId: number, overridesJson: string) => Promise<unknown>;
}

export interface LeaseSubmissionCoordinatorOptions {
	createOperationId?: () => string;
	/** Kept for the executable navigation contract; Guided Setup owns wizard navigation. */
	navigate?: (url: string) => void;
}

/**
 * Coordinates the two Guided Setup lease submission branches. Manual retries retain the same
 * idempotency key until the caller clears the successful operation; prefilled drafts always use
 * the existing confirmation endpoint. Navigation is deliberately left to the wizard caller.
 */
export function createLeaseSubmissionCoordinator(
	scan: LeaseSubmissionScan,
	options: LeaseSubmissionCoordinatorOptions = {}
) {
	let manualOperationId: string | null = null;
	const createOperationId = options.createOperationId ?? (() => crypto.randomUUID());

	// The coordinator intentionally never calls navigate: a manual lease stays in Guided Setup,
	// and the component advances the wizard after the mutation succeeds.
	void options.navigate;

	return {
		submit(data: OnboardingLeaseConfirmInput, prefillDraftId: number | null) {
			if (prefillDraftId != null) {
				return scan.confirm(prefillDraftId, buildOnboardingLeaseScanOverrides(data));
			}

			manualOperationId ??= createOperationId();
			return scan.createManualLease(
				buildOnboardingManualLeaseRequest(data),
				manualOperationId
			);
		},
		clear() {
			manualOperationId = null;
		},
	};
}
