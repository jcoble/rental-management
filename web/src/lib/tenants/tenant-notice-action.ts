export interface TenantNoticeAction {
	noticeType?: string;
}

const FORCEABLE_NOTICE_TYPES = new Set([
	'rent-reminder',
	'lease-renewal-offer',
	'month-to-month-offer',
	'lease-non-renewal',
	'late-rent-late-fee'
]);

export function readTenantNoticeAction(params: URLSearchParams): TenantNoticeAction | null {
	if (params.get('action') !== 'create-notice') return null;
	const noticeType = params.get('noticeType')?.trim();
	return noticeType && FORCEABLE_NOTICE_TYPES.has(noticeType) ? { noticeType } : {};
}

export function tenantNoticeActionKey(
	tenantId: number | string,
	action: TenantNoticeAction
): string {
	return `${tenantId}:${action.noticeType ?? 'all'}`;
}

export function clearTenantNoticeActionUrl(url: URL): string {
	const next = new URL(url);
	next.searchParams.delete('action');
	next.searchParams.delete('noticeType');

	const search = next.searchParams.toString();
	return `${next.pathname}${search ? `?${search}` : ''}${next.hash}`;
}
