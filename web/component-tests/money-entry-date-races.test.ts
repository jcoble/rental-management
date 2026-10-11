import { cleanup, fireEvent, render, waitFor } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { businessDateOrToday } from '$lib/utils/business-date';
import { formatIsoToUsInput } from '$lib/utils/parse-date';
import type { RecurringTenantChargeRow } from '$lib/api/endpoints/tenant-ledgers';
import MoneyEntryRaceHarness from './MoneyEntryRaceHarness.svelte';
import ExpenseDateRaceHarness from './ExpenseDateRaceHarness.svelte';

const mocks = vi.hoisted(() => ({
	list: vi.fn(),
	expenseList: vi.fn(),
	property: vi.fn(),
	loanList: vi.fn()
}));

vi.mock('$app/state', () => ({
	page: {
		url: new URL('https://rentalcommand.test/units/8?tab=money&view=operating-costs'),
		state: {},
		params: { id: '8' }
	}
}));

vi.mock('$lib/api/endpoints/tenant-ledgers', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/tenant-ledgers')>(
		'$lib/api/endpoints/tenant-ledgers'
	);
	return { ...actual, tenantLedgers: { ...actual.tenantLedgers, list: mocks.list } };
});

vi.mock('$lib/api/endpoints/expenses', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/expenses')>(
		'$lib/api/endpoints/expenses'
	);
	return { ...actual, expenses: { ...actual.expenses, listPage: mocks.expenseList } };
});

vi.mock('$lib/api/endpoints/properties', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/properties')>(
		'$lib/api/endpoints/properties'
	);
	return { ...actual, properties: { ...actual.properties, get: mocks.property } };
});

vi.mock('$lib/api/endpoints/loans', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/loans')>(
		'$lib/api/endpoints/loans'
	);
	return { ...actual, loans: { ...actual.loans, listPage: mocks.loanList } };
});

afterEach(() => cleanup());

const schedule: RecurringTenantChargeRow = {
	id: 91,
	publicId: 'recurring-91',
	tenantAccountId: 20,
	leaseAgreementId: 30,
	displayName: 'Monthly rent',
	amount: 1950,
	currency: 'USD',
	ledgerAccountId: 700,
	effectiveStartOn: '2026-08-01',
	effectiveEndOn: null,
	monthlyDueDay: 1,
	nextRunDate: '2026-09-01',
	isActive: true,
	propertyId: 7,
	unitId: 8
};

beforeEach(() => {
	mocks.list.mockResolvedValue({ items: [], totalCount: 0, skip: 0, take: 100 });
	mocks.expenseList.mockResolvedValue({ items: [], totalCount: 0, skip: 0, take: 20 });
	mocks.property.mockResolvedValue({ id: 7, rentalStructure: 'SingleFamily' });
	mocks.loanList.mockResolvedValue({ items: [], totalCount: 0, skip: 0, take: 20 });
});

describe('money-entry asynchronous date boundaries', () => {
	it('uses an authoritative date that arrives before the sheet opens', async () => {
		const view = render(MoneyEntryRaceHarness, {
			props: { kind: 'payment', open: false, businessDate: null }
		});

		await view.rerender({ kind: 'payment', open: true, businessDate: '2027-02-28' });
		await waitFor(() => expect((view.getByTestId('record-payment-date') as HTMLInputElement).value).toBe('02/28/2027'));
	});

	it('keeps a local fallback and dirty input when a delayed date arrives later', async () => {
		const view = render(MoneyEntryRaceHarness, {
			props: { kind: 'payment', open: true, businessDate: null }
		});
		const fallback = formatIsoToUsInput(businessDateOrToday(undefined));

		await waitFor(() => expect((view.getByTestId('record-payment-date') as HTMLInputElement).value).toBe(fallback));
		await fireEvent.input(view.getByLabelText('Amount'), { target: { value: '125' } });
		await view.rerender({ kind: 'payment', open: true, businessDate: '2027-02-28' });

		expect((view.getByLabelText('Amount') as HTMLInputElement).value).toBe('125');
		expect((view.getByTestId('record-payment-date') as HTMLInputElement).value).toBe(fallback);
	});

	it('does not let business-date changes reinitialize a recurring-charge edit', async () => {
		const view = render(MoneyEntryRaceHarness, {
			props: { kind: 'recurring', open: true, businessDate: null, schedule }
		});

		await waitFor(() => expect((view.getByTestId('recurring-charge-start') as HTMLInputElement).value).toBe('08/01/2026'));
		await fireEvent.input(view.getByLabelText('Amount'), { target: { value: '1975' } });
		await view.rerender({ kind: 'recurring', open: true, businessDate: '2027-02-28', schedule });

		expect((view.getByLabelText('Amount') as HTMLInputElement).value).toBe('1975');
		expect((view.getByTestId('recurring-charge-start') as HTMLInputElement).value).toBe('08/01/2026');
		expect(view.getByTestId('recurring-charge-sheet').textContent).toContain('Edit recurring charge');
	});

	it('uses an authoritative date that arrives before the expense form opens', async () => {
		const view = render(ExpenseDateRaceHarness, {
			props: { businessDate: '2027-02-28', businessDatePending: false }
		});

		await fireEvent.click(view.getByTestId('expenses-create'));

		await waitFor(() => expect((view.getByTestId('expenses-date-input') as HTMLInputElement).value).toBe('02/28/2027'));
	});

	it('does not clobber dirty expense input when the authoritative date arrives late', async () => {
		const view = render(ExpenseDateRaceHarness, {
			props: { businessDate: null, businessDatePending: false }
		});
		const fallback = formatIsoToUsInput(businessDateOrToday(undefined));

		await fireEvent.click(view.getByTestId('expenses-create'));
		await waitFor(() => expect((view.getByTestId('expenses-date-input') as HTMLInputElement).value).toBe(fallback));
		await fireEvent.input(view.getByTestId('expenses-description-input'), { target: { value: 'Emergency plumbing' } });
		await view.rerender({ businessDate: '2027-02-28', businessDatePending: false });

		expect((view.getByTestId('expenses-description-input') as HTMLInputElement).value).toBe('Emergency plumbing');
		expect((view.getByTestId('expenses-date-input') as HTMLInputElement).value).toBe(fallback);
	});

	it('falls back to the local date and enables expense entry after a terminal date-query failure', async () => {
		const view = render(ExpenseDateRaceHarness, {
			props: { businessDate: null, businessDatePending: true }
		});
		const addButton = view.getByTestId('expenses-create') as HTMLButtonElement;

		expect(addButton.disabled).toBe(true);
		await view.rerender({ businessDate: null, businessDatePending: false });
		expect(addButton.disabled).toBe(false);
		await fireEvent.click(addButton);

		await waitFor(() => expect((view.getByTestId('expenses-date-input') as HTMLInputElement).value)
			.toBe(formatIsoToUsInput(businessDateOrToday(undefined))));
		expect(view.getByTestId('expenses-create-next')).toBeTruthy();
	});
});
