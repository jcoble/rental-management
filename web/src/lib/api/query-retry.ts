const MAX_TRANSIENT_QUERY_RETRIES = 2;

function getStatus(error: unknown): number | undefined {
	return typeof error === 'object' && error !== null && 'status' in error
		? Number((error as { status?: unknown }).status)
		: undefined;
}

export function shouldRetryQuery(failureCount: number, error: unknown): boolean {
	const status = getStatus(error);

	if (status === 408) {
		return failureCount < MAX_TRANSIENT_QUERY_RETRIES;
	}

	if (status !== undefined && status >= 400 && status < 500) {
		return false;
	}

	return failureCount < MAX_TRANSIENT_QUERY_RETRIES;
}
