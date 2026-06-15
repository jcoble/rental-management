/**
 * Persist a grid's filter / sort / search / paging state in the page URL query string, so it survives
 * navigating away and back (and browser Back/Forward, which remounts the page and re-seeds from the
 * URL). Mirrors EdiPlatform's grid pattern: read the params on load, then `goto('?…', { replaceState })`
 * on change. `replaceState` keeps each keystroke from stacking a history entry; `noScroll` + `keepFocus`
 * keep typing in a filter input undisturbed.
 *
 * Usage (Svelte 5 runes): declare the state, seed it from {@link readGridParam} at init, then register a
 * single `$effect` that calls {@link syncGridUrl} with the current values. The query keys you pass are
 * the URL param names; only non-empty / non-default values are written, keeping the URL clean.
 *
 *     import { page } from '$app/stores';
 *     import { get } from 'svelte/store';
 *     import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
 *
 *     const seed = get(page).url.searchParams;
 *     let search = $state(readGridParam(seed, 'q'));
 *     let status = $state(readGridParam(seed, 'status'));
 *     let pageNum = $state(readGridParam(seed, 'page', 1));
 *
 *     $effect(() => {
 *         syncGridUrl({ q: search, status, page: pageNum }, { page: 1 });
 *     });
 *
 * The values object maps URL param name -> current value (string | number). `defaults` (optional) lists
 * values that should be OMITTED from the URL (e.g. page 1, the default sort), keeping links tidy.
 */

import { goto } from '$app/navigation';
import { get } from 'svelte/store';
import { page } from '$app/stores';

/** Read a string grid param from the seed URL (empty string when absent). */
export function readGridParam(params: URLSearchParams, key: string): string;
/** Read a numeric grid param from the seed URL (`fallback` when absent / unparseable, min 1). */
export function readGridParam(params: URLSearchParams, key: string, fallback: number): number;
export function readGridParam(params: URLSearchParams, key: string, fallback?: number): string | number {
	const raw = params.get(key);
	if (fallback !== undefined) {
		const n = Number(raw);
		return Number.isFinite(n) && n >= 1 ? Math.floor(n) : fallback;
	}
	return raw ?? '';
}

/**
 * Build the query string for a set of grid values MERGED onto a base set of existing params, so
 * unrelated params on the URL (coach marks, deep-link flags, etc.) are preserved. Grid keys are set
 * when non-empty / non-default and deleted otherwise; every other base param is left untouched.
 */
export function gridQueryString(
	values: Record<string, string | number | null | undefined>,
	defaults: Record<string, string | number> = {},
	base?: URLSearchParams
): string {
	const p = new URLSearchParams(base);
	for (const [key, value] of Object.entries(values)) {
		const omit =
			value === null ||
			value === undefined ||
			value === '' ||
			(Object.prototype.hasOwnProperty.call(defaults, key) && defaults[key] === value);
		if (omit) {
			p.delete(key);
		} else {
			p.set(key, String(value));
		}
	}
	return p.toString();
}

/**
 * Reflect the current grid values into the URL (replaceState), preserving any unrelated params already
 * on the URL. No-op when the URL already matches, so it's safe to call from an `$effect` that re-runs on
 * every state change without stacking navigations or looping. Returns the query string it settled on.
 */
export function syncGridUrl(
	values: Record<string, string | number | null | undefined>,
	defaults: Record<string, string | number> = {}
): string {
	const url = get(page).url;
	const query = gridQueryString(values, defaults, url.searchParams);
	const current = url.searchParams.toString();
	if (query !== current) {
		void goto(query ? `?${query}` : url.pathname, {
			replaceState: true,
			noScroll: true,
			keepFocus: true,
		});
	}
	return query;
}
