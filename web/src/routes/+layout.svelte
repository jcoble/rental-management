<script lang="ts">
	import '../app.css';
	// Body/UI typeface.
	import '@fontsource-variable/google-sans-flex/index.css';
	// Display font for large titles only (Display + Headline roles, via --m3-font-display).
	import '@fontsource-variable/funnel-display/index.css';
	// Experiment-swap alternative for --m3-font-display (Space Grotesk).
	import '@fontsource-variable/space-grotesk/index.css';
	// Material Symbols Rounded (variable) — sidebar-nav icons only. Self-hosted SUBSET
	// (~36 KB) keeping every axis (FILL/wght/GRAD/opsz) so the active nav item morphs
	// its FILL 0→1 (outline→filled). Registers 'Material Symbols Rounded Variable'.
	import '$lib/styles/material-symbols.css';
	// Generated M3 color roles (--m3c-*) + base design tokens (shape/type/motion/
	// elevation + .m3-shape-morph* / .m3-motion-* utilities). Imported here (not via
	// app.css @import) so they bypass Tailwind v4's @import processor, which errors on
	// imported files containing a top-level :root{} block.
	import '$lib/styles/m3-theme.css';
	import '$lib/styles/m3-base.css';
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import { ModeWatcher } from 'mode-watcher';
	import { Toaster } from '$lib/components/ui/sonner';
	import NavigationLoader from '$lib/components/NavigationLoader.svelte';
	import * as Tooltip from '$lib/components/ui/tooltip';
	import { shouldRetryQuery } from '$lib/api/query-retry';
	import { onNavigate } from '$app/navigation';
	import { initAuth } from '$lib/stores/auth.svelte';
	import { env } from '$env/dynamic/public';
	import SimClockPanel from '$lib/dev/SimClockPanel.svelte';
	import type { LayoutData } from './$types';

	let { data, children }: { data: LayoutData; children: import('svelte').Snippet } = $props();

	// Dev-only master simulation-clock panel (spec §7.3), gated on PUBLIC_SIMULATION_ENABLED.
	const simClockEnabled = env.PUBLIC_SIMULATION_ENABLED === 'true';

	const queryClient = new QueryClient({
		defaultOptions: {
			queries: {
				staleTime: 15000,
				refetchOnWindowFocus: false,
				// Don't retry deterministic client errors (esp. a 404 on a not-found detail page), but do
				// retry transient request abort/timeouts so route navigation does not leave recoverable
				// detail pages stuck in a hard error state.
				retry: shouldRetryQuery
			}
		}
	});

	// Scoped View Transitions (TSK-596). The prior attempt cross-faded the WHOLE root (shell
	// included) and stacked with the route-enter animation → a 12px drift/flicker. Now the shell
	// is pinned via stable view-transition-names (see AppShell + app.css) so only the content
	// cross-fades, and the CSS route-enter is disabled where the API runs (app.css @supports), so
	// the two never stack. A shared `rc-hero` name (clicked list row ↔ detail header) yields an M3
	// container transform. Skips in-place ?tab= changes; no-ops where unsupported / reduced-motion.
	//
	// Cold-cache fix: detail pages fetch their hero data client-side via TanStack Query, which is
	// independent of SvelteKit's own navigation lifecycle. `navigation.complete` resolves once the
	// destination component has mounted — but on a NEVER-visited record, its query is still
	// in-flight at that moment, so `.rc-hero` hasn't rendered yet and the transition's "after"
	// snapshot misses it (silently degrades to a plain cross-fade). A REVISIT happens to work
	// because the cached, already-resolved query renders the hero synchronously on mount — hence
	// the bug report "only works the 2nd time a row is clicked." Detect a hero-tagged click
	// source (DataGrid.svelte tags the clicked row `view-transition-name: rc-hero` before
	// navigating) and, only then, give the destination a bounded window to actually render its
	// hero before the snapshot is taken — every other navigation is untouched.
	onNavigate((navigation) => {
		const doc = document as unknown as {
			startViewTransition?: (cb: () => Promise<void> | void) => {
				ready: Promise<void>;
				finished: Promise<void>;
				updateCallbackDone: Promise<void>;
			};
		};
		if (
			typeof document === 'undefined' ||
			!doc.startViewTransition ||
			window.matchMedia('(prefers-reduced-motion: reduce)').matches ||
			navigation.from?.url.pathname === navigation.to?.url.pathname
		) {
			return;
		}
		// Must be read now, synchronously, before startViewTransition takes its "before" snapshot —
		// the clicked row (still in the live DOM at this point) carries the tag DataGrid set on click.
		const heroTaggedSource = document.querySelector('[style*="rc-hero"]') !== null;
		return new Promise<void>((resolve) => {
			const transition = doc.startViewTransition!(async () => {
				resolve();
				await navigation.complete;
				if (heroTaggedSource) await waitForHeroReady();
			});
			// The transition can be superseded/skipped (e.g. another navigation starts, or the tab
			// is backgrounded) — that's a normal race, not a bug. Its own ready/finished/
			// updateCallbackDone promises reject in that case; without a handler that surfaces as an
			// unhandled-rejection console error even though the page itself already navigated
			// correctly via SvelteKit regardless of what the VT animation does.
			transition.finished.catch(() => {});
			transition.ready.catch(() => {});
		});
	});

	/** Poll for `.rc-hero` to mount WITH real content. Uses setTimeout rather than
	 *  requestAnimationFrame — rAF can be throttled/paused entirely by the browser while the tab
	 *  is backgrounded, which would hold the transition's old-page freeze open far longer than
	 *  intended if the user switches away mid-navigation. Bounded: if it never shows up (slow
	 *  network, or the destination has no hero at all), this just times out and the transition
	 *  proceeds with its existing degrade-gracefully cross-fade — never blocks navigation
	 *  indefinitely. */
	async function waitForHeroReady(maxMs = 1500): Promise<void> {
		const start = performance.now();
		while (performance.now() - start < maxMs) {
			const hero = document.querySelector('.rc-hero');
			if (hero && (hero.textContent ?? '').trim().length > 0) return;
			await new Promise<void>((r) => setTimeout(r, 30));
		}
	}

	// Seed the runes auth store from server-provided session data. Re-runs when
	// the server data changes (e.g. after login/logout navigations).
	$effect(() => {
		initAuth(
			data.user ?? null,
			data.accessToken ?? null,
			data.accessTokenExpiration ? new Date(data.accessTokenExpiration) : null,
			data.access ?? null,
			data.isPlatformAdmin ?? false
		);
	});
</script>

<ModeWatcher
	defaultMode="dark"
	lightClassNames={['light']}
	themeColors={{ dark: '#121217', light: '#fdf9ff' }}
/>
<NavigationLoader />
<Toaster richColors closeButton position="bottom-right" />
{#if simClockEnabled}
	<SimClockPanel />
{/if}
<Tooltip.Provider delayDuration={300}>
	<QueryClientProvider client={queryClient}>
		{@render children()}
	</QueryClientProvider>
</Tooltip.Provider>
