import { cleanup, fireEvent, render } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import TenantLedgerMonth from '$lib/components/accounting/TenantLedgerMonth.svelte';
import TenantLedgerMonthHarness from './TenantLedgerMonthHarness.svelte';
import type { TenantLedgerRow, TenantMonthSummary } from '$lib/api/endpoints/tenant-ledgers';

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
	closingBalance: 600
};

const inconsistentSummary: TenantMonthSummary = {
	...consistentSummary,
	closingBalance: 650
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
		...overrides
	};
}

function paymentRow(): TenantLedgerRow {
	return row({
		tenantLedgerEntryId: 43,
		publicId: 'entry-43',
		sourceId: 43,
		type: 'PaymentReceipt',
		description: 'Payment',
		chargeAmount: 0,
		paymentAmount: 600,
		runningAmountOwed: 600,
		openAmount: 0,
		paymentMethod: 'Check',
		accountLabel: 'PaymentReceipt',
		categoryName: null,
		journalEntryPublicId: null
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
			{ type: 'OpeningBalance', chargeAmount: 900, expected: ['View detail', 'Give credit', 'Add related charge', 'Reverse charge'] },
			{ type: 'DepositCharge', chargeAmount: 1500, expected: ['View detail'] },
			{ type: 'PaymentReceipt', paymentAmount: 600, expected: ['View detail', 'Review payment allocation'] },
			{ type: 'Credit', creditAmount: 125, expected: ['View detail'] },
			{ type: 'Adjustment', creditAmount: 25, expected: ['View detail'] },
			{ type: 'Refund', expected: ['View detail'] },
			{ type: 'TransferIn', expected: ['View detail'] },
			{ type: 'TransferOut', expected: ['View detail'] },
			{ type: 'Reversal', reversesEntryId: 42, expected: ['View detail'] },
			{ type: 'RentCharge', chargeAmount: 1200, replacedByEntryId: 99, expected: ['View detail', 'Give credit', 'Add related charge'] }
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
			replacedByEntryId: entry.replacedByEntryId ?? null
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
