import { cleanup, render } from '@testing-library/svelte';
import { afterEach, describe, expect, it } from 'vitest';
import TenantLedgerMonth from '$lib/components/accounting/TenantLedgerMonth.svelte';
import PortalAccountHistoryRows from '$lib/components/accounting/PortalAccountHistoryRows.svelte';
import type { PortalTenantAccountHistory } from '$lib/api/endpoints/portal';

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
						allocationId: 700,
						targetSourceId: 7,
						targetPublicId: 'charge-7',
						targetDescription: 'January rent',
						amount: 400,
						effectiveOn: '2027-01-01',
					},
					{
						allocationId: 701,
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
		expect(view.getByTestId('tenant-ledger-payment-allocation-700').textContent).toContain('January rent');
		expect(view.getByTestId('tenant-ledger-payment-allocation-700').textContent).toContain('Jan 1, 2027');
		expect(view.getByTestId('tenant-ledger-payment-allocation-700').textContent).toContain('$400.00');
		expect(view.getByTestId('tenant-ledger-payment-allocation-701').textContent).toContain('February rent');
		expect(view.getByTestId('tenant-ledger-payment-allocation-701').textContent).toContain('$200.00');
	});

	it('renders no allocation detail when a payment has no allocations', () => {
		const view = render(TenantLedgerMonth, {
			props: { summary, rows: [paymentRow([])] },
		});

		expect(view.queryByTestId('tenant-ledger-payment-allocations-42')).toBeNull();
	});

	it('renders mixed tenant-facing portal history rows with every allocation fact and no empty wrapper', () => {
		const history: PortalTenantAccountHistory = {
			tenantAccountId: 20,
			leaseManagementId: 10,
			currency: 'USD',
			businessDate: '2027-02-28',
			period: 'all',
			periodFrom: null,
			periodTo: '2027-02-28',
			currentDue: 0,
			beginningBalance: 1200,
			closingBalance: 0,
			totalCount: 2,
			skip: 0,
			take: 20,
			items: [
				{
					tenantLedgerEntryId: 42,
					entryType: 'PaymentReceipt',
					direction: 'Credit',
					displayType: 'PaymentReceipt',
					description: 'Partial rent payment',
					effectiveOn: '2027-02-15',
					postedAtUtc: '2027-02-15T12:00:00Z',
					signedAmount: -600,
					runningBalance: 600,
					openAmount: 0,
					payable: false,
					reversesEntryId: null,
					reversedByEntryId: null,
					isFocused: false,
					allocations: [
						{ allocationId: 700, targetSourceId: 7, targetPublicId: 'charge-7', targetDescription: 'January rent', amount: 400, effectiveOn: '2027-01-01' },
						{ allocationId: 701, targetSourceId: 7, targetPublicId: 'charge-7', targetDescription: 'January rent reversal', amount: -100, effectiveOn: '2027-02-16' },
					],
				},
				{
					tenantLedgerEntryId: 43,
					entryType: 'PaymentReceipt',
					direction: 'Credit',
					displayType: 'PaymentReceipt',
					description: 'Unallocated payment',
					effectiveOn: '2027-02-20',
					postedAtUtc: '2027-02-20T12:00:00Z',
					signedAmount: -25,
					runningBalance: 575,
					openAmount: 0,
					payable: false,
					reversesEntryId: null,
					reversedByEntryId: null,
					isFocused: false,
					allocations: [],
				},
			],
		};

		const view = render(PortalAccountHistoryRows, {
			props: {
				history,
				onlinePaymentsUnavailable: false,
				payPending: false,
				payingId: null,
				onpay: () => undefined,
			},
		});

		expect(view.getByTestId('portal-payment-allocations-42')).toBeTruthy();
		expect(view.getByTestId('tenant-ledger-payment-allocation-700').textContent).toContain('$400.00');
		expect(view.getByTestId('tenant-ledger-payment-allocation-701').textContent).toContain('-$100.00');
		expect(view.queryByTestId('portal-payment-allocations-43')).toBeNull();
	});
});
