import { cleanup, render } from '@testing-library/svelte';
import { afterEach, describe, expect, it } from 'vitest';
import TenantLedgerMonth from '$lib/components/accounting/TenantLedgerMonth.svelte';
import TenantPaymentAllocationDetails from '$lib/components/accounting/TenantPaymentAllocationDetails.svelte';

afterEach(() => cleanup());

const summary = {
	year: 2027,
	month: 2,
	currency: 'USD',
	openingBalance: 0,
	chargeAmount: 1200,
	paymentAmount: 600,
	creditAmount: 0,
	closingBalance: 600,
};

function paymentRow(allocations: Array<Record<string, unknown>>) {
	return {
		tenantLedgerEntryId: 42,
		publicId: 'payment-42',
		sourceType: 'tenant-ledger',
		sourceId: 42,
		sourcePublicId: 'payment-42',
		effectiveOn: '2027-02-15',
		postedAtUtc: '2027-02-15T12:00:00Z',
		type: 'PaymentReceipt' as const,
		description: 'Partial rent payment',
		chargeAmount: 0,
		paymentAmount: 600,
		creditAmount: 0,
		runningAmountOwed: 600,
		dueOn: null,
		openAmount: 0,
		status: 'Settled',
		paymentMethod: 'Check',
		reference: '1042',
		accountLabel: 'PaymentReceipt',
		recurringScheduleContext: null,
		sourceDocumentContext: null,
		allocations,
		reversesEntryId: null,
		replacedByEntryId: null,
		journalEntryPublicId: null,
		currency: 'USD',
		relatedTenantLedgerEntryId: null,
		relatedEntryDescription: null,
		categoryName: null,
		servicePeriodStartOn: null,
		servicePeriodEndOn: null,
	};
}

describe('internal tenant ledger allocation display', () => {
	it('renders every allocation with charge label, date, and amount on payment rows', () => {
		const view = render(TenantLedgerMonth, {
			props: {
				summary,
				rows: [paymentRow([
					{
						targetSourceId: 7,
						targetPublicId: 'charge-7',
						targetDescription: 'January rent',
						amount: 400,
						effectiveOn: '2027-01-01',
					},
					{
						targetSourceId: 8,
						targetPublicId: 'charge-8',
						targetDescription: 'February rent',
						amount: 200,
						effectiveOn: '2027-02-01',
					},
				])],
			},
		});

		expect(view.getByTestId('tenant-ledger-payment-allocations-42')).toBeTruthy();
		expect(view.getByTestId('tenant-ledger-payment-allocation-7').textContent).toContain('January rent');
		expect(view.getByTestId('tenant-ledger-payment-allocation-7').textContent).toContain('Jan 1, 2027');
		expect(view.getByTestId('tenant-ledger-payment-allocation-7').textContent).toContain('$400.00');
		expect(view.getByTestId('tenant-ledger-payment-allocation-8').textContent).toContain('February rent');
		expect(view.getByTestId('tenant-ledger-payment-allocation-8').textContent).toContain('$200.00');
	});

	it('renders no allocation detail when a payment has no allocations', () => {
		const view = render(TenantLedgerMonth, {
			props: { summary, rows: [paymentRow([])] },
		});

		expect(view.queryByTestId('tenant-ledger-payment-allocations-42')).toBeNull();
	});

	it('renders the same allocation detail on the tenant-facing ledger surface', () => {
		const view = render(TenantPaymentAllocationDetails, {
			props: {
				testid: 'portal-payment-allocations-42',
				currency: 'USD',
				allocations: [{
					targetSourceId: 7,
					targetPublicId: 'charge-7',
					targetDescription: 'January rent',
					amount: 400,
					effectiveOn: '2027-01-01',
				}]
			}
		});

		expect(view.getByTestId('portal-payment-allocations-42')).toBeTruthy();
		expect(view.getByTestId('tenant-ledger-payment-allocation-7').textContent).toContain('$400.00');
	});

	it('renders no extra allocation detail on the tenant-facing ledger when absent', () => {
		const view = render(TenantPaymentAllocationDetails, {
			props: { testid: 'portal-payment-allocations-42', allocations: [], currency: 'USD' },
		});

		expect(view.queryByTestId('portal-payment-allocations-42')).toBeNull();
	});
});
