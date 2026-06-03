<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { notifications } from '$lib/api/endpoints/notifications';
	import { portalActionUrl } from '$lib/utils/portalLinks';
	import { BellRing } from '@lucide/svelte';

	const notificationsQuery = createQuery(() => ({
		queryKey: ['portal-notifications-page'],
		queryFn: () => notifications.list({ take: 50 })
	}));
</script>

<svelte:head><title>Notifications - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-5 flex items-center gap-2">
		<BellRing class="h-5 w-5 text-primary" />
		<h1 class="text-2xl font-semibold">Notifications</h1>
	</div>
	<div class="space-y-3">
		{#each notificationsQuery.data ?? [] as item}
			<a href={portalActionUrl(item.actionUrl)} class="block rounded-lg border border-border bg-card p-4 hover:bg-muted/40">
				<p class="font-medium">{item.title}</p>
				<p class="mt-1 text-sm text-muted-foreground">{item.message}</p>
			</a>
		{:else}
			<p class="text-sm text-muted-foreground">No notifications yet.</p>
		{/each}
	</div>
</div>
