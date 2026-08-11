import { cleanup, fireEvent, render, waitFor } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { businessDateOrToday } from '$lib/utils/business-date';
import { formatIsoToUsInput } from '$lib/utils/parse-date';
import type { RecurringTenantChargeRow } from '$lib/api/endpoints/tenant-ledgers';
import MoneyEntryRaceHarness from './MoneyEntryRaceHarness.svelte';

const mocks = vi.hoisted(() => ({ list: vi.fn() }));

vi.mock('$lib/api/endpoints/tenant-ledgers', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/tenant-ledgers')>(
		'$lib/api/endpoints/tenant-ledgers'
	);
	return { ...actual, tenantLedgers: { ...actual.tenantLedgers, list: mocks.list } };
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
});
