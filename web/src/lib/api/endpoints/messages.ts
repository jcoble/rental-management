import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

/** Channels a message can be delivered on. Portal is the always-on base channel. */
export type MessageChannel = 'Portal' | 'Email' | 'Sms';

/** Who sent a given message in a conversation thread. */
export type SenderRole = 'Landlord' | 'Tenant';

/** A single message (bubble) inside a conversation thread. */
export interface ConversationMessage {
	id: number;
	senderRole: SenderRole;
	body: string;
	/** Comma-separated list of channels a landlord message was sent on (e.g. "Portal,Email"). */
	channels?: string;
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

export interface ConversationListResponse {
	items: ConversationSummary[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface ConversationUnreadCountResponse {
	count: number;
}

export interface StartConversationRequest {
	operationKey: string;
	tenantId: number;
	subject: string;
	body: string;
	channels: string[];
	/**
	 * Set true to send even when the Fair Housing review flags the message copy (the landlord
	 * reviewed the concerns and is overriding). Omitted/false → flagged copy is blocked with a 422
	 * carrying `fairHousingConcerns`.
	 */
	acknowledgedFairHousingReview?: boolean;
}

export interface SendMessageRequest {
	operationKey: string;
	body: string;
	channels: string[];
}

export const messages = {
	/** List all conversations, newest activity first. */
	list: () => api.get<ConversationSummary[]>('/conversations'),
	listPage: (params?: ListParams) =>
		api.get<ConversationListResponse>(`/conversations/page${buildListQuery(params)}`),
	unreadCount: () => api.get<ConversationUnreadCountResponse>('/conversations/unread-count'),
	/** Fetch one conversation's full history. Marks the thread read for the landlord. */
	get: (id: number) => api.get<Conversation>(`/conversations/${id}`),
	/** Start a new conversation with a tenant. */
	start: (data: StartConversationRequest) => api.post<Conversation>('/conversations', data),
	/** Reply to an existing conversation. */
	sendMessage: (id: number, data: SendMessageRequest) =>
		api.post<Conversation>(`/conversations/${id}/messages`, data),
};
