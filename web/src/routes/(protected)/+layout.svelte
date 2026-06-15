<script lang="ts">
	import { onDestroy } from 'svelte';
	import { browser } from '$app/environment';
	import { invalidateAll } from '$app/navigation';
	import { useQueryClient } from '@tanstack/svelte-query';
	import { setOnAuthCleared, isTokenExpired } from '$lib/stores/auth.svelte';
	import { refreshToken } from '$lib/api/client';
	import { signalRService } from '$lib/realtime/signalr';
	import { useInvalidateOnSignalR } from '$lib/realtime/invalidate';
	import { CLIENT_HUB_URL } from '$lib/config';
	import AppShell from '$lib/components/AppShell.svelte';
	import CoachOverlay from '$lib/components/onboarding/CoachOverlay.svelte';
	import CoachTrigger from '$lib/components/onboarding/CoachTrigger.svelte';
	import type { LayoutData } from './$types';

	let { data, children }: { data: LayoutData; children: import('svelte').Snippet } = $props();

	// Bridge SignalR data-update events to TanStack Query invalidation (wired once).
	const queryClient = useQueryClient();
	const disconnectQueryBridge = useInvalidateOnSignalR(queryClient);

	// Tear down the realtime connection when auth is cleared (logout / expiry),
	// before the login redirect fires.
	setOnAuthCleared(() => {
		signalRService.disconnect();
	});

	// Open the hub connection once we have an access token; the service guards
	// against duplicate connects so re-runs are harmless.
	$effect(() => {
		if (data.accessToken) {
			signalRService.connect(CLIENT_HUB_URL);
		}
	});

	// Proactive recovery after laptop sleep/wake or tab switch: refresh a stale
	// token, reconnect the hub if dropped, then refresh page data.
	let isRecovering = false;

	$effect(() => {
		if (!browser) return;

		const handleVisibilityChange = async () => {
			if (document.hidden || isRecovering) return;

			const tokenNeedsRefresh = isTokenExpired(120);
			const signalrNeedsReconnect = !signalRService.isConnected;
			if (!tokenNeedsRefresh && !signalrNeedsReconnect) return;

			isRecovering = true;
			try {
				if (tokenNeedsRefresh) {
					try {
						await refreshToken();
					} catch {
						/* invalidateAll below triggers server-side refresh or redirect */
					}
				}
				if (!signalRService.isConnected) {
					try {
						await signalRService.reconnect();
					} catch {
						/* data refresh still proceeds */
					}
				}
				await invalidateAll();
			} finally {
				isRecovering = false;
			}
		};

		document.addEventListener('visibilitychange', handleVisibilityChange);
		return () => document.removeEventListener('visibilitychange', handleVisibilityChange);
	});

	onDestroy(() => {
		setOnAuthCleared(null);
		disconnectQueryBridge();
		signalRService.disconnect();
	});
</script>

<AppShell>{@render children()}</AppShell>

<!-- Getting-started coachmark layer: a checklist deep-link (?coach=<key>) spotlights the matching
     data-coach element on whatever page it lands on. Mounted once for the whole authenticated app. -->
<CoachTrigger />
<CoachOverlay />
