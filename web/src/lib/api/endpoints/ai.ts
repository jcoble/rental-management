import { api } from '../client';

export interface AiChatResponse {
	reply: string;
}

export const ai = {
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
