import { api } from '../client';
import type {
	NotificationEmailResponse,
	NotificationItem,
	NotificationListParams,
	NotificationSettingsResponse,
	UpdateNotificationSettingsRequest,
	UnreadCountResponse,
	TestSmsRequest,
	TestSmsResponse
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
	broadcast: (data: { title: string; message: string; severity?: string; actionUrl?: string | null }) =>
		api.post<NotificationItem>('/notifications/broadcast', data),
	getNotificationEmail: () => api.get<NotificationEmailResponse>('/notifications/email'),
	setNotificationEmail: (email: string | null) =>
		api.put<NotificationEmailResponse>('/notifications/email', { email }),
	getSettings: () => api.get<NotificationSettingsResponse>('/notifications/settings'),
	setSettings: (settings: UpdateNotificationSettingsRequest) =>
		api.put<NotificationSettingsResponse>('/notifications/settings', settings),
	testSms: (req: TestSmsRequest) =>
		api.post<TestSmsResponse>('/notifications/settings/test-sms', req)
};
