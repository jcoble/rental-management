import { cleanup, fireEvent, render, waitFor } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import AccountingRecordDetailsHarness from './AccountingRecordDetailsHarness.svelte';

const mocks = vi.hoisted(() => ({
	sourceJournals: vi.fn(),
	journalDetail: vi.fn()
}));

vi.mock('$lib/stores/auth.svelte', () => ({
	getAuthState: () => ({ isAuthenticated: true })
}));

vi.mock('$lib/api/endpoints/accounting-books', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/accounting-books')>(
		'$lib/api/endpoints/accounting-books'
	);
	return {
		...actual,
		accountingBooks: {
			...actual.accountingBooks,
			sourceJournals: mocks.sourceJournals,
			journalDetail: mocks.journalDetail
		}
	};
});

afterEach(() => cleanup());

const lines = [
	{
		id: 11,
		accountId: 101,
		accountCode: '1100',
		accountName: 'Tenant Accounts Receivable',
		normalBalance: 'Debit' as const,
		accountType: 'Asset' as const,
		systemKey: 'tenant-accounts-receivable',
		debitAmount: 1950,
		creditAmount: 0,
		memo: null,
		propertyId: 7,
		unitId: 8,
		tenantAccountId: 9,
		ownerEntityId: null
	},
	{
		id: 12,
		accountId: 401,
		accountCode: '4100',
		accountName: 'Rental Income',
		normalBalance: 'Credit' as const,
		accountType: 'Income' as const,
		systemKey: 'rental-income',
		debitAmount: 0,
		creditAmount: 1950,
		memo: null,
		propertyId: 7,
		unitId: 8,
		tenantAccountId: 9,
		ownerEntityId: null
	}
];

const sourceJournal = {
	publicId: 'journal-1',
	effectiveOn: '2026-08-01',
	postedAtUtc: '2026-08-01T12:00:00Z',
	sourceType: 'TenantCharge' as const,
	description: 'Rent for August 2026',
	currency: 'USD',
	totalDebits: 1950,
	totalCredits: 1950,
	isReversal: false,
	reversesPublicId: null,
	lines
};

const journalDetail = {
	publicId: 'journal-1',
	description: 'Rent for August 2026',
	effectiveOn: '2026-08-01',
	postedAtUtc: '2026-08-01T12:00:00Z',
	sourceType: 'TenantCharge' as const,
	sourceId: 17,
	sourceBusinessKey: 'tenant-charge:17:2026-08',
	actor: 'Demo landlord',
	attemptId: 'attempt-1',
	atomicReceiptId: 'receipt-1',
	idempotencyDigest: 'digest-1',
	currency: 'USD',
	lines,
	totalDebits: 1950,
	totalCredits: 1950,
	isBalanced: true,
	reversesJournalEntryPublicId: null,
	reversalPublicIds: [],
	auditLink: null,
	documentIds: [],
	bankReconciliationEvidence: null
};

describe('rendered accounting record details', () => {
	beforeEach(() => {
		mocks.sourceJournals.mockResolvedValue([sourceJournal]);
		mocks.journalDetail.mockResolvedValue(journalDetail);
	});

	it('uses landlord sentences in Simple mode and raw posting facts only in Advanced mode', async () => {
		const view = render(AccountingRecordDetailsHarness, { props: { sourceId: 17 } });

		await waitFor(() => {
			expect(view.getByTestId('accounting-impact-line-11').textContent).toContain('Tenant now owes $1,950 more');
			expect(view.getByTestId('journal-detail-line-11').textContent).toContain('Tenant now owes $1,950 more');
		});
		expect(view.getByTestId('accounting-impact-line-12').textContent).toContain('Counted as rent earned for August 2026');
		expect(view.getByTestId('journal-detail-line-12').textContent).toContain('Counted as rent earned for August 2026');
		expect(view.queryByText('Tenant Accounts Receivable')).toBeNull();
		expect(view.queryByText('Rental Income')).toBeNull();

		await fireEvent.click(view.getByTestId('test-accounting-detail-mode-advanced'));
		await waitFor(() => {
			expect(view.getByTestId('accounting-impact-line-11').textContent).toContain('Tenant Accounts Receivable');
			expect(view.getByTestId('journal-detail-line-11').textContent).toContain('Tenant Accounts Receivable');
		});
		expect(view.getByTestId('accounting-impact-line-11').textContent).toContain('Debit');
		expect(view.getByTestId('journal-detail-line-11').textContent).toContain('Debit $1,950.00');
		expect(view.getByTestId('accounting-impact-line-12').textContent).toContain('Rental Income');
		expect(view.getByTestId('journal-detail-line-12').textContent).toContain('Credit $1,950.00');
	});
});
