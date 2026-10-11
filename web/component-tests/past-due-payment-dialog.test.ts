import { cleanup, fireEvent, render } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import PastDuePaymentDialog from '$lib/components/accounting/PastDuePaymentDialog.svelte';
import type { PastDueOpenCharge, PastDuePaymentSubmission } from '$lib/components/accounting/PastDuePaymentDialog.svelte';
import {
	loadAllOpenCharges,
	MAX_OPEN_CHARGE_PREVIEW_REQUESTS,
	type OpenChargePageLoader
} from '$lib/accounting/past-due-preview';
import type { TenantLedgerRow } from '$lib/api/endpoints/tenant-ledgers';

afterEach(async () => {
	cleanup();
	await new Promise((resolve) => setTimeout(resolve, 40));
});

const target = {
	leaseManagementId: 10,
	tenantAccountId: 20,
	currentAgreementId: 30,
	unitId: 40,
	tenantName: 'Ada Lovelace',
	tenantPhone: null,
	relationshipNumber: 'L-020',
	propertyName: 'Example House',
	unitNumber: '1A',
	pastDueAmount: 900,
	totalOpenBalance: 1700,
	overduePaymentCount: 2,
	oldestDueOn: '2027-01-01',
	oldestLedgerEntryId: 7,
	oldestLedgerEntryOpenAmount: 400,
};

const openCharges: PastDueOpenCharge[] = [
	{
		tenantLedgerEntryId: 7,
		description: 'January rent',
		effectiveOn: '2027-01-01',
		dueOn: '2027-01-01',
		openAmount: 400,
		currency: 'USD',
	},
	{
		tenantLedgerEntryId: 8,
		description: 'February rent',
		effectiveOn: '2027-02-01',
		dueOn: '2027-02-01',
		openAmount: 500,
		currency: 'USD',
	},
	{
		tenantLedgerEntryId: 9,
		description: 'March rent',
		effectiveOn: '2027-03-01',
		dueOn: '2027-03-01',
		openAmount: 800,
		currency: 'USD',
	},
];

function renderDialog(
	onsubmit = vi.fn<(data: PastDuePaymentSubmission) => void>(),
	props: Partial<{
		openCharges: PastDueOpenCharge[];
		openChargesLoading: boolean;
		openChargesError: boolean;
	}> = {}
) {
	return {
		onsubmit,
		...render(PastDuePaymentDialog, {
			props: {
				open: true,
				target,
				openCharges: props.openCharges ?? openCharges,
				totalOpenAmount: target.totalOpenBalance,
				openChargesLoading: props.openChargesLoading ?? false,
				openChargesError: props.openChargesError ?? false,
				onclose: vi.fn(),
				onsubmit,
			},
		}),
	};
}

describe('past-due partial payment dialog', () => {
	it('defaults the amount field to the full server-owned open balance', () => {
		const view = renderDialog();
		expect((view.getByTestId('past-due-mark-paid-amount-input') as HTMLInputElement).value).toBe('1700.00');
	});

	it('submits a valid partial amount with oldest-first allocation enabled', async () => {
		const view = renderDialog();
		await fireEvent.input(view.getByTestId('past-due-mark-paid-amount-input'), { target: { value: '600' } });
		await fireEvent.change(view.getByTestId('past-due-mark-paid-method-input'), { target: { value: 'Check' } });
		await fireEvent.click(view.getByTestId('past-due-mark-paid-confirm'));

		expect(view.onsubmit).toHaveBeenCalledOnce();
		expect(view.onsubmit.mock.calls[0][0]).toMatchObject({ amount: 600, allocateOldestCharges: true, method: 'Check' });
	});

	it('rejects zero, negative, and over-balance amounts with visible errors', async () => {
		for (const [amount, message] of [
			['0', 'greater than $0.00'],
			['-1', 'greater than $0.00'],
			['1800', 'cannot exceed']
		]) {
			const view = renderDialog();
			await fireEvent.input(view.getByTestId('past-due-mark-paid-amount-input'), { target: { value: amount } });
			await fireEvent.click(view.getByTestId('past-due-mark-paid-confirm'));
			expect(view.getByTestId('past-due-mark-paid-amount-error').textContent?.toLowerCase()).toContain(message.toLowerCase());
			expect(view.onsubmit).not.toHaveBeenCalled();
			view.unmount();
		}
	});

	it('previews partial allocation across ordered server-provided charges', async () => {
		const view = renderDialog();
		await fireEvent.input(view.getByTestId('past-due-mark-paid-amount-input'), { target: { value: '600' } });

		expect(view.getByTestId('past-due-allocation-preview-row-7').textContent).toContain('$400.00');
		expect(view.getByTestId('past-due-allocation-preview-row-8').textContent).toContain('$200.00');
		expect(view.queryByTestId('past-due-allocation-preview-row-9')).toBeNull();
	});

	it('loads the production preview through a second server page and renders charge 201', async () => {
		const pageRow = (index: number): TenantLedgerRow => ({
			tenantLedgerEntryId: 999 + index,
			publicId: `00000000-0000-0000-0000-${String(index).padStart(12, '0')}`,
			sourceType: 'tenant-ledger',
			sourceId: 999 + index,
			sourcePublicId: null,
			effectiveOn: '2027-01-01',
			postedAtUtc: '2027-01-01T00:00:00Z',
			type: 'RentCharge',
			description: `Charge ${index}`,
			chargeAmount: 1,
			paymentAmount: 0,
			creditAmount: 0,
			runningAmountOwed: index,
			dueOn: '2027-01-01',
			openAmount: 1,
			status: 'Open',
			paymentMethod: null,
			reference: null,
			accountLabel: 'RentCharge',
			recurringScheduleContext: null,
			sourceDocumentContext: null,
			allocations: [],
			reversesEntryId: null,
			replacedByEntryId: null,
			journalEntryPublicId: null,
			currency: 'USD',
			relatedTenantLedgerEntryId: null,
			relatedEntryDescription: null,
			categoryName: null,
			servicePeriodStartOn: null,
			servicePeriodEndOn: null
		});
		const list = vi.fn<OpenChargePageLoader>(async (_tenantAccountId, params) => ({
			items: params.skip === 0
				? Array.from({ length: 200 }, (_, index) => pageRow(index + 1))
				: [pageRow(201)],
			totalCount: 201,
			skip: params.skip,
			take: 200
		}));

		const loadedCharges = await loadAllOpenCharges(target.tenantAccountId, list);
		expect(list).toHaveBeenCalledTimes(2);
		expect(list).toHaveBeenNthCalledWith(2, target.tenantAccountId, {
			skip: 200,
			take: 200,
			openOnly: true,
			sort: 'oldestDueOn'
		});
		expect(loadedCharges).toHaveLength(201);

		const view = renderDialog(undefined, { openCharges: loadedCharges });
		await fireEvent.input(view.getByTestId('past-due-mark-paid-amount-input'), { target: { value: '201' } });

		expect(view.getByTestId('past-due-allocation-preview-row-1200').textContent).toContain('Charge 201');
		expect(view.getByTestId('past-due-allocation-preview-row-1200').textContent).toContain('$1.00');
	});

	it('fails closed after the finite preview request bound with no payment submission', async () => {
		const list = vi.fn<OpenChargePageLoader>(async (_tenantAccountId, params) => ({
			items: Array.from({ length: 200 }, (_, index) => ({
				...openCharges[0],
				tenantLedgerEntryId: params.skip + index + 1
			} as TenantLedgerRow)),
			totalCount: MAX_OPEN_CHARGE_PREVIEW_REQUESTS * 200 + 1,
			skip: params.skip,
			take: 200
		}));

		let openChargesError = false;
		try {
			await loadAllOpenCharges(target.tenantAccountId, list);
		} catch (error) {
			openChargesError = true;
			expect(error).toBeInstanceOf(Error);
			expect((error as Error).message).toContain('request bound');
		}
		expect(openChargesError).toBe(true);
		expect(list).toHaveBeenCalledTimes(MAX_OPEN_CHARGE_PREVIEW_REQUESTS);

		const onsubmit = vi.fn<(data: PastDuePaymentSubmission) => void>();
		const view = renderDialog(onsubmit, { openCharges: [], openChargesError });
		await fireEvent.change(view.getByTestId('past-due-mark-paid-method-input'), { target: { value: 'Check' } });
		await fireEvent.click(view.getByTestId('past-due-mark-paid-confirm'));

		expect(view.getByTestId('past-due-allocation-preview-error').textContent).toContain('Try again');
		expect((view.getByTestId('past-due-mark-paid-confirm') as HTMLButtonElement).disabled).toBe(true);
		expect(onsubmit).not.toHaveBeenCalled();
	});
});
