import { afterEach } from 'vitest';

if (typeof window !== 'undefined' && typeof window.matchMedia !== 'function') {
	window.matchMedia = (query: string): MediaQueryList => {
		const listeners = new Set<(event: MediaQueryListEvent) => void>();
		return {
			matches: false,
			media: query,
			onchange: null,
			addListener: (listener) => listeners.add(listener),
			removeListener: (listener) => listeners.delete(listener),
			addEventListener: (_type, listener) => {
				if (typeof listener === 'function') listeners.add(listener as (event: MediaQueryListEvent) => void);
			},
			removeEventListener: (_type, listener) => {
				if (typeof listener === 'function') listeners.delete(listener as (event: MediaQueryListEvent) => void);
			},
			dispatchEvent: (event) => {
				listeners.forEach((listener) => listener(event as MediaQueryListEvent));
				return true;
			}
		} as MediaQueryList;
	};
}

// bits-ui releases body-scroll-lock on a short timer after unmounting.
afterEach(async () => {
	await new Promise((resolve) => setTimeout(resolve, 75));
});
