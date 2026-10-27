import { api } from '$lib/api/client';

export type AiProvider = 'openai' | 'anthropic';

export type AiIntegrationStatus = {
	configured: boolean;
	provider: AiProvider | null;
	modelId: string | null;
	lastTestedAtUtc: string | null;
	updatedAtUtc: string | null;
};

export type AiCredentialInput = {
	provider: AiProvider;
	modelId: string;
	apiKey: string;
};

export type AiCredentialTestResult = {
	succeeded: boolean;
	provider: AiProvider;
	modelId: string;
	error: string | null;
};

export const getAiIntegrationStatus = () =>
	api.get<AiIntegrationStatus>('/integrations/ai');

export const testAiCredential = (input: AiCredentialInput) =>
	api.post<AiCredentialTestResult>('/integrations/ai/test', input);

export const activateAiCredential = (input: AiCredentialInput) =>
	api.put<AiIntegrationStatus>('/integrations/ai', input);

export const rotateAiCredential = (input: AiCredentialInput) =>
	api.put<AiIntegrationStatus>('/integrations/ai/rotate', input);

export const removeAiCredential = () =>
	api.delete<void>('/integrations/ai');

export type AiDecisionProvider = 'typesafe' | 'openrouter';

export type AiDecisionStatus = {
	configured: boolean;
	provider: AiDecisionProvider | null;
	modelId: string | null;
	lastTestedAtUtc: string | null;
	updatedAtUtc: string | null;
};

export type AiDecisionCredentialInput = {
	provider: AiDecisionProvider;
	modelId: string;
	apiKey: string;
};

export type AiDecisionCredentialTestResult = {
	succeeded: boolean;
	provider: AiDecisionProvider;
	modelId: string;
	error: string | null;
};

export const getAiDecisionStatus = () =>
	api.get<AiDecisionStatus>('/integrations/ai/decisions');

export const testAiDecisionCredential = (input: AiDecisionCredentialInput) =>
	api.post<AiDecisionCredentialTestResult>('/integrations/ai/decisions/test', input);

export const activateAiDecisionCredential = (input: AiDecisionCredentialInput) =>
	api.put<AiDecisionStatus>('/integrations/ai/decisions', input);

export const rotateAiDecisionCredential = (input: AiDecisionCredentialInput) =>
	api.put<AiDecisionStatus>('/integrations/ai/decisions/rotate', input);

export const removeAiDecisionCredential = () =>
	api.delete<void>('/integrations/ai/decisions');
