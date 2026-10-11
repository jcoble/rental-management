import { canRunApplicationScreening } from './application-display.ts';

/** The three outcomes a landlord can record once a screening report is back. */
export type ScreeningDecision = 'Accept' | 'Conditional' | 'Decline';

/** What the single screening button does, or `null` when it cannot be offered at all. */
export type ScreeningActionKind = 'integrated' | 'external';

/**
 * One screening action, never a choice of integration. A connected provider means the
 * button invites the applicant through Rental Command; otherwise it records a screening
 * the landlord ran somewhere else. Either way it needs consent and an open application.
 */
export function screeningAction(input: {
	providerConnected: boolean | null | undefined;
	consent: boolean | null | undefined;
	status: string | null | undefined;
}): ScreeningActionKind | null {
	if (!canRunApplicationScreening(input.status, input.consent)) return null;
	return input.providerConnected === true ? 'integrated' : 'external';
}

/**
 * The report details (reference and the company that ran the report) are only legally
 * needed when the landlord declines and says the report influenced that decline.
 */
export function reportDetailsRequired(input: {
	decision: ScreeningDecision | null | undefined;
	consumerReportUsed: boolean | null | undefined;
}): boolean {
	return input.decision === 'Decline' && input.consumerReportUsed === true;
}
