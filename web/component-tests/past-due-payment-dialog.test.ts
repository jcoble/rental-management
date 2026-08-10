import { cleanup, fireEvent, render } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import PastDuePaymentDialog from '$lib/components/accounting/PastDuePaymentDialog.svelte';
import type { PastDueOpenCharge, PastDuePaymentSubmission } from '$lib/components/accounting/PastDuePaymentDialog.svelte';

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

const openChargesBeyondFirstServerPage: PastDueOpenCharge[] = Array.from({ length: 201 }, (_, index) => ({
	tenantLedgerEntryId: 1000 + index,
	description: `Charge ${index + 1}`,
	effectiveOn: `2027-${String(Math.floor(index / 28) + 1).padStart(2, '0')}-01`,
	dueOn: `2027-${String(Math.floor(index / 28) + 1).padStart(2, '0')}-01`,
	openAmount: 1,
	currency: 'USD',
}));

function renderDialog(onsubmit = vi.fn<(data: PastDuePaymentSubmission) => void>()) {
	return {
		onsubmit,
		...render(PastDuePaymentDialog, {
			props: {
				open: true,
				target,
				openCharges,
				totalOpenAmount: target.totalOpenBalance,
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

	it('previews a covered charge beyond the first server page', async () => {
		const view = render(PastDuePaymentDialog, {
			props: {
				open: true,
				target,
				openCharges: openChargesBeyondFirstServerPage,
				totalOpenAmount: 201,
				onclose: vi.fn(),
				onsubmit: vi.fn(),
			},
		});

		await fireEvent.input(view.getByTestId('past-due-mark-paid-amount-input'), { target: { value: '201' } });

		expect(view.getByTestId('past-due-allocation-preview-row-1200').textContent).toContain('Charge 201');
		expect(view.getByTestId('past-due-allocation-preview-row-1200').textContent).toContain('$1.00');
	});
});
