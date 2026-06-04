import { api } from '../client';

export interface BriefingBullet {
	title: string;
	detail: string;
	category: string;
	severity: 'info' | 'warning' | 'critical';
	entityType?: string | null;
	entityId?: number | null;
}

export interface BriefingResponse {
	date: string;
	generatedAt: string;
	summary: string | null;
	llmEnhanced: boolean;
	bullets: BriefingBullet[];
}

export interface QaTurn {
	role: 'user' | 'assistant';
	content: string;
}

export interface AskResponse {
	answer: string;
	toolsUsed: string[];
	llmAvailable: boolean;
	tokensUsed: number;
	modelId: string;
	/** Channels the answer was queued for delivery on (e.g. "Email", "Sms"). */
	deliveredChannels?: string[] | null;
}

/** Optional "text me / email me this answer" delivery options for POST /ai/ask. */
export interface AskDelivery {
	deliverViaEmail?: boolean;
	deliverViaSms?: boolean;
	/** Override email recipient; defaults to the signed-in user's email server-side. */
	deliverToEmail?: string;
	/** Override phone recipient; defaults to a configured owner phone server-side. */
	deliverToPhone?: string;
}

export interface AiChatResponse {
	reply: string;
}

/** A single phrase flagged by the Fair Housing review and why it is a risk. */
export interface FairHousingIssue {
	phrase: string;
	concern: string;
}

/**
 * Result of a Fair Housing Act copy review. When `reviewed` is false (no AI key configured), the
 * copy was NOT checked — never present it as compliant; show "AI review unavailable" instead.
 */
export interface FairHousingReviewResult {
	reviewed: boolean;
	compliant: boolean;
	issues: FairHousingIssue[];
	suggestedRewrite?: string | null;
}

export const ai = {
	briefing: () => api.get<BriefingResponse>('/ai/briefing'),
	ask: (question: string, history?: QaTurn[], delivery?: AskDelivery) =>
		api.post<AskResponse>('/ai/ask', { question, history, ...delivery }),
	chat: (message: string) => api.post<AiChatResponse>('/ai/chat', { message }),
	fairHousingCheck: (text: string) =>
		api.post<FairHousingReviewResult>('/ai/fair-housing-check', { text }),
	intake: (portfolioId: number, message: string) =>
		api.post('/ai/intake', { portfolioId, message }),
	portfolioSummary: (portfolioId: number) =>
		api.get(`/ai/portfolio-summary/${portfolioId}`),
	generateNotice: (data: {
		noticeType: string;
		recipientName: string;
		senderName: string;
		dueDate: string;
		amount?: number;
		context?: string;
	}) => api.post('/ai/generate-notice', data),
};
