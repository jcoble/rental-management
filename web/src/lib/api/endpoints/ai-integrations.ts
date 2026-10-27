import { api } from '$lib/api/client';
import { idempotentMutation } from '$lib/api/idempotency';

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
	idempotentMutation(`ai-integration:activate:${input.provider}:${input.modelId}`, (key) =>
		api.put<AiIntegrationStatus>('/integrations/ai', input, { headers: { 'Idempotency-Key': key } })
	);

export const rotateAiCredential = (input: AiCredentialInput) =>
	idempotentMutation(`ai-integration:rotate:${input.provider}:${input.modelId}`, (key) =>
		api.put<AiIntegrationStatus>('/integrations/ai/rotate', input, { headers: { 'Idempotency-Key': key } })
	);

export const removeAiCredential = () =>
	idempotentMutation('ai-integration:remove', (key) =>
		api.delete<void>('/integrations/ai', { headers: { 'Idempotency-Key': key } })
	);

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
	idempotentMutation(`ai-integration:decisions:activate:${input.provider}:${input.modelId}`, (key) =>
		api.put<AiDecisionStatus>('/integrations/ai/decisions', input, { headers: { 'Idempotency-Key': key } })
	);

export const rotateAiDecisionCredential = (input: AiDecisionCredentialInput) =>
	idempotentMutation(`ai-integration:decisions:rotate:${input.provider}:${input.modelId}`, (key) =>
		api.put<AiDecisionStatus>('/integrations/ai/decisions/rotate', input, { headers: { 'Idempotency-Key': key } })
	);

export const removeAiDecisionCredential = () =>
	idempotentMutation('ai-integration:decisions:remove', (key) =>
		api.delete<void>('/integrations/ai/decisions', { headers: { 'Idempotency-Key': key } })
	);
