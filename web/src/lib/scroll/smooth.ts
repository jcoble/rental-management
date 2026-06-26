/**
 * Marketing smooth-scroll engine (Lenis) for the public landing.
 *
 * Mirrored from EdiPlatform's `src/lib/scroll/smooth.ts` and wired to GSAP
 * ScrollTrigger so the scrubbed hero / section animations ride the same buttery
 * scroll instead of raw wheel deltas.
 *
 * - **Window-scoped Lenis.** The marketing page scrolls the document (the app
 *   shell's `body { overflow:hidden }` is overridden on the welcome route), so
 *   Lenis + ScrollTrigger both use the default window scroller — the simplest,
 *   most robust setup.
 * - **One rAF source of truth.** `gsap.ticker` drives `lenis.raf`, and every
 *   Lenis 'scroll' calls `ScrollTrigger.update()` so pinned/scrubbed triggers
 *   stay in perfect lockstep with the smoothed scroll position.
 * - **Fans scroll out to subscribers** (parallax layers, the nav progress bar)
 *   via `onScroll`, so there's a single source rather than many scroll listeners.
 * - **Honors `prefers-reduced-motion`:** Lenis is NOT started; subscribers and
 *   ScrollTrigger still get native-scroll updates so everything keeps working.
 *
 * Everything is browser-only and dynamically imported, so this module is safe to
 * reference from an SSR'd page (the heavy deps never load on the server).
 */

// Type-only import is erased at compile time, so it stays SSR-safe (no runtime
// `lenis` import on the server) while giving us full typing.
import type Lenis from 'lenis';

type ScrollSub = (scroll: number, progress: number) => void;

let lenis: Lenis | null = null;
const subs = new Set<ScrollSub>();
let last = { scroll: 0, progress: 0 };

function emit(scroll: number, progress: number) {
	last = { scroll, progress };
	for (const s of subs) s(scroll, progress);
}

function winProgress(scroll: number): number {
	const max = document.documentElement.scrollHeight - window.innerHeight;
	return max > 0 ? Math.min(1, Math.max(0, scroll / max)) : 0;
}

/** Subscribe to scroll updates. Fires once immediately; returns an unsubscribe fn. */
export function onScroll(fn: ScrollSub): () => void {
	subs.add(fn);
	fn(last.scroll, last.progress);
	return () => subs.delete(fn);
}

/** Smooth-scroll to a target (selector, element, or absolute offset). */
export function scrollTo(target: string | HTMLElement | number, offset = -72) {
	if (lenis) {
		lenis.scrollTo(target, { offset });
		return;
	}
	if (typeof window === 'undefined') return;
	const el = typeof target === 'string' ? document.querySelector<HTMLElement>(target) : target;
	if (el instanceof HTMLElement) window.scrollTo({ top: el.offsetTop + offset, behavior: 'smooth' });
	else if (typeof target === 'number') window.scrollTo({ top: target + offset, behavior: 'smooth' });
}

/**
 * Start the engine. Returns a teardown fn (call from onDestroy / onMount cleanup).
 * The async setup is internal; the returned teardown is safe to call immediately.
 */
export function startSmoothScroll(): () => void {
	let cancelled = false;
	let teardown = () => {};

	(async () => {
		const [{ default: Lenis }, { gsap }, { ScrollTrigger }] = await Promise.all([
			import('lenis'),
			import('gsap'),
			import('gsap/ScrollTrigger')
		]);
		if (cancelled) return;
		gsap.registerPlugin(ScrollTrigger);

		const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
		if (reduce) {
			const onNative = () => {
				emit(window.scrollY, winProgress(window.scrollY));
				ScrollTrigger.update();
			};
			window.addEventListener('scroll', onNative, { passive: true });
			emit(window.scrollY, winProgress(window.scrollY));
			teardown = () => window.removeEventListener('scroll', onNative);
			return;
		}

		const instance = new Lenis({
			duration: 1.35,
			// floatier glide: quartic ease-out → a longer, dreamier decelerate tail
			easing: (t: number) => 1 - Math.pow(1 - t, 4),
			smoothWheel: true,
			wheelMultiplier: 0.92,
			touchMultiplier: 1.4
		});
		lenis = instance;
		if (import.meta.env.DEV) (window as unknown as { __lenis?: unknown }).__lenis = instance;

		instance.on('scroll', (e: { scroll: number; progress: number }) => {
			emit(e.scroll, e.progress);
			ScrollTrigger.update();
		});

		const tick = (time: number) => instance.raf(time * 1000);
		gsap.ticker.add(tick);
		gsap.ticker.lagSmoothing(0);
		ScrollTrigger.refresh();

		teardown = () => {
			gsap.ticker.remove(tick);
			instance.destroy();
			if (lenis === instance) lenis = null;
		};
	})();

	return () => {
		cancelled = true;
		teardown();
	};
}
