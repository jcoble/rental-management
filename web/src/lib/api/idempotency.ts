const pendingOperationKeys = new Map<string, string>();

/** Keeps one caller-owned key across ambiguous failures; clears it only after a confirmed success. */
export async function idempotentMutation<T>(
	scope: string,
	execute: (key: string) => Promise<T>
): Promise<T> {
	const key = pendingOperationKeys.get(scope) ?? crypto.randomUUID();
	pendingOperationKeys.set(scope, key);
	try {
		const result = await execute(key);
		pendingOperationKeys.delete(scope);
		return result;
	} catch (error) {
		// Payment endpoints return a typed 409 only after the server has reconciled the
		// provider attempt and marked it Canceled/Failed. Release that browser nonce so
		// the next deliberate tap can start a new attempt; retain it for ambiguous/pending
		// responses where retrying the exact provider key is the safe action.
		if (isTerminalProviderAttemptResponse(error)) pendingOperationKeys.delete(scope);
		throw error;
	}
}

function isTerminalProviderAttemptResponse(error: unknown): boolean {
	if (!error || typeof error !== 'object') return false;
	const candidate = error as { status?: unknown; extensions?: unknown };
	if (candidate.status !== 409 || !candidate.extensions || typeof candidate.extensions !== 'object') {
		return false;
	}
	const state = (candidate.extensions as Record<string, unknown>).attemptState;
	return state === 'Canceled' || state === 'Failed';
}

/** Explicit UI cancellation may abandon a pending operation and its retry key. */
export function cancelIdempotentMutation(scope: string): void {
	pendingOperationKeys.delete(scope);
}
