import { api } from '../client';
import type {
	NotificationEmailResponse,
	NotificationItem,
	NotificationListParams,
	UnreadCountResponse
} from '$lib/api/types/notification';

export const notifications = {
	list: (params: NotificationListParams = {}) => {
		const search = new URLSearchParams();
		if (params.unreadOnly) search.set('unreadOnly', 'true');
		if (params.take) search.set('take', String(params.take));
		if (params.skip) search.set('skip', String(params.skip));
		const query = search.toString();
		return api.get<NotificationItem[]>(`/notifications${query ? `?${query}` : ''}`);
	},

	unreadCount: () => api.get<UnreadCountResponse>('/notifications/unread-count'),
	markAsRead: (id: number) => api.post<void>(`/notifications/${id}/read`, {}),
	markAllAsRead: () => api.post<void>('/notifications/read-all', {}),
	getNotificationEmail: () => api.get<NotificationEmailResponse>('/notifications/email'),
	setNotificationEmail: (email: string | null) =>
		api.put<NotificationEmailResponse>('/notifications/email', { email })
};
