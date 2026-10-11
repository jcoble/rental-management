import { browser } from '$app/environment';
import { notifications } from '$lib/api/endpoints/notifications';
import { signalRService } from '$lib/realtime/signalr';
import type { NotificationItem } from '$lib/api/types/notification';

/**
 * Module-scoped singleton (L-13). Under adapter-node the server shares module scope across all
 * concurrent requests, so a server-side write would bleed one user's notifications into another's
 * SSR render. All writes flow from {@link initialize} (network + SignalR, inherently client-only)
 * or from user interactions; the entry point is hard-guarded to the browser. Reads stay unguarded —
 * components render the 0/[] defaults during SSR.
 */
class NotificationStore {
	private accessGeneration = 0;
	unreadCount = $state(0);
	recentNotifications = $state<NotificationItem[]>([]);
	isDropdownOpen = $state(false);
	activeDropdownId = $state<string | null>(null);
	private initialized = false;
	private unsubscribe: (() => void) | null = null;

	async initialize() {
		// Client-only: never seed/subscribe during SSR (shared server module scope → cross-request leak).
		if (!browser || this.initialized) return;
		this.initialized = true;
		await this.refresh();

		this.unsubscribe = signalRService.subscribe((event, payload) => {
			if (event === 'EntityUpdated' && payload.entityType === 'Notification') {
				this.refresh();
			}
		});
	}

	async refresh() {
		const accessGeneration = this.accessGeneration;
		try {
			const [count, recent] = await Promise.all([
				notifications.unreadCount(),
				notifications.list({ take: 20 })
			]);
			if (accessGeneration !== this.accessGeneration) return;
			this.unreadCount = count.count;
			this.recentNotifications = recent;
		} catch {
			/* notification UI should not block the app */
		}
	}

	async markAsRead(notificationId: number) {
		const accessGeneration = this.accessGeneration;
		try {
			await notifications.markAsRead(notificationId);
			if (accessGeneration !== this.accessGeneration) return;
			const idx = this.recentNotifications.findIndex((n) => n.id === notificationId);
			if (idx >= 0 && !this.recentNotifications[idx].isRead) {
				this.recentNotifications[idx] = { ...this.recentNotifications[idx], isRead: true };
				this.unreadCount = Math.max(0, this.unreadCount - 1);
			}
		} catch {
			/* best effort */
		}
	}

	async markAllAsRead() {
		const accessGeneration = this.accessGeneration;
		try {
			await notifications.markAllAsRead();
			if (accessGeneration !== this.accessGeneration) return;
			this.recentNotifications = this.recentNotifications.map((n) => ({ ...n, isRead: true }));
			this.unreadCount = 0;
		} catch {
			/* best effort */
		}
	}

	toggleDropdown(dropdownId = 'notification-dropdown') {
		if (this.isDropdownOpen && this.activeDropdownId === dropdownId) {
			this.closeDropdown();
			return;
		}
		this.activeDropdownId = dropdownId;
		this.isDropdownOpen = true;
		this.refresh();
	}

	closeDropdown() {
		this.isDropdownOpen = false;
		this.activeDropdownId = null;
	}

	destroy() {
		this.unsubscribe?.();
		this.unsubscribe = null;
		this.initialized = false;
	}

	resetForAccessChange() {
		this.accessGeneration++;
		this.unreadCount = 0;
		this.recentNotifications = [];
		this.isDropdownOpen = false;
		this.activeDropdownId = null;
		if (this.initialized) void this.refresh();
	}
}

export const notificationStore = new NotificationStore();
