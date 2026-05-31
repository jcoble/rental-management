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
