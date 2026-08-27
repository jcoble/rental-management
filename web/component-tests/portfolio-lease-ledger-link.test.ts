import { cleanup, render } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import PortfolioLeaseLedgerPanel from '$lib/components/accounting/PortfolioLeaseLedgerPanel.svelte';

vi.mock('@tanstack/svelte-query', () => ({
	createQuery: () => ({
		isLoading: false,
		isError: false,
		data: { items: [{ tenantLedgerEntryId: 42, unitId: 8, effectiveOn: '2026-08-01', monthCharges: 1200, monthPaymentsAndCredits: 0, primaryTenantName: 'Ada', unitNumber: '1A', propertyName: 'Example House', description: 'August rent', direction: 'Debit', amount: 1200 }], totalCount: 1, skip: 0, take: 25 }
	})
}));

afterEach(() => cleanup());

describe('portfolio lease ledger links', () => {
	it('opens the unit money ledger instead of the retired rent tab', () => {
		const view = render(PortfolioLeaseLedgerPanel);
		const href = view.getByTestId('portfolio-ledger-row-42').getAttribute('href');

		expect(href).toContain('tab=money&view=tenant-account');
		expect(href).not.toContain('tab=rent');
	});
});
