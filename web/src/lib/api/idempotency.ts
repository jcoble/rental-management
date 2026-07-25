const pendingOperationKeys = new Map<string, string>();

/** Keeps one caller-owned key across ambiguous failures; clears it only after a confirmed success. */
export async function idempotentMutation<T>(
	scope: string,
	execute: (key: string) => Promise<T>
): Promise<T> {
	const key = pendingOperationKeys.get(scope) ?? crypto.randomUUID();
	pendingOperationKeys.set(scope, key);
	const result = await execute(key);
	pendingOperationKeys.delete(scope);
	return result;
}

/** Explicit UI cancellation may abandon a pending operation and its retry key. */
export function cancelIdempotentMutation(scope: string): void {
	pendingOperationKeys.delete(scope);
}
