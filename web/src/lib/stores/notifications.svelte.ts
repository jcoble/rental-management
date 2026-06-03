import { notifications } from '$lib/api/endpoints/notifications';
import { signalRService } from '$lib/realtime/signalr';
import type { NotificationItem } from '$lib/api/types/notification';

class NotificationStore {
	unreadCount = $state(0);
	recentNotifications = $state<NotificationItem[]>([]);
	isDropdownOpen = $state(false);
	activeDropdownId = $state<string | null>(null);
	private initialized = false;
	private unsubscribe: (() => void) | null = null;

	async initialize() {
		if (this.initialized) return;
		this.initialized = true;
		await this.refresh();

		this.unsubscribe = signalRService.subscribe((event, payload) => {
			if (event === 'EntityUpdated' && payload.entityType === 'Notification') {
				this.refresh();
			}
		});
	}

	async refresh() {
		try {
			const [count, recent] = await Promise.all([
				notifications.unreadCount(),
				notifications.list({ take: 20 })
			]);
			this.unreadCount = count.count;
			this.recentNotifications = recent;
		} catch {
			/* notification UI should not block the app */
		}
	}

	async markAsRead(notificationId: number) {
		try {
			await notifications.markAsRead(notificationId);
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
		try {
			await notifications.markAllAsRead();
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
}

export const notificationStore = new NotificationStore();
