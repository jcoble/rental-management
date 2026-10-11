import { cleanup, fireEvent, render, waitFor } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import PaymentDetailHarness from './PaymentDetailHarness.svelte';

const mocks = vi.hoisted(() => ({ entry: vi.fn(), account: vi.fn() }));

vi.mock('$lib/api/endpoints/tenant-accounts', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/tenant-accounts')>(
		'$lib/api/endpoints/tenant-accounts'
	);
	return {
		...actual,
		tenantAccounts: { ...actual.tenantAccounts, entry: mocks.entry, get: mocks.account }
	};
});

vi.mock('$lib/stores/auth.svelte', async () => {
	const actual = await vi.importActual<typeof import('$lib/stores/auth.svelte')>('$lib/stores/auth.svelte');
	return { ...actual, currentCapabilities: () => new Set(['money.payments.manage']) };
});

afterEach(() => cleanup());

describe('payment detail description display', () => {
	beforeEach(() => {
		mocks.entry.mockResolvedValue({
			tenantAccountId: 20,
			leaseManagementId: 10,
			tenantLedgerEntryId: 52,
			publicId: 'payment-52',
			entryType: 'PaymentReceipt',
			direction: 'Credit',
			amount: 1950,
			currency: 'USD',
			effectiveOn: '2026-07-15',
			dueOn: null,
			postedAtUtc: '2026-07-15T12:00:00Z',
			description: 'Rent payment for 2026-07',
			businessKey: 'demo:rent-payment:2026-07',
			reversesEntryId: null,
			hasReversal: false,
			providerPaymentAttemptId: null,
			sourceStoredFileId: null,
			portfolioId: 1,
			tenantName: 'Ada Lovelace',
			propertyId: 7,
			propertyName: 'Example House',
			unitId: 8,
			unitNumber: '1A',
			accountNumber: 'TA-52',
			relationshipNumber: 'LM-52',
			primaryTenantName: 'Ada Lovelace',
			providerAttempt: null,
			sourceFile: null,
			filteredTotalCount: 1,
			monthCharges: 0,
			monthPaymentsAndCredits: 1950
		});
		mocks.account.mockResolvedValue({ businessDate: '2027-02-28' });
	});

	it('normalizes a legacy payment description at the PaymentDetail render site', async () => {
		const view = render(PaymentDetailHarness);

		await waitFor(() => expect(view.getByTestId('payment-card-receipt')).toBeTruthy());
		expect(view.getByTestId('payment-card-receipt').textContent).toContain('Rent payment for July 2026');
		expect(view.getByTestId('payment-card-receipt').textContent).not.toContain('Rent payment for 2026-07');
	});

	it('waits for the account date before allowing correction and uses it when it arrives', async () => {
		let resolveAccount!: (value: { businessDate: string }) => void;
		mocks.account.mockReturnValueOnce(new Promise((resolve) => { resolveAccount = resolve; }));
		const view = render(PaymentDetailHarness);

		const action = await waitFor(() => view.getByTestId('correct-payment-action') as HTMLButtonElement);
		expect(action.disabled).toBe(true);

		resolveAccount({ businessDate: '2027-02-28' });
		await waitFor(() => expect(action.disabled).toBe(false));
		await fireEvent.click(action);
		await waitFor(() => expect(view.getByTestId('payment-correction-form')).toBeTruthy());
		expect((view.getByTestId('payment-correction-form').querySelector('input') as HTMLInputElement).value).toBe('02/28/2027');
	});

	it('opens with the local fallback only after a failed account-date query', async () => {
		let rejectAccount!: (reason?: unknown) => void;
		mocks.account.mockReturnValueOnce(new Promise((_resolve, reject) => { rejectAccount = reject; }));
		const view = render(PaymentDetailHarness);

		await waitFor(() => expect((view.getByTestId('correct-payment-action') as HTMLButtonElement).disabled).toBe(true));
		rejectAccount(new Error('account unavailable'));
		await waitFor(() => expect((view.getByTestId('correct-payment-action') as HTMLButtonElement).disabled).toBe(false));
		await fireEvent.click(view.getByTestId('correct-payment-action'));
		await waitFor(() => expect(view.getByTestId('payment-correction-form')).toBeTruthy());
		expect((view.getByTestId('payment-correction-form').querySelector('input') as HTMLInputElement).value).toMatch(/^\d{2}\/\d{2}\/\d{4}$/);
	});
});
