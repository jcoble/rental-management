import { describe, expect, it, vi } from 'vitest';
import {
	activateAiCredential,
	activateAiDecisionCredential,
	removeAiCredential,
	removeAiDecisionCredential,
	rotateAiCredential,
	rotateAiDecisionCredential
} from '$lib/api/endpoints/ai-integrations';

const api = vi.hoisted(() => ({
	put: vi.fn().mockResolvedValue({}),
	delete: vi.fn().mockResolvedValue(undefined)
}));

vi.mock('$lib/api/client', () => ({ api }));

const extraction = { provider: 'openai', modelId: 'gpt-4o', apiKey: 'sk-test' } as const;
const decisions = { provider: 'typesafe', modelId: 'jev-1.13.0', apiKey: 'ts-test' } as const;

function idempotencyKey(options: RequestInit | undefined) {
	return (options?.headers as Record<string, string> | undefined)?.['Idempotency-Key'];
}

describe('AI key writes send a request key', () => {
	it.each([
		['activate extraction', () => activateAiCredential(extraction), '/integrations/ai'],
		['rotate extraction', () => rotateAiCredential(extraction), '/integrations/ai/rotate'],
		['activate decisions', () => activateAiDecisionCredential(decisions), '/integrations/ai/decisions'],
		['rotate decisions', () => rotateAiDecisionCredential(decisions), '/integrations/ai/decisions/rotate']
	])('%s', async (_name, call, path) => {
		await call();
		const [calledPath, , options] = api.put.mock.calls.at(-1)!;
		expect(calledPath).toBe(path);
		expect(idempotencyKey(options)).toMatch(/\S+/);
	});

	it.each([
		['remove extraction', () => removeAiCredential(), '/integrations/ai'],
		['remove decisions', () => removeAiDecisionCredential(), '/integrations/ai/decisions']
	])('%s', async (_name, call, path) => {
		await call();
		const [calledPath, options] = api.delete.mock.calls.at(-1)!;
		expect(calledPath).toBe(path);
		expect(idempotencyKey(options)).toMatch(/\S+/);
	});
});
