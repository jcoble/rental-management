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

export type NotificationChannelType =
	| 'RentCharge'
	| 'LateFee'
	| 'LeaseExpiry'
	| 'RentConfirmation'
	| 'NoticeAutopilot'
	| 'DailyBriefing';

export interface NotificationChannelPreference {
	notificationType: NotificationChannelType;
	enableInApp: boolean;
	enableEmail: boolean;
	enableSms: boolean;
}

export interface NotificationSettingsResponse {
	enableRentCharges: boolean;
	enableLateFees: boolean;
	enableLeaseExpiryReminders: boolean;
	notifyTenants: boolean;
	rentChargeLeadDays: number;
	lateFeeGraceDays: number;
	leaseExpiryReminderDays: number;
	enableDailyBriefingMessages: boolean;
	dailyBriefingSendHourLocal: number;
	dailyBriefingIncludeEmpty: boolean;
	dailyBriefingSmsRecipients: string[];
	dailyBriefingEmailRecipients: string[];
	signalWireProjectId: string | null;
	signalWireTokenSet: boolean;
	signalWireToken: string | null;
	signalWireSpaceUrl: string | null;
	signalWireFromNumber: string | null;
	channelPreferences: NotificationChannelPreference[];
}

export interface UpdateNotificationSettingsRequest {
	enableRentCharges: boolean;
	enableLateFees: boolean;
	enableLeaseExpiryReminders: boolean;
	notifyTenants: boolean;
	rentChargeLeadDays: number;
	lateFeeGraceDays: number;
	leaseExpiryReminderDays: number;
	enableDailyBriefingMessages: boolean;
	dailyBriefingSendHourLocal: number;
	dailyBriefingIncludeEmpty: boolean;
	dailyBriefingSmsRecipients: string[];
	dailyBriefingEmailRecipients: string[];
	signalWireProjectId: string | null;
	signalWireToken?: string | null;
	signalWireSpaceUrl: string | null;
	signalWireFromNumber: string | null;
	channelPreferences: NotificationChannelPreference[];
}
