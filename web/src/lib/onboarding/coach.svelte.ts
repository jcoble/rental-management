/**
 * Coachmark / spotlight controller — the reusable "show me exactly where to click" primitive.
 *
 * A coach targets a DOM element tagged `data-coach="<key>"`. When started it:
 *  - waits for that element to mount (deep-links navigate first, then the page renders), polling a few
 *    frames before giving up;
 *  - scrolls it into view and tracks its live bounding box on scroll / resize;
 *  - exposes the active key + rect so {@link CoachOverlay} can dim the rest of the page and pulse a
 *    ring + callout around it.
 *
 * It is dismissed on: pressing Escape, the user interacting with the target (pointer/keydown anywhere
 * inside it), or a route change. There is exactly one active coach at a time, held in this module-level
 * runes store so any surface can drive it.
 *
 * Usage: a page reads `?coach=<key>` (and clears that param) then calls {@link startCoach}. Checklist
 * deep-links produce that param via `taskHref()` in getting-started-tasks.ts.
 */
import { tick } from 'svelte';

export interface CoachRect {
	top: number;
	left: number;
	width: number;
	height: number;
}

let _activeKey = $state<string | null>(null);
let _rect = $state<CoachRect | null>(null);
let _label = $state<string | null>(null);
let _targetEl: HTMLElement | null = null;
let _cleanup: (() => void) | null = null;

/** Currently spotlighted coach key (or null). */
export function activeCoachKey(): string | null {
	return _activeKey;
}

/** Live bounding box of the spotlighted element in viewport coordinates (or null). */
export function coachRect(): CoachRect | null {
	return _rect;
}

/** Optional plain-English caption shown in the callout. */
export function coachLabel(): string | null {
	return _label;
}

function readRect(el: HTMLElement): CoachRect {
	const r = el.getBoundingClientRect();
	return { top: r.top, left: r.left, width: r.width, height: r.height };
}

function detach() {
	_cleanup?.();
	_cleanup = null;
	_targetEl = null;
}

/** Stop spotlighting and tear down listeners. */
export function dismissCoach(): void {
	detach();
	_activeKey = null;
	_rect = null;
	_label = null;
}

async function locate(key: string, attempt = 0): Promise<HTMLElement | null> {
	const el = document.querySelector<HTMLElement>(`[data-coach="${CSS.escape(key)}"]`);
	if (el) return el;
	// The target may not be in the DOM yet (route still transitioning). Retry across a handful of
	// frames before giving up so a deep-link lands reliably without spinning forever.
	if (attempt >= 40) return null;
	await new Promise((r) => requestAnimationFrame(() => r(null)));
	return locate(key, attempt + 1);
}

/**
 * Start spotlighting the element tagged `data-coach="<key>"`.
 * @param key the coach key (matches a `data-coach` attribute)
 * @param label optional plain-English caption for the callout
 */
export async function startCoach(key: string, label?: string): Promise<boolean> {
	if (typeof document === 'undefined') return false;
	dismissCoach();
	_activeKey = key;
	_label = label ?? null;
	await tick();

	const el = await locate(key);
	if (!el) {
		// Target never appeared — abandon quietly rather than leaving a dim overlay with nothing under it.
		dismissCoach();
		return false;
	}
	_targetEl = el;

	// Bring it into view (centered), then measure once the scroll settles.
	el.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' });

	const update = () => {
		if (_targetEl) _rect = readRect(_targetEl);
	};
	update();
	// Re-measure for a short window as the smooth-scroll animates.
	let frames = 0;
	const raf = () => {
		update();
		if (frames++ < 30 && _activeKey === key) requestAnimationFrame(raf);
	};
	requestAnimationFrame(raf);

	const onScroll = () => update();
	const onResize = () => update();
	const onKey = (e: KeyboardEvent) => {
		if (e.key === 'Escape') dismissCoach();
	};
	// Interacting with the target itself counts as "I've got it" → dismiss (after the click lands).
	const onTargetActivate = () => setTimeout(() => dismissCoach(), 0);

	window.addEventListener('scroll', onScroll, true);
	window.addEventListener('resize', onResize);
	window.addEventListener('keydown', onKey);
	el.addEventListener('pointerdown', onTargetActivate);
	el.addEventListener('keydown', onTargetActivate);

	_cleanup = () => {
		window.removeEventListener('scroll', onScroll, true);
		window.removeEventListener('resize', onResize);
		window.removeEventListener('keydown', onKey);
		el.removeEventListener('pointerdown', onTargetActivate);
		el.removeEventListener('keydown', onTargetActivate);
	};
	return true;
}
