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
}

export interface AiChatResponse {
	reply: string;
}

export const ai = {
	briefing: () => api.get<BriefingResponse>('/ai/briefing'),
	ask: (question: string, history?: QaTurn[]) =>
		api.post<AskResponse>('/ai/ask', { question, history }),
	chat: (message: string) => api.post<AiChatResponse>('/ai/chat', { message }),
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
