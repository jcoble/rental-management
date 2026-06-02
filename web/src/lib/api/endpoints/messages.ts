import { api } from '../client';

/** Channels a message can be delivered on. Portal is the always-on base channel. */
export type MessageChannel = 'Portal' | 'Email' | 'Sms';

/** Who sent a given message in a conversation thread. */
export type SenderRole = 'Landlord' | 'Tenant';

/** A single message (bubble) inside a conversation thread. */
export interface ConversationMessage {
	id: number;
	senderRole: SenderRole;
	body: string;
	channels?: string[];
	createdAt: string;
}

/** Thread-list row: one conversation summarized for the left pane. */
export interface ConversationSummary {
	id: number;
	tenantId: number;
	tenantName: string;
	subject: string;
	propertyName?: string;
	lastMessagePreview?: string;
	lastMessageAt: string;
	unreadCount: number;
	messageCount: number;
}

/** Full conversation: the summary plus the chronological message history. */
export interface Conversation extends ConversationSummary {
	messages: ConversationMessage[];
}

export interface StartConversationRequest {
	tenantId: number;
	subject: string;
	body: string;
	channels: string[];
}

export interface SendMessageRequest {
	body: string;
	channels: string[];
}

export const messages = {
	/** List all conversations, newest activity first. */
	list: () => api.get<ConversationSummary[]>('/conversations'),
	/** Fetch one conversation's full history. Marks the thread read for the landlord. */
	get: (id: number) => api.get<Conversation>(`/conversations/${id}`),
	/** Start a new conversation with a tenant. */
	start: (data: StartConversationRequest) => api.post<Conversation>('/conversations', data),
	/** Reply to an existing conversation. */
	sendMessage: (id: number, data: SendMessageRequest) =>
		api.post<Conversation>(`/conversations/${id}/messages`, data),
};
