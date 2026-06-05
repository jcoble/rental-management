/**
 * Wrap an async operation so concurrent callers share one in-flight promise.
 * Used to dedupe token refreshes triggered by overlapping requests.
 */
export function createSingleFlight<TArgs extends unknown[], TResult>(
	operation: (...args: TArgs) => Promise<TResult>
): (...args: TArgs) => Promise<TResult> {
	let inFlight: Promise<TResult> | null = null;

	return (...args: TArgs): Promise<TResult> => {
		if (!inFlight) {
			inFlight = Promise.resolve(operation(...args)).finally(() => {
				inFlight = null;
			});
		}

		return inFlight;
	};
}

/**
 * Like {@link createSingleFlight}, but additionally re-serves the LAST
 * SUCCESSFUL result for `reuseWindowMs` after it settles. This coalesces a
 * *burst* of calls (e.g. N concurrent + slightly-staggered 401 retries on page
 * mount) into a single underlying run, not just calls whose lifetimes overlap.
 *
 * Used for token refresh: N concurrent API 401s must share ONE real refresh.
 * Firing several would replay the server's single-use refresh token and trip
 * reuse-detection. Failures are never cached, so a failed refresh retries
 * immediately and surfaces to the caller.
 */
export function createSingleFlightWithReuse<TArgs extends unknown[], TResult>(
	operation: (...args: TArgs) => Promise<TResult>,
	reuseWindowMs: number
): (...args: TArgs) => Promise<TResult> {
	let inFlight: Promise<TResult> | null = null;
	let cached: { result: TResult; expiresAt: number } | null = null;

	return (...args: TArgs): Promise<TResult> => {
		if (cached && Date.now() < cached.expiresAt) {
			return Promise.resolve(cached.result);
		}
		cached = null;

		if (!inFlight) {
			inFlight = Promise.resolve(operation(...args))
				.then((result) => {
					cached = { result, expiresAt: Date.now() + reuseWindowMs };
					return result;
				})
				.finally(() => {
					inFlight = null;
				});
		}

		return inFlight;
	};
}
