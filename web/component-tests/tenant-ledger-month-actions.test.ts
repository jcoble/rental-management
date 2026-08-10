import { cleanup, fireEvent, render } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import TenantLedgerMonth from '$lib/components/accounting/TenantLedgerMonth.svelte';
import TenantLedgerMonthHarness from './TenantLedgerMonthHarness.svelte';
import TenantLedgerAllocationFlowHarness from './TenantLedgerAllocationFlowHarness.svelte';
import type { TenantLedgerRow, TenantMonthSummary } from '$lib/api/endpoints/tenant-ledgers';
import {
	buildTenantLedgerReversalRequest,
	buildTenantLedgerRowActionFlow
} from '$lib/accounting/tenant-ledger-action-flows';

const storage = new Map<string, string>();

beforeEach(() => {
	Object.defineProperty(window, 'localStorage', {
		configurable: true,
		value: {
			getItem: (key: string) => storage.get(key) ?? null,
			setItem: (key: string, value: string) => storage.set(key, value),
			removeItem: (key: string) => storage.delete(key),
			clear: () => storage.clear()
		}
	});
	storage.clear();
});

afterEach(() => cleanup());

const consistentSummary: TenantMonthSummary = {
	year: 2027,
	month: 2,
	currency: 'USD',
	openingBalance: 0,
	chargeAmount: 1200,
	paymentAmount: 600,
	creditAmount: 0,
	closingBalance: 600,
	needsReview: false,
	rows: []
};

const inconsistentSummary: TenantMonthSummary = {
	...consistentSummary,
	closingBalance: 650,
	needsReview: true
};

function row(overrides: Partial<TenantLedgerRow> = {}): TenantLedgerRow {
	return {
		tenantLedgerEntryId: 42,
		publicId: 'entry-42',
		sourceType: 'tenant-ledger',
		sourceId: 42,
		sourcePublicId: 'entry-42',
		effectiveOn: '2027-02-15',
		postedAtUtc: '2027-02-15T12:00:00Z',
		type: 'RentCharge',
		description: 'February rent',
		chargeAmount: 1200,
		paymentAmount: 0,
		creditAmount: 0,
		runningAmountOwed: 1200,
		dueOn: '2027-02-15',
		openAmount: 1200,
		status: 'Open',
		paymentMethod: null,
		reference: null,
		accountLabel: 'RentCharge',
		recurringScheduleContext: null,
		sourceDocumentContext: null,
		allocations: [],
		reversesEntryId: null,
		replacedByEntryId: null,
		journalEntryPublicId: 'journal-42',
		currency: 'USD',
		relatedTenantLedgerEntryId: null,
		relatedEntryDescription: null,
		categoryName: 'Rental income',
		servicePeriodStartOn: null,
		servicePeriodEndOn: null,
		direction: 'Debit',
		ledgerKind: 'charge',
		actionCapabilities: {
			canViewDetail: true,
			canGiveCredit: true,
			canAddRelatedCharge: true,
			canReverseCharge: true,
			canReverseLedgerEntry: false,
			canReviewPaymentAllocation: false
		},
		...overrides
	};
}

function paymentRow(): TenantLedgerRow {
	return row({
		tenantLedgerEntryId: 43,
		publicId: 'entry-43',
		sourceId: 43,
		type: 'PaymentReceipt',
		description: 'Rent payment for 2026-07',
		chargeAmount: 0,
		paymentAmount: 600,
		runningAmountOwed: 600,
		openAmount: 0,
		paymentMethod: 'Check',
		accountLabel: 'PaymentReceipt',
		categoryName: null,
		journalEntryPublicId: null,
		direction: 'Credit',
		ledgerKind: 'payment',
		allocations: [{
			allocationId: 4300,
			targetSourceId: 42,
			targetPublicId: 'charge-42',
			targetDescription: 'Rent for July 2026',
			amount: 600,
			effectiveOn: '2027-02-01'
		}],
		actionCapabilities: {
			canViewDetail: true,
			canGiveCredit: false,
			canAddRelatedCharge: false,
			canReverseCharge: false,
			canReverseLedgerEntry: false,
			canReviewPaymentAllocation: true
		}
	});
}

async function openActions(view: ReturnType<typeof render>, entryId: number) {
	await fireEvent.click(view.getByTestId(`tenant-ledger-actions-trigger-${entryId}`));
}

describe('tenant ledger month row actions', () => {
	it('dispatches every payment and charge menu item with its row payload', async () => {
		const onaction = vi.fn();
		const payment = paymentRow();
		const charge = row();
		const view = render(TenantLedgerMonth, {
			props: { summary: consistentSummary, rows: [payment, charge], onaction }
		});

		await openActions(view, payment.tenantLedgerEntryId);
		await fireEvent.click(view.getByRole('menuitem', { name: 'View detail' }));
		await openActions(view, payment.tenantLedgerEntryId);
		await fireEvent.click(view.getByRole('menuitem', { name: 'Review payment allocation' }));
		await openActions(view, charge.tenantLedgerEntryId);
		await fireEvent.click(view.getByRole('menuitem', { name: 'View detail' }));
		await openActions(view, charge.tenantLedgerEntryId);
		await fireEvent.click(view.getByRole('menuitem', { name: 'Give credit' }));
		await openActions(view, charge.tenantLedgerEntryId);
		await fireEvent.click(view.getByRole('menuitem', { name: 'Add related charge' }));
		await openActions(view, charge.tenantLedgerEntryId);
		await fireEvent.click(view.getByRole('menuitem', { name: 'Reverse charge' }));

		expect(onaction.mock.calls.map(([payload, action]) => [payload.tenantLedgerEntryId, action])).toEqual([
			[43, 'view'],
			[43, 'fix-payment'],
			[42, 'view'],
			[42, 'give-credit'],
			[42, 'related-charge'],
			[42, 'fix-charge']
		]);
		expect(onaction.mock.calls[1][0].allocations).toEqual(expect.arrayContaining([
			expect.objectContaining({ allocationId: 4300, amount: 600 })
		]));
		expect(buildTenantLedgerRowActionFlow(payment, 'fix-payment')).toMatchObject({
			kind: 'allocation-review',
			entryId: 43,
			allocations: [{ allocationId: 4300, amount: 600 }]
		});
		expect(buildTenantLedgerRowActionFlow(charge, 'related-charge')).toMatchObject({
		kind: 'related-charge',
		seed: {
			description: 'Additional charge related to February rent',
			effectiveOn: '2027-02-15',
			dueOn: '2027-02-15',
			chargeType: 'Other'
		}
	});
	});

	it('keeps reversal flow boundaries exact for charge and opening-balance rows', () => {
		const charge = row({ tenantLedgerEntryId: 71, description: 'Rent for 2026-07', chargeAmount: 1950 });
		const opening = row({
			tenantLedgerEntryId: 72,
			type: 'OpeningBalance',
			description: 'Opening balance for 2026-07',
			chargeAmount: 3200,
			actionCapabilities: {
				canViewDetail: true,
				canGiveCredit: false,
				canAddRelatedCharge: false,
				canReverseCharge: false,
				canReverseLedgerEntry: true,
				canReviewPaymentAllocation: false
			}
		});

		expect(buildTenantLedgerRowActionFlow(charge, 'view')).toMatchObject({ kind: 'view', entryId: 71 });
		expect(buildTenantLedgerRowActionFlow(charge, 'give-credit')).toMatchObject({ kind: 'give-credit', targetEntryId: 71 });
		expect(buildTenantLedgerRowActionFlow(charge, 'fix-charge')).toMatchObject({ kind: 'reverse', targetEntryId: 71 });
		expect(buildTenantLedgerRowActionFlow(opening, 'fix-charge')).toMatchObject({ kind: 'reverse', targetEntryId: 72 });
		expect(buildTenantLedgerReversalRequest(20, charge, '2027-02-28')).toEqual({
		endpoint: '/tenant-accounts/20/charges/71/reversals',
		body: { effectiveOn: '2027-02-28', reason: 'Reverse charge: Rent for July 2026' }
	});
		expect(buildTenantLedgerReversalRequest(20, opening, '2027-02-28')).toEqual({
		endpoint: '/tenant-accounts/20/reversals',
		body: {
			reversesEntryId: 72,
			effectiveOn: '2027-02-28',
			reason: 'Reverse opening balance: Opening balance for July 2026'
		}
	});
	});

	it('shows only the actions supported by each ledger row type', async () => {
		const entryTypes: Array<{
			type: TenantLedgerRow['type'];
			chargeAmount?: number;
			paymentAmount?: number;
			creditAmount?: number;
			reversesEntryId?: number | null;
			replacedByEntryId?: number | null;
			expected: string[];
		}> = [
			{ type: 'RentCharge', chargeAmount: 1200, expected: ['View detail', 'Give credit', 'Add related charge', 'Reverse charge'] },
			{ type: 'AddendumCharge', chargeAmount: 200, expected: ['View detail', 'Give credit', 'Add related charge', 'Reverse charge'] },
			{ type: 'LateFeeCharge', chargeAmount: 50, expected: ['View detail', 'Give credit', 'Add related charge', 'Reverse charge'] },
			{ type: 'ManualCharge', chargeAmount: 75, expected: ['View detail', 'Give credit', 'Add related charge', 'Reverse charge'] },
			{ type: 'OpeningBalance', chargeAmount: 900, expected: ['View detail', 'Reverse opening balance'] },
			{ type: 'DepositCharge', chargeAmount: 1500, expected: ['View detail'] },
			{ type: 'PaymentReceipt', paymentAmount: 600, expected: ['View detail', 'Review payment allocation'] },
			{ type: 'Credit', creditAmount: 125, expected: ['View detail'] },
			{ type: 'Adjustment', creditAmount: 25, expected: ['View detail'] },
			{ type: 'Refund', chargeAmount: 430, expected: ['View detail'] },
			{ type: 'TransferIn', chargeAmount: 275, expected: ['View detail'] },
			{ type: 'TransferOut', chargeAmount: 180, expected: ['View detail'] },
			{ type: 'Reversal', chargeAmount: 300, reversesEntryId: 42, expected: ['View detail'] },
			{ type: 'RentCharge', chargeAmount: 1200, replacedByEntryId: 99, expected: ['View detail'] }
		];

		const rows = entryTypes.map((entry, index) => row({
			tenantLedgerEntryId: 100 + index,
			publicId: `entry-${100 + index}`,
			sourceId: 100 + index,
			type: entry.type,
			chargeAmount: entry.chargeAmount ?? 0,
			paymentAmount: entry.paymentAmount ?? 0,
			creditAmount: entry.creditAmount ?? 0,
			reversesEntryId: entry.reversesEntryId ?? null,
			replacedByEntryId: entry.replacedByEntryId ?? null,
			direction: entry.type === 'PaymentReceipt' || entry.type === 'Credit' || entry.type === 'Adjustment' ? 'Credit' : 'Debit',
			ledgerKind: ['RentCharge', 'AddendumCharge', 'LateFeeCharge', 'DepositCharge', 'ManualCharge'].includes(entry.type) ? 'charge' : entry.type === 'PaymentReceipt' ? 'payment' : ['Credit', 'Adjustment'].includes(entry.type) ? 'credit' : 'other',
			actionCapabilities: {
				canViewDetail: true,
				canGiveCredit: ['RentCharge', 'AddendumCharge', 'LateFeeCharge', 'ManualCharge'].includes(entry.type) && entry.replacedByEntryId == null,
				canAddRelatedCharge: ['RentCharge', 'AddendumCharge', 'LateFeeCharge', 'ManualCharge'].includes(entry.type) && entry.replacedByEntryId == null,
				canReverseCharge: ['RentCharge', 'AddendumCharge', 'LateFeeCharge', 'ManualCharge'].includes(entry.type) && entry.replacedByEntryId == null,
				canReverseLedgerEntry: entry.type === 'OpeningBalance' && entry.replacedByEntryId == null,
				canReviewPaymentAllocation: entry.type === 'PaymentReceipt'
			}
		}));
		const view = render(TenantLedgerMonth, {
			props: { summary: consistentSummary, rows }
		});

		for (const [index, entry] of entryTypes.entries()) {
			await openActions(view, 100 + index);
			expect(view.getAllByRole('menuitem').map((item) => item.textContent?.trim())).toEqual(entry.expected);
			await fireEvent.click(view.getByRole('menuitem', { name: 'View detail' }));
		}
	});
});

describe('tenant ledger month summary and detail mode', () => {
	it('does not render a Reconciled badge for a consistent month', () => {
		const view = render(TenantLedgerMonth, {
			props: { summary: consistentSummary, rows: [row()] }
		});

		expect(view.queryByTestId('tenant-ledger-summary-reconciliation')).toBeNull();
		expect(view.queryByText('Reconciled')).toBeNull();
	});

	it('renders a plain-language Needs review warning for an inconsistent month', () => {
		const view = render(TenantLedgerMonth, {
			props: { summary: inconsistentSummary, rows: [row()] }
		});

		expect(view.getByTestId('tenant-ledger-summary-reconciliation-warning').textContent).toContain('Needs review');
	});

	it('normalizes legacy rent and payment descriptions in the month rows', () => {
		const view = render(TenantLedgerMonth, {
			props: {
				summary: consistentSummary,
				rows: [
					row({ tenantLedgerEntryId: 81, description: 'Rent for 2026-07' }),
					paymentRow()
				]
			}
		});

		expect(view.getByTestId('tenant-ledger-row-81').textContent).toContain('Rent for July 2026');
		expect(view.getByTestId('tenant-ledger-row-43').textContent).toContain('Rent payment for July 2026');
	});

	it('opens allocation review from the rendered payment menu with entry and allocation payload', async () => {
		const payment = paymentRow();
		const view = render(TenantLedgerAllocationFlowHarness, {
			props: { summary: consistentSummary, row: payment }
		});

		await openActions(view, payment.tenantLedgerEntryId);
		await fireEvent.click(view.getByRole('menuitem', { name: 'Review payment allocation' }));
		expect(view.getByTestId('tenant-payment-allocation-entry-id').textContent).toContain('Payment entry #43');
		expect(view.getByTestId('tenant-payment-allocation-list').textContent).toContain('Rent for July 2026');
		expect(view.getByTestId('tenant-payment-allocation-list').textContent).toContain('$600.00');
	});

	it('changes the rendered ledger facts when switching between Simple and Advanced', async () => {
		const view = render(TenantLedgerMonthHarness, {
			props: { summary: consistentSummary, rows: [row()] }
		});

		expect(view.queryByTestId('tenant-ledger-advanced-facts-42')).toBeNull();
		await fireEvent.click(view.getByTestId('test-accounting-detail-mode-advanced'));
		expect(view.getByTestId('tenant-ledger-advanced-facts-42').textContent).toContain('RentCharge');
		expect(view.getByTestId('tenant-ledger-advanced-facts-42').textContent).toContain('journal-42');
		await fireEvent.click(view.getByTestId('test-accounting-detail-mode-simple'));
		expect(view.queryByTestId('tenant-ledger-advanced-facts-42')).toBeNull();
	});
});
