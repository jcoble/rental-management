export function readConversationId(searchParams: URLSearchParams): number | null {
	const raw = searchParams.get('conversation');
	if (!raw) return null;

	const id = Number(raw);
	return Number.isInteger(id) && id > 0 ? id : null;
}

export function conversationSelectionUrl(currentUrl: URL, conversationId: number | null): string {
	const next = new URL(currentUrl);
	if (conversationId === null) {
		next.searchParams.delete('conversation');
	} else {
		next.searchParams.set('conversation', String(conversationId));
	}

	const query = next.searchParams.toString();
	return `${next.pathname}${query ? `?${query}` : ''}${next.hash}`;
}
