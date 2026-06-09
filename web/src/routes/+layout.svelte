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
	import { initAuth } from '$lib/stores/auth.svelte';
	import type { LayoutData } from './$types';

	let { data, children }: { data: LayoutData; children: import('svelte').Snippet } = $props();

	const queryClient = new QueryClient({
		defaultOptions: {
			queries: {
				staleTime: 15000,
				refetchOnWindowFocus: false
			}
		}
	});

	// Seed the runes auth store from server-provided session data. Re-runs when
	// the server data changes (e.g. after login/logout navigations).
	$effect(() => {
		initAuth(
			data.user ?? null,
			data.accessToken ?? null,
			data.accessTokenExpiration ? new Date(data.accessTokenExpiration) : null
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
<Tooltip.Provider delayDuration={300}>
	<QueryClientProvider client={queryClient}>
		{@render children()}
	</QueryClientProvider>
</Tooltip.Provider>
