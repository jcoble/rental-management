<script lang="ts">
	// Owners/tenants get a self-contained portal. Staff users entering /portal
	// from the app keep the standard drawer shell so navigation does not disappear.
	//
	// We still wire SignalR realtime here (same as the staff `(protected)` layout)
	// so a landlord message lands live in the tenant's portal: the bridge maps a
	// `Conversation` event to invalidate the `['portal-conversations']` /
	// `['portal-conversation']` query keys (see $lib/realtime/invalidate.ts).
	import { onDestroy } from 'svelte';
	import { page } from '$app/stores';
	import { useQueryClient } from '@tanstack/svelte-query';
	import { setOnAuthCleared } from '$lib/stores/auth.svelte';
	import { signalRService } from '$lib/realtime/signalr';
	import { useInvalidateOnSignalR } from '$lib/realtime/invalidate';
	import { CLIENT_HUB_URL } from '$lib/config';
	import { isStaff } from '$lib/types/user';
	import AppShell from '$lib/components/AppShell.svelte';
	import type { LayoutData } from './$types';

	let { data, children }: { data: LayoutData; children: import('svelte').Snippet } = $props();
	let staffUser = $derived(isStaff(data.user ?? null));
	let isMessagesPage = $derived($page.url.pathname.startsWith('/portal/messages'));

	// Bridge SignalR data-update events to TanStack Query invalidation (wired once).
	const queryClient = useQueryClient();
	const disconnectQueryBridge = useInvalidateOnSignalR(queryClient);

	// Tear down the realtime connection when auth is cleared (logout / expiry).
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

	onDestroy(() => {
		setOnAuthCleared(null);
		disconnectQueryBridge();
		signalRService.disconnect();
	});
</script>

{#if staffUser}
	<AppShell>{@render children()}</AppShell>
{:else}
	<main class="min-h-screen overflow-y-auto bg-background {isMessagesPage ? '' : 'p-6'}">
		{@render children()}
	</main>
{/if}
