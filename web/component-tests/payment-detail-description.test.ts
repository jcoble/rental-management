import { cleanup, render, waitFor } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import PaymentDetailHarness from './PaymentDetailHarness.svelte';

const mocks = vi.hoisted(() => ({ entry: vi.fn() }));

vi.mock('$lib/api/endpoints/tenant-accounts', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/tenant-accounts')>(
		'$lib/api/endpoints/tenant-accounts'
	);
	return {
		...actual,
		tenantAccounts: { ...actual.tenantAccounts, entry: mocks.entry }
	};
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
	});

	it('normalizes a legacy payment description at the PaymentDetail render site', async () => {
		const view = render(PaymentDetailHarness);

		await waitFor(() => expect(view.getByTestId('payment-card-receipt')).toBeTruthy());
		expect(view.getByTestId('payment-card-receipt').textContent).toContain('Rent payment for July 2026');
		expect(view.getByTestId('payment-card-receipt').textContent).not.toContain('Rent payment for 2026-07');
	});
});
