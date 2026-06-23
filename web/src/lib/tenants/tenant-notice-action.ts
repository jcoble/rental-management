export interface TenantNoticeAction {
	noticeType?: string;
}

const FORCEABLE_NOTICE_TYPES = new Set(['RenewalOffer', 'MoveOutReminder']);

export function readTenantNoticeAction(params: URLSearchParams): TenantNoticeAction | null {
	if (params.get('action') !== 'create-notice') return null;
	const noticeType = params.get('noticeType')?.trim();
	return noticeType && FORCEABLE_NOTICE_TYPES.has(noticeType) ? { noticeType } : {};
}
