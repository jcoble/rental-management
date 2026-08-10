import { cleanup, fireEvent, render, waitFor } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TenantLedgerRow, TenantMonthSummary } from '$lib/api/endpoints/tenant-ledgers';
import type { UnitDashboard } from '$lib/types';
import TenantLedgerPanelFlowHarness from './TenantLedgerPanelFlowHarness.svelte';

const mocks = vi.hoisted(() => ({
	account: vi.fn(),
	creditTargets: vi.fn(),
	monthSummary: vi.fn(),
	ledgerSummary: vi.fn(),
	recurringCharges: vi.fn(),
	deposit: vi.fn(),
	postCredit: vi.fn(),
	reverseCharge: vi.fn(),
	reverseLedgerEntry: vi.fn()
}));

vi.mock('$lib/api/endpoints/tenant-accounts', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/tenant-accounts')>(
		'$lib/api/endpoints/tenant-accounts'
	);
	return { ...actual, tenantAccounts: { ...actual.tenantAccounts, get: mocks.account } };
});

vi.mock('$lib/api/endpoints/tenant-ledgers', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/tenant-ledgers')>(
		'$lib/api/endpoints/tenant-ledgers'
	);
	return {
		...actual,
		tenantLedgers: {
			...actual.tenantLedgers,
			monthSummary: mocks.monthSummary,
			ledgerSummary: mocks.ledgerSummary,
			recurringCharges: mocks.recurringCharges,
			creditTargets: mocks.creditTargets
		}
	};
});

vi.mock('$lib/api/endpoints/securityDeposits', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/securityDeposits')>(
		'$lib/api/endpoints/securityDeposits'
	);
	return { ...actual, securityDeposits: { ...actual.securityDeposits, get: mocks.deposit } };
});

vi.mock('$lib/api/endpoints/tenant-money', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/tenant-money')>(
		'$lib/api/endpoints/tenant-money'
	);
	return {
		...actual,
		tenantMoney: {
			...actual.tenantMoney,
			postCredit: mocks.postCredit,
			reverseCharge: mocks.reverseCharge,
			reverseLedgerEntry: mocks.reverseLedgerEntry
		}
	};
});

afterEach(async () => {
	cleanup();
	await new Promise((resolve) => setTimeout(resolve, 100));
});

const dashboard = {
	unit: {
		id: 8, propertyId: 7, unitNumber: '1A', bedrooms: 1, bathrooms: 1, marketRent: 1950,
		status: 'Occupied', notes: '', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z'
	},
	propertyName: 'Example House',
	lifecycleStage: 'Active',
	nextBestAction: { label: 'Review rent', href: '/units/8' },
	header: { rentState: 'Current', outstandingRentBalance: 1950, openWorkOrderCount: 0, docsNeedingReviewCount: 0 },
	occupancyPossession: { status: 'Occupied', isOccupied: true, hasScheduledMoveIn: false, leaseManagementId: 10 },
	marketingAvailability: { status: 'Unavailable', isAvailable: false },
	tenantAccountCondition: { status: 'Current', tenantAccountId: 20, receivableBalance: 1950, pastDueAmount: 0 },
	legalNoticeCondition: { status: 'Clear', agreementId: 30, agreementStatus: 'Active', openNoticeCount: 0 },
	maintenanceTurnover: { status: 'Ready', openWorkOrderCount: 0, isInTurnover: false, isOutOfService: false, isOnManagementHold: false },
	leaseManagementId: 10,
	tenantAccountId: 20,
	currentLease: { id: 30, leaseManagementId: 10, tenantAccountId: 20, leaseNumber: 'L-20', status: 'Active', startDate: '2026-01-01', endDate: '2027-01-01', monthlyRent: 1950, securityDeposit: 1950 },
	currentTenant: { id: 1, name: 'Ada Lovelace' },
	currentTenants: [{ id: 1, name: 'Ada Lovelace' }],
	overview: { recentPayments: [], openWorkOrders: [], pendingDocs: [], upcomingAppointments: [] },
	turnover: { status: 'NotStarted', totalTaskCount: 0, openTaskCount: 0, completedTaskCount: 0, receiptCount: 0, estimatedCost: 0, actualCost: 0 },
	recentTimeline: []
} as unknown as UnitDashboard;

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
		direction: 'Debit',
		ledgerKind: 'charge',
		description: 'Rent for February 2027',
		chargeAmount: 1950,
		paymentAmount: 0,
		creditAmount: 0,
		runningAmountOwed: 1950,
		dueOn: '2027-02-15',
		openAmount: 1950,
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

function summary(entry: TenantLedgerRow): TenantMonthSummary {
	return {
		year: 2027, month: 2, currency: 'USD', openingBalance: 0, chargeAmount: entry.chargeAmount,
		paymentAmount: entry.paymentAmount, creditAmount: entry.creditAmount,
		closingBalance: entry.runningAmountOwed, needsReview: false, rows: [entry]
	};
}

function setupQueries(entry: TenantLedgerRow): void {
	mocks.account.mockResolvedValue({
		tenantAccountId: 20, leaseManagementId: 10, propertyId: 7, propertyName: 'Example House', unitId: 8,
		unitNumber: '1A', accountNumber: 'TA-20', relationshipNumber: 'LM-20', currency: 'USD',
		businessDate: '2027-02-28', receivableBalance: entry.runningAmountOwed, unappliedCredit: 0,
		pastDueAmount: 0, pastDueCount: 0, nextDueOn: entry.dueOn, nextDueAmount: entry.openAmount,
		oldestOpenChargeDueOn: entry.dueOn, oldestOpenChargeAmount: entry.openAmount
	});
	mocks.creditTargets.mockResolvedValue({
		items: entry.actionCapabilities.canGiveCredit ? [{
			tenantLedgerEntryId: entry.tenantLedgerEntryId,
			publicId: entry.publicId,
			description: entry.description,
			chargeAmount: entry.chargeAmount,
			remainingTargetableAmount: entry.openAmount,
			effectiveOn: entry.effectiveOn,
			currency: entry.currency
		}] : [],
		totalCount: entry.actionCapabilities.canGiveCredit ? 1 : 0,
		skip: 0,
		take: 50
	});
	mocks.monthSummary.mockResolvedValue([summary(entry)]);
	mocks.ledgerSummary.mockResolvedValue({
		periodMonths: 12, currency: 'USD', chargeAmount: entry.chargeAmount, paymentAmount: entry.paymentAmount,
		creditAmount: entry.creditAmount, endingBalance: entry.runningAmountOwed,
	 agingCurrent: entry.openAmount, aging1To30: 0, aging31To60: 0, aging61To90: 0, aging90Plus: 0
	});
	mocks.recurringCharges.mockResolvedValue({ items: [], totalCount: 0, skip: 0, take: 50 });
	mocks.deposit.mockResolvedValue(null);
	mocks.postCredit.mockResolvedValue({ value: { found: true, applied: true }, replayed: false });
	mocks.reverseCharge.mockResolvedValue({ value: { found: true, applied: true }, replayed: false });
	mocks.reverseLedgerEntry.mockResolvedValue({ value: { found: true, applied: true }, replayed: false });
}

async function renderPanel(entry: TenantLedgerRow) {
	setupQueries(entry);
	const view = render(TenantLedgerPanelFlowHarness, { props: { dashboard } });
	await waitFor(() => expect(view.getByTestId(`tenant-ledger-actions-trigger-${entry.tenantLedgerEntryId}`)).toBeTruthy());
	await fireEvent.click(view.getByTestId(`tenant-ledger-actions-trigger-${entry.tenantLedgerEntryId}`));
	return view;
}

describe('rendered tenant ledger panel flow boundaries', () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	it('routes View detail through the supplied record-detail destination with the entry id', async () => {
		const entry = row();
		const view = await renderPanel(entry);
		await fireEvent.click(view.getByRole('menuitem', { name: 'View detail' }));
		expect(view.getByTestId('panel-flow-view-destination').textContent).toContain('Tenant entry #42');
	});

	it('opens Give credit with the selected server-projected charge target', async () => {
		const entry = row({ tenantLedgerEntryId: 43 });
		const view = await renderPanel(entry);
		await fireEvent.click(view.getByRole('menuitem', { name: 'Give credit' }));
		await waitFor(() => expect(view.getByTestId('tenant-credit-sheet').textContent).toContain('Rent for February 2027'));
	});

	it('invokes every offered action for every supported charge class', async () => {
		for (const [index, entryType] of (['RentCharge', 'AddendumCharge', 'LateFeeCharge', 'ManualCharge'] as const).entries()) {
			const entry = row({
				tenantLedgerEntryId: 100 + index,
				type: entryType,
				description: `${entryType} for February 2027`
			});

			let view = await renderPanel(entry);
			await fireEvent.click(view.getByRole('menuitem', { name: 'View detail' }));
			expect(view.getByTestId('panel-flow-view-destination').textContent)
				.toContain(`Tenant entry #${entry.tenantLedgerEntryId}`);
			view.unmount();

			view = await renderPanel(entry);
			await fireEvent.click(view.getByRole('menuitem', { name: 'Give credit' }));
			await waitFor(() => expect(view.getByTestId('tenant-credit-sheet').textContent)
				.toContain(entry.description));
			await waitFor(() => expect(mocks.creditTargets).toHaveBeenLastCalledWith(
				20,
				expect.objectContaining({ skip: 0, take: 50, targetEntryId: entry.tenantLedgerEntryId })
			));
			view.unmount();

			view = await renderPanel(entry);
			await fireEvent.click(view.getByRole('menuitem', { name: 'Add related charge' }));
			await waitFor(() => expect(view.getByTestId('one-time-charge-sheet')).toBeTruthy());
			expect((view.getByLabelText('Description') as HTMLInputElement).value)
				.toBe(`Additional charge related to ${entry.description}`);
			view.unmount();

			mocks.reverseCharge.mockClear();
			view = await renderPanel(entry);
			await fireEvent.click(view.getByRole('menuitem', { name: 'Reverse charge' }));
			await fireEvent.click(view.getByRole('button', { name: /Reverse the posted charge/ }));
			await fireEvent.click(view.getByRole('button', { name: 'Continue' }));
			await waitFor(() => expect(mocks.reverseCharge).toHaveBeenCalledOnce());
			const [tenantAccountId, chargeEntryId, , body] = mocks.reverseCharge.mock.calls[0];
			expect(tenantAccountId).toBe(20);
			expect(chargeEntryId).toBe(entry.tenantLedgerEntryId);
			expect(body).toEqual({
				effectiveOn: expect.any(String),
				reason: `Reverse charge: ${entry.description}`
			});
			view.unmount();
		}
	});

	it('navigates between server pages and submits only the visibly selected target', async () => {
		const entry = row({ tenantLedgerEntryId: 90 });
		const laterTarget = {
			tenantLedgerEntryId: 91,
			publicId: 'entry-91',
			description: 'Later eligible charge',
			chargeAmount: 875,
			remainingTargetableAmount: 875,
			effectiveOn: '2027-01-15',
			currency: 'USD'
		};
		const firstPage = {
			items: [{
				tenantLedgerEntryId: entry.tenantLedgerEntryId,
				publicId: entry.publicId,
				description: entry.description,
				chargeAmount: entry.chargeAmount,
				remainingTargetableAmount: entry.openAmount,
				effectiveOn: entry.effectiveOn,
				currency: entry.currency
			}],
			totalCount: 51,
			skip: 0,
			take: 50
		};
		const view = await renderPanel(entry);
		mocks.creditTargets.mockImplementation((_tenantAccountId: number, params: { skip?: number }) =>
			Promise.resolve(params.skip === 0 ? firstPage : { items: [laterTarget], totalCount: 51, skip: 50, take: 50 }));

		await fireEvent.click(view.getByRole('menuitem', { name: 'Give credit' }));
		await waitFor(() => expect(view.getByTestId('tenant-credit-sheet').textContent)
			.toContain(entry.description));
		expect(view.getByTestId('tenant-credit-page-navigation').textContent).toContain('Next page');
		await fireEvent.input(view.getByLabelText('Amount'), { target: { value: '25' } });
		await fireEvent.input(view.getByLabelText('Reason'), { target: { value: 'Visible target test' } });

		await fireEvent.click(view.getByTestId('tenant-credit-next-page'));
		await waitFor(() => expect(mocks.creditTargets).toHaveBeenLastCalledWith(
			20,
			expect.objectContaining({ skip: 50, take: 50 })
		));
		const laterPageTrigger = view.getByRole('button', { name: 'Choose an original charge' });
		await fireEvent.keyDown(laterPageTrigger, { key: 'ArrowDown' });
		expect(laterPageTrigger.getAttribute('aria-expanded')).toBe('true');
		await waitFor(() => expect(view.getByRole('option', { name: /Later eligible charge/ })).toBeTruthy());
		await fireEvent.keyDown(laterPageTrigger, { key: 'Escape' });
		expect(view.getByTestId('tenant-credit-page-navigation').textContent).toContain('Previous page');
		expect(view.getByTestId('tenant-credit-page-navigation').textContent).not.toContain('Show more');
		await fireEvent.click(view.getByTestId('tenant-credit-submit'));
		expect(mocks.postCredit).not.toHaveBeenCalled();
		expect(view.getByText('Choose a visible original charge or turn off the checkbox.')).toBeTruthy();

		await fireEvent.click(view.getByTestId('tenant-credit-previous-page'));
		await waitFor(() => expect(mocks.creditTargets).toHaveBeenLastCalledWith(
			20,
			expect.objectContaining({ skip: 0, take: 50, targetEntryId: 90 })
		));
		const targetTrigger = await waitFor(() => view.getByTestId('tenant-credit-target-trigger'));
		await fireEvent.keyDown(targetTrigger, { key: 'ArrowDown' });
		expect(targetTrigger.getAttribute('aria-expanded')).toBe('true');
		const visibleTarget = await waitFor(() => view.getByRole('option', { name: /Rent for February 2027/ }));
		expect(visibleTarget).toBeTruthy();
		await fireEvent.keyDown(targetTrigger, { key: 'Enter' });
		await waitFor(() => expect(view.getByTestId('tenant-credit-target-trigger').textContent)
			.toContain('Rent for February 2027'));

		await fireEvent.click(view.getByTestId('tenant-credit-submit'));
		await waitFor(() => expect(mocks.postCredit).toHaveBeenCalledOnce());
		const [, , body] = mocks.postCredit.mock.calls[0];
		expect(body).toMatchObject({
			amount: 25,
			description: 'Visible target test',
			targetChargeEntryId: 90,
			incomeLedgerAccountId: null,
			allocateOldestCharges: false
		});
	});

	it('opens Add related charge with a normalized related-charge seed', async () => {
		const entry = row({ tenantLedgerEntryId: 44, description: 'Rent for 2026-07' });
		const view = await renderPanel(entry);
		await fireEvent.click(view.getByRole('menuitem', { name: 'Add related charge' }));
		await waitFor(() => expect(view.getByTestId('one-time-charge-sheet')).toBeTruthy());
		expect((view.getByLabelText('Description') as HTMLInputElement).value)
			.toBe('Additional charge related to Rent for July 2026');
	});

	it('calls the charge reversal endpoint with the exact body after the rendered choice', async () => {
		const entry = row({ tenantLedgerEntryId: 45 });
		const view = await renderPanel(entry);
		await fireEvent.click(view.getByRole('menuitem', { name: 'Reverse charge' }));
		await fireEvent.click(view.getByRole('button', { name: /Reverse the posted charge/ }));
		await fireEvent.click(view.getByRole('button', { name: 'Continue' }));
		await waitFor(() => expect(mocks.reverseCharge).toHaveBeenCalledOnce());
		const [, , , body] = mocks.reverseCharge.mock.calls[0];
		expect(body).toEqual({ effectiveOn: expect.any(String), reason: 'Reverse charge: Rent for February 2027' });
	});

	it('calls the generic opening-balance reversal endpoint with the exact body', async () => {
		const entry = row({
			tenantLedgerEntryId: 46, type: 'OpeningBalance', description: 'Opening balance for 2026-07',
			chargeAmount: 3200, openAmount: 3200, actionCapabilities: {
				canViewDetail: true, canGiveCredit: false, canAddRelatedCharge: false,
				canReverseCharge: false, canReverseLedgerEntry: true, canReviewPaymentAllocation: false
			}
		});
		const view = await renderPanel(entry);
		await fireEvent.click(view.getByRole('menuitem', { name: 'Reverse opening balance' }));
		await fireEvent.click(view.getByRole('button', { name: /Reverse the posted charge/ }));
		await fireEvent.click(view.getByRole('button', { name: 'Continue' }));
		await waitFor(() => expect(mocks.reverseLedgerEntry).toHaveBeenCalledOnce());
		const [, , body] = mocks.reverseLedgerEntry.mock.calls[0];
		expect(body).toEqual({
			reversesEntryId: 46,
			effectiveOn: expect.any(String),
			reason: 'Reverse opening balance: Opening balance for July 2026'
		});
	});

	it('opens allocation review with the payment entry id and allocation payload', async () => {
		const entry = row({
			tenantLedgerEntryId: 47, type: 'PaymentReceipt', direction: 'Credit', ledgerKind: 'payment',
			description: 'Rent payment for 2026-07', chargeAmount: 0, paymentAmount: 1950, openAmount: 0,
			actionCapabilities: {
				canViewDetail: true, canGiveCredit: false, canAddRelatedCharge: false, canReverseCharge: false,
				canReverseLedgerEntry: false, canReviewPaymentAllocation: true
			},
			allocations: [{ allocationId: 701, targetSourceId: 42, targetPublicId: 'charge-42', targetDescription: 'Rent for 2026-07', amount: 1950, effectiveOn: '2026-07-01' }]
		});
		const view = await renderPanel(entry);
		await fireEvent.click(view.getByRole('menuitem', { name: 'Review payment allocation' }));
		expect(view.getByTestId('tenant-payment-allocation-entry-id').textContent).toContain('Payment entry #47');
		expect(view.getByTestId('tenant-payment-allocation-list').textContent).toContain('Rent for July 2026');
		expect(view.getByTestId('tenant-payment-allocation-list').textContent).toContain('$1,950.00');
	});

	it('does not offer unsupported actions for debit refunds, transfers, reversals, or corrected charges', async () => {
		for (const [index, entryType] of (['Refund', 'TransferIn', 'TransferOut', 'Reversal'] as const).entries()) {
			const entry = row({
				tenantLedgerEntryId: 60 + index, type: entryType, direction: 'Debit', ledgerKind: 'other',
				chargeAmount: 430, runningAmountOwed: 430,
				actionCapabilities: { canViewDetail: true, canGiveCredit: false, canAddRelatedCharge: false, canReverseCharge: false, canReverseLedgerEntry: false, canReviewPaymentAllocation: false }
			});
			const view = await renderPanel(entry);
			expect(view.getAllByRole('menuitem').map((item) => item.textContent?.trim())).toEqual(['View detail']);
			await fireEvent.click(view.getByRole('menuitem', { name: 'View detail' }));
			expect(view.getByTestId('panel-flow-view-destination').textContent).toContain(`Tenant entry #${entry.tenantLedgerEntryId}`);
			view.unmount();
		}
	});

	it('routes record detail for deposit, credit, adjustment, and already-reversed rows', async () => {
		const entries = [
			row({
				tenantLedgerEntryId: 64,
				type: 'DepositCharge',
				direction: 'Debit',
				ledgerKind: 'charge',
				chargeAmount: 1500,
				actionCapabilities: {
					canViewDetail: true, canGiveCredit: false, canAddRelatedCharge: false,
					canReverseCharge: false, canReverseLedgerEntry: false, canReviewPaymentAllocation: false
				}
			}),
			row({
				tenantLedgerEntryId: 65,
				type: 'Credit',
				direction: 'Credit',
				ledgerKind: 'credit',
				chargeAmount: 0,
				creditAmount: 250,
				actionCapabilities: {
					canViewDetail: true, canGiveCredit: false, canAddRelatedCharge: false,
					canReverseCharge: false, canReverseLedgerEntry: false, canReviewPaymentAllocation: false
				}
			}),
			row({
				tenantLedgerEntryId: 66,
				type: 'Adjustment',
				direction: 'Credit',
				ledgerKind: 'credit',
				chargeAmount: 0,
				creditAmount: 75,
				actionCapabilities: {
					canViewDetail: true, canGiveCredit: false, canAddRelatedCharge: false,
					canReverseCharge: false, canReverseLedgerEntry: false, canReviewPaymentAllocation: false
				}
			}),
			row({
				tenantLedgerEntryId: 67,
				type: 'RentCharge',
				direction: 'Debit',
				ledgerKind: 'charge',
				chargeAmount: 1950,
				replacedByEntryId: 68,
				actionCapabilities: {
					canViewDetail: true, canGiveCredit: false, canAddRelatedCharge: false,
					canReverseCharge: false, canReverseLedgerEntry: false, canReviewPaymentAllocation: false
				}
			})
		];

		for (const entry of entries) {
			const view = await renderPanel(entry);
			expect(view.getAllByRole('menuitem').map((item) => item.textContent?.trim())).toEqual(['View detail']);
			await fireEvent.click(view.getByRole('menuitem', { name: 'View detail' }));
			expect(view.getByTestId('panel-flow-view-destination').textContent).toContain(`Tenant entry #${entry.tenantLedgerEntryId}`);
			view.unmount();
		}
	});

	it('keeps a missing-journal charge on the record-detail path only', async () => {
		const entry = row({
			tenantLedgerEntryId: 68,
			journalEntryPublicId: null,
			actionCapabilities: {
				canViewDetail: true,
				canGiveCredit: false,
				canAddRelatedCharge: false,
				canReverseCharge: false,
				canReverseLedgerEntry: false,
				canReviewPaymentAllocation: false
			}
		});
		const view = await renderPanel(entry);
		expect(view.getAllByRole('menuitem').map((item) => item.textContent?.trim())).toEqual(['View detail']);
	});
});
