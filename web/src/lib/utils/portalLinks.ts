export function portalActionUrl(actionUrl: string | null | undefined): string {
	if (!actionUrl) return '/portal';
	if (actionUrl.startsWith('/portal')) return actionUrl;
	if (actionUrl.startsWith('/messages')) {
		const url = new URL(actionUrl, 'https://rentalcommand.local');
		const conversationId =
			url.searchParams.get('conversation') ?? url.searchParams.get('conversationId');
		return conversationId ? `/portal/messages?conversation=${conversationId}` : '/portal/messages';
	}
	return actionUrl;
}
