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

export const ai = {
	briefing: () => api.get<BriefingResponse>('/ai/briefing'),
	ask: (question: string, history?: QaTurn[]) =>
		api.post<AskResponse>('/ai/ask', { question, history }),
};
