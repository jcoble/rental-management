export interface PortfolioSelectionResolution {
	id: number | null;
	shouldPersist: boolean;
}

function parsePositiveInteger(value: string | null): number | null {
	if (!value) return null;
	const parsed = parseInt(value, 10);
	return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

function isPositiveInteger(value: number | undefined): value is number {
	return typeof value === 'number' && Number.isInteger(value) && value > 0;
}

/**
 * Resolve the client-side portfolio selection during authenticated layout hydration.
 *
 * Phase 0 users are claim-scoped to one portfolio server-side, so the authenticated fallback is
 * authoritative. A stale localStorage value can come from a previous localhost account and must not
 * win, or pages can briefly query another portfolio id before the selector list corrects it.
 */
export function resolveInitialPortfolioId(
	storedValue: string | null,
	fallbackPortfolioId?: number
): PortfolioSelectionResolution {
	const storedId = parsePositiveInteger(storedValue);

	if (isPositiveInteger(fallbackPortfolioId)) {
		return {
			id: fallbackPortfolioId,
			shouldPersist: storedId !== fallbackPortfolioId,
		};
	}

	return {
		id: storedId,
		shouldPersist: false,
	};
}
