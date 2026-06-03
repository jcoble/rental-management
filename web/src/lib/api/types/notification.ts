export type NotificationSeverity = 'Info' | 'Success' | 'Warning' | 'Error';

export interface NotificationItem {
	id: number;
	type: string;
	title: string;
	message: string;
	severity: NotificationSeverity;
	actionUrl?: string | null;
	relatedEntityType?: string | null;
	relatedEntityId?: number | null;
	isRead: boolean;
	createdAt: string;
}

export interface NotificationListParams {
	unreadOnly?: boolean;
	take?: number;
	skip?: number;
}

export interface UnreadCountResponse {
	count: number;
}

export interface NotificationEmailResponse {
	email: string | null;
}
