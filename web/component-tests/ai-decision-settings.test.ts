import { cleanup, fireEvent, render, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import AiSettingsPageHarness from './AiSettingsPageHarness.svelte';

const mocks = vi.hoisted(() => ({
	getAiIntegrationStatus: vi.fn(),
	testAiCredential: vi.fn(),
	activateAiCredential: vi.fn(),
	rotateAiCredential: vi.fn(),
	removeAiCredential: vi.fn(),
	getAiDecisionStatus: vi.fn(),
	testAiDecisionCredential: vi.fn(),
	activateAiDecisionCredential: vi.fn(),
	rotateAiDecisionCredential: vi.fn(),
	removeAiDecisionCredential: vi.fn()
}));

vi.mock('$lib/api/endpoints/ai-integrations', () => mocks);

const notConfigured = {
	configured: false,
	provider: null,
	modelId: null,
	lastTestedAtUtc: null,
	updatedAtUtc: null
};

const configured = {
	configured: true,
	provider: 'typesafe',
	modelId: 'jev-1.13.0',
	lastTestedAtUtc: '2026-10-27T12:00:00Z',
	updatedAtUtc: '2026-10-27T12:00:00Z'
};

const expectedInput = { provider: 'typesafe', modelId: 'jev-1.13.0', apiKey: 'ts-key-123' };

afterEach(() => cleanup());

async function renderDecisionCard() {
	const view = render(AiSettingsPageHarness);
	const card = await waitFor(() => view.getByTestId('ai-decision-settings'));
	await waitFor(() => expect(within(card).getByLabelText(/^API key/)).toBeTruthy());
	return within(card);
}

async function checkKey(card: ReturnType<typeof within>) {
	await fireEvent.input(card.getByLabelText(/^API key/), { target: { value: 'ts-key-123' } });
	await fireEvent.click(card.getByTestId('ai-decision-check'));
	await waitFor(() => expect(mocks.testAiDecisionCredential).toHaveBeenCalledWith(expectedInput));
	await waitFor(() => expect((card.getByTestId('ai-decision-save') as HTMLButtonElement).disabled).toBe(false));
}

function expectExistingCardUntouched() {
	expect(mocks.testAiCredential).not.toHaveBeenCalled();
	expect(mocks.activateAiCredential).not.toHaveBeenCalled();
	expect(mocks.rotateAiCredential).not.toHaveBeenCalled();
	expect(mocks.removeAiCredential).not.toHaveBeenCalled();
}

describe('faster document sorting settings', () => {
	beforeEach(() => {
		mocks.getAiIntegrationStatus.mockResolvedValue(notConfigured);
		mocks.testAiDecisionCredential.mockResolvedValue({ succeeded: true, provider: 'typesafe', modelId: 'jev-1.13.0', error: null });
		mocks.activateAiDecisionCredential.mockResolvedValue(configured);
		mocks.rotateAiDecisionCredential.mockResolvedValue(configured);
		mocks.removeAiDecisionCredential.mockResolvedValue(undefined);
	});

	it('saves a new Jev key only after it has been checked', async () => {
		mocks.getAiDecisionStatus.mockResolvedValue(notConfigured);
		const card = await renderDecisionCard();

		expect(card.getByText('Not connected')).toBeTruthy();
		expect((card.getByTestId('ai-decision-save') as HTMLButtonElement).disabled).toBe(true);

		await checkKey(card);
		await fireEvent.click(card.getByTestId('ai-decision-save'));

		await waitFor(() => expect(mocks.activateAiDecisionCredential).toHaveBeenCalledWith(expectedInput));
		expect(mocks.rotateAiDecisionCredential).not.toHaveBeenCalled();
		expectExistingCardUntouched();
	});

	it('replaces and removes a connected Jev key', async () => {
		mocks.getAiDecisionStatus.mockResolvedValue(configured);
		const card = await renderDecisionCard();

		await waitFor(() => expect(card.getByText('TypeSafe · jev-1.13.0')).toBeTruthy());
		await checkKey(card);
		await fireEvent.click(card.getByTestId('ai-decision-save'));
		await waitFor(() => expect(mocks.rotateAiDecisionCredential).toHaveBeenCalledWith(expectedInput));
		expect(mocks.activateAiDecisionCredential).not.toHaveBeenCalled();

		await fireEvent.click(card.getByTestId('ai-decision-remove'));
		await waitFor(() => expect(mocks.removeAiDecisionCredential).toHaveBeenCalled());
		expectExistingCardUntouched();
	});
});
