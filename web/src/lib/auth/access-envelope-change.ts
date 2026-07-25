import type { AccessEnvelope } from '$lib/types/user';

/**
 * Cache and realtime authority is bound to these server-selected coordinates. Display-only envelope
 * changes do not require a purge, while any coordinate change must be applied before requests resume.
 */
export function accessEnvelopeAuthorityChanged(
	previous: AccessEnvelope | null,
	next: AccessEnvelope
): boolean {
	if (!previous) return false;
	return (
		previous.selectedContext.accessContextId !== next.selectedContext.accessContextId ||
		previous.selectedContext.portfolioId !== next.selectedContext.portfolioId ||
		previous.selectedContext.accessRevision !== next.selectedContext.accessRevision ||
		previous.selectedContext.activeExperience !== next.selectedContext.activeExperience
	);
}
