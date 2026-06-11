<script lang="ts">
	import { Bell } from '@lucide/svelte';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { notificationStore } from '$lib/stores/notifications.svelte';
	import NotificationDropdown from './NotificationDropdown.svelte';

	type Props = {
		'data-testid'?: string;
		placement?: 'up' | 'down';
	};

	let { 'data-testid': testId = 'notification-bell', placement = 'up' }: Props = $props();

	const auth = getAuthState();
	const dropdownId = $derived(`${testId}-dropdown`);
	const buttonLabelId = $derived(`${testId}-label`);

	$effect(() => {
		if (auth.isAuthenticated) {
			notificationStore.initialize();
		}
	});

</script>

<div class="notification-bell-root relative">
	<button
		onclick={() => notificationStore.toggleDropdown(dropdownId)}
		class="relative flex items-center justify-center rounded-md p-2 text-muted-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-foreground"
		aria-label="Notifications"
		aria-expanded={notificationStore.isDropdownOpen}
		aria-controls={notificationStore.isDropdownOpen ? dropdownId : undefined}
		data-testid={testId}
	>
		<span id={buttonLabelId} class="sr-only">Notifications</span>
		<Bell class="h-5 w-5" />
		{#if notificationStore.unreadCount > 0}
			<span
				class="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-[var(--m3c-error)] px-1 text-[10px] font-bold text-[var(--m3c-on-error)]"
				aria-hidden="true"
				data-testid="notification-badge"
			>
				{notificationStore.unreadCount > 99 ? '99+' : notificationStore.unreadCount}
			</span>
		{/if}
	</button>
	<span class="sr-only" role="status" aria-live="polite" aria-atomic="true">
		{notificationStore.unreadCount} unread notification{notificationStore.unreadCount === 1 ? '' : 's'}
	</span>

	{#if notificationStore.isDropdownOpen && notificationStore.activeDropdownId === dropdownId}
		<NotificationDropdown id={dropdownId} labelledBy={buttonLabelId} {placement} />
	{/if}
</div>
