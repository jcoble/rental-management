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
