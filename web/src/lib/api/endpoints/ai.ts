import { api, fetchApi } from '../client';
import type { DocCitation } from './docs';

export type { DocCitation } from './docs';

const AI_ASK_TIMEOUT_MS = 120_000;

export interface BriefingBullet {
	title: string;
	detail: string;
	category: string;
	severity: 'info' | 'warning' | 'critical';
	entityType?: string | null;
	entityId?: number | null;
	unitId?: number | null;
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
	/** Where the answer came from: portfolio "Data" or the "Docs" knowledge base. */
	source?: 'Data' | 'Docs' | null;
	/** When `source === 'Docs'`, the knowledge-base articles the answer drew on. */
	citations?: DocCitation[] | null;
}

export type AssistantActionKind = 'Unsupported' | 'CreateExpense';
export type AssistantActionRisk = 'Low' | 'Medium' | 'High';
export type AssistantActionStatus =
	| 'DraftReady'
	| 'MissingRequiredFields'
	| 'WriteModeRequired'
	| 'Unsupported'
	| 'NotConfirmed'
	| 'InvalidDraft'
	| 'Created';

export interface AssistantExpenseDraft {
	propertyId?: number | null;
	propertyName?: string | null;
	category: string;
	status: string;
	description: string;
	amount?: number | null;
	incurredAt: string;
	paidAt?: string | null;
	notes?: string | null;
}

export interface AssistantActionDraft {
	kind: AssistantActionKind;
	risk: AssistantActionRisk;
	summary: string;
	expense?: AssistantExpenseDraft | null;
}

export interface AssistantActionDraftResponse {
	status: AssistantActionStatus;
	message: string;
	requiresWriteMode: boolean;
	canExecute: boolean;
	draft?: AssistantActionDraft | null;
	missingFields: string[];
	options: Array<{ field: string; id: number; label: string }>;
}

export interface AssistantActionExecuteResponse {
	status: AssistantActionStatus;
	message: string;
	kind: AssistantActionKind;
	entityId?: number | null;
	detailHref?: string | null;
	expense?: unknown;
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
		fetchApi<AskResponse>('/ai/ask', {
			method: 'POST',
			body: JSON.stringify({ question, history, ...delivery }),
			timeoutMs: AI_ASK_TIMEOUT_MS
		}),
	draftAction: (command: string, writeModeEnabled: boolean) =>
		api.post<AssistantActionDraftResponse>('/ai/actions/draft', {
			command,
			writeModeEnabled
		}),
	executeAction: (draft: AssistantActionDraft, writeModeEnabled: boolean) =>
		api.post<AssistantActionExecuteResponse>('/ai/actions/execute', {
			draft,
			writeModeEnabled,
			confirmed: true
		}),
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
