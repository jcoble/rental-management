export type NotificationSeverity = 'Info' | 'Success' | 'Warning' | 'Error' | 'Critical';

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
	// Push (mobile FCM/APNs) channel. Browser web push is not supported by this setting.
	enablePush: boolean;
}

// Single auto action taken when a lease is ending:
// Draft = just draft it for review; Renewal/MonthToMonth/NonRenewal = auto-send that offer/notice.
export type LeaseEndAutoAction = 'Draft' | 'Renewal' | 'MonthToMonth' | 'NonRenewal';

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
	smsProvider: string;
	smsFromNumber: string | null;
	smsCredentialASet: boolean;
	smsCredentialBSet: boolean;
	smsCredentialCSet: boolean;
	// Recurring notices: true = auto-send the approved draft, false = ask the landlord first.
	autoSendRentReminder: boolean;
	autoSendLateRent: boolean;
	// What to do automatically when a lease is ending (single action; default 'Draft').
	leaseEndAutoAction: LeaseEndAutoAction;
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
	smsProvider: string;
	smsFromNumber: string | null;
	// Write-only secrets: undefined = keep saved value, '' = clear, value = set.
	smsCredentialA?: string | null;
	smsCredentialB?: string | null;
	smsCredentialC?: string | null;
	// Recurring notices: true = auto-send the approved draft, false = ask the landlord first.
	autoSendRentReminder: boolean;
	autoSendLateRent: boolean;
	// What to do automatically when a lease is ending (single action; default 'Draft').
	leaseEndAutoAction: LeaseEndAutoAction;
	channelPreferences: NotificationChannelPreference[];
}

/** SMS provider option metadata: labels for the three generic credential slots per provider. */
export interface SmsProviderMeta {
	key: string;
	label: string;
	/** Per-slot config; null = slot unused by this provider. */
	credentialA: { label: string; placeholder?: string } | null;
	credentialB: { label: string; placeholder?: string } | null;
	credentialC: { label: string; placeholder?: string } | null;
}

export interface TestSmsRequest {
	smsProvider: string;
	toPhoneNumber: string;
	smsFromNumber?: string | null;
	smsCredentialA?: string | null;
	smsCredentialB?: string | null;
	smsCredentialC?: string | null;
}

export interface TestSmsResponse {
	success: boolean;
	message: string;
}
