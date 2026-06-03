<script lang="ts">
	import { onMount } from 'svelte';
	import { notificationStore } from '$lib/stores/notifications.svelte';
	import { getCurrentUser } from '$lib/stores/auth.svelte';
	import { isPortalUser, isStaff } from '$lib/types/user';
	import NotificationListItem from './NotificationListItem.svelte';

	type Props = {
		id?: string;
		labelledBy?: string;
		placement?: 'up' | 'down';
	};

	let { id = 'notification-dropdown', labelledBy, placement = 'up' }: Props = $props();
	let dropdownEl: HTMLDivElement | undefined = $state();
	const currentUser = $derived(getCurrentUser());
	const portalUser = $derived(isPortalUser(currentUser) && !isStaff(currentUser));

	function handleClickOutside(event: MouseEvent) {
		if (dropdownEl && !dropdownEl.closest('.notification-bell-root')?.contains(event.target as Node)) {
			notificationStore.closeDropdown();
		}
	}

	onMount(() => {
		const timer = setTimeout(() => {
			document.addEventListener('click', handleClickOutside, true);
		}, 0);
		return () => {
			clearTimeout(timer);
			document.removeEventListener('click', handleClickOutside, true);
		};
	});

	function handleRead(id: number) {
		notificationStore.markAsRead(id);
	}
</script>

<div
	id={id}
	bind:this={dropdownEl}
	class="absolute z-50 w-[calc(100vw-2rem)] max-w-96 rounded-lg border border-border bg-popover shadow-lg sm:w-96 {placement === 'down' ? 'right-0 top-full mt-2' : 'bottom-full left-0 mb-2'}"
	role="region"
	aria-labelledby={labelledBy}
	data-testid="notification-dropdown"
>
	<div class="flex items-center justify-between border-b border-border px-4 py-3">
		<h3 class="text-sm font-semibold text-popover-foreground">Notifications</h3>
		{#if notificationStore.unreadCount > 0}
			<button
				type="button"
				class="text-xs text-primary transition-colors hover:text-primary/80"
				onclick={() => notificationStore.markAllAsRead()}
				data-testid="notification-mark-all-read"
			>
				Mark all as read
			</button>
		{/if}
	</div>

	<div class="max-h-96 overflow-y-auto">
		{#if notificationStore.recentNotifications.length === 0}
			<div class="flex items-center justify-center py-10">
				<p class="text-sm text-muted-foreground">No notifications yet</p>
			</div>
		{:else}
			{#each notificationStore.recentNotifications as notification (notification.id)}
				<NotificationListItem {notification} onRead={handleRead} />
			{/each}
		{/if}
	</div>

	<div class="border-t border-border px-4 py-2.5">
		<a
			href={portalUser ? '/portal/notifications' : '/settings'}
			class="block text-center text-xs text-primary transition-colors hover:text-primary/80"
			onclick={() => notificationStore.closeDropdown()}
			data-testid="notification-view-settings"
		>
			{portalUser ? 'View notifications' : 'Notification settings'}
		</a>
	</div>
</div>
