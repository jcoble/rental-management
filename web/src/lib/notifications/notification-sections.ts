/** The sections shown on the one Notifications settings page, in the order they appear. */
export type NotificationSectionId = 'my-alerts' | 'tenant-notices' | 'team-routing';

/**
 * "Who gets told what" only makes sense once somebody else works in the workspace, so a
 * one-person workspace never sees it.
 */
export function notificationSections({
	hasTeamMembers
}: {
	hasTeamMembers: boolean;
}): NotificationSectionId[] {
	const sections: NotificationSectionId[] = ['my-alerts', 'tenant-notices'];
	if (hasTeamMembers) sections.push('team-routing');
	return sections;
}

/** Where a reply is delivered. Portal is the tenant app. */
export interface ReplyChannels {
	portal: boolean;
	email: boolean;
	sms: boolean;
}

/**
 * Pick how a reply should be delivered without asking. Repeat the way the last reply in the
 * thread went out; with nothing to repeat, use the tenant app plus text when we have a mobile
 * number.
 */
export function defaultReplyChannels({
	hasPhone,
	lastChannels = []
}: {
	hasPhone: boolean;
	lastChannels?: readonly string[];
}): ReplyChannels {
	const previous = lastChannels.map((channel) => channel.trim().toLowerCase());
	const repeated = {
		portal: previous.includes('portal'),
		email: previous.includes('email'),
		sms: previous.includes('sms')
	};
	if (repeated.portal || repeated.email || repeated.sms) return repeated;
	return { portal: true, email: false, sms: hasPhone };
}
