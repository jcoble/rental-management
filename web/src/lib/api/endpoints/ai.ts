import { api } from '../client';

export const ai = {
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
