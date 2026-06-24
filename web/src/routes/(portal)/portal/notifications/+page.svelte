<script lang="ts">
	import { goto } from '$app/navigation';
	import { createQuery } from '@tanstack/svelte-query';
	import { notifications } from '$lib/api/endpoints/notifications';
	import type { NotificationItem } from '$lib/api/types/notification';
	import { notificationStore } from '$lib/stores/notifications.svelte';
	import { portalActionUrl } from '$lib/utils/portalLinks';
	import { BellRing } from '@lucide/svelte';

	const notificationsQuery = createQuery(() => ({
		queryKey: ['portal-notifications-page'],
		queryFn: () => notifications.list({ take: 50 })
	}));

	async function openNotification(item: NotificationItem) {
		if (!item.isRead) {
			await notificationStore.markAsRead(item.id);
			await notificationStore.refresh();
		}
		await goto(portalActionUrl(item.actionUrl), { invalidateAll: true });
	}
</script>

<svelte:head><title>Notifications - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-5 flex items-center gap-2">
		<BellRing class="h-5 w-5 text-primary" />
		<h1 class="text-2xl font-semibold">Notifications</h1>
	</div>
	<div class="space-y-3">
		{#each notificationsQuery.data ?? [] as item}
			<button
				type="button"
				class="block w-full rounded-lg border border-border bg-card p-4 text-left hover:bg-muted/40"
				onclick={() => openNotification(item)}
			>
				<p class="font-medium">{item.title}</p>
				<p class="mt-1 text-sm text-muted-foreground">{item.message}</p>
			</button>
		{:else}
			<p class="text-sm text-muted-foreground">No notifications yet.</p>
		{/each}
	</div>
</div>
