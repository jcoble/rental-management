/**
 * Returns a reactive object whose `.value` tracks `getter()` but only after it
 * has stopped changing for `delayMs`. Useful for debouncing search inputs into
 * TanStack Query keys without firing a request on every keystroke.
 *
 * Usage:
 *   let search = $state('');
 *   const debounced = debounced(() => search, 300);
 *   // debounced.value updates 300ms after the last edit
 */
export function debounced<T>(getter: () => T, delayMs = 300): { readonly value: T } {
	let current = $state(getter());

	$effect(() => {
		const next = getter();
		const timer = setTimeout(() => {
			current = next;
		}, delayMs);
		return () => clearTimeout(timer);
	});

	return {
		get value() {
			return current;
		},
	};
}
