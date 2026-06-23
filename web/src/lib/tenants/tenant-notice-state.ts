export type TenantNoticeEmptyCopy = {
	message: string;
	description: string;
};

export function getTenantNoticeEmptyCopy(forcedNoticeLabel?: string | null): TenantNoticeEmptyCopy {
	if (forcedNoticeLabel) {
		return {
			message: `No ${forcedNoticeLabel.toLowerCase()} could be created.`,
			description: 'This tenant needs an active eligible lease for that notice type.',
		};
	}

	return {
		message: 'No notices are due for this tenant right now.',
		description: 'Renewal, late-rent, and move-out notices appear here automatically when they come due.',
	};
}
