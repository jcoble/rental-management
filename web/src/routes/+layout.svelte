<script lang="ts">
	import '../app.css';
	import '@fontsource-variable/google-sans-flex/index.css';
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
	defaultMode="light"
	lightClassNames={['light']}
	themeColors={{ dark: '#151218', light: '#fafafa' }}
/>
<NavigationLoader />
<Toaster richColors closeButton position="bottom-right" />
<Tooltip.Provider delayDuration={300}>
	<QueryClientProvider client={queryClient}>
		{@render children()}
	</QueryClientProvider>
</Tooltip.Provider>
