export interface ConversationReadSource {
	id: number;
	messageCount: number;
	lastMessageAt: string;
}

export interface ConversationUnreadItem {
	id: number;
	unreadCount: number;
}

export function conversationReadKey(conversation: ConversationReadSource | null | undefined): string | null {
	if (!conversation) return null;
	return `${conversation.id}:${conversation.messageCount}:${conversation.lastMessageAt}`;
}

export function markConversationRead<T extends ConversationUnreadItem>(
	items: readonly T[],
	conversationId: number
): T[] {
	return items.map((item) =>
		item.id === conversationId && item.unreadCount !== 0
			? { ...item, unreadCount: 0 }
			: item
	);
}
