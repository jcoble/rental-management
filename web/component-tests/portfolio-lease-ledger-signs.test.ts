import { cleanup, render } from '@testing-library/svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import PortfolioLeaseLedgerPanel from '$lib/components/accounting/PortfolioLeaseLedgerPanel.svelte';

vi.mock('@tanstack/svelte-query', () => ({
	createQuery: (options: () => { queryKey: string[] }) => options().queryKey[0] === 'portfolio-lease-ledger'
		? { isLoading: false, isError: false, data: { items: [
			{ tenantLedgerEntryId: 1, effectiveOn: '2026-08-01', monthCharges: 1075, monthPaymentsAndCredits: 925, unitId: 1, unitNumber: '1A', propertyName: 'Oak House', primaryTenantName: 'Alex', description: 'August rent', direction: 'Debit', amount: 1075, currency: 'USD' },
			{ tenantLedgerEntryId: 2, effectiveOn: '2026-08-02', monthCharges: 1075, monthPaymentsAndCredits: 925, unitId: 1, unitNumber: '1A', propertyName: 'Oak House', primaryTenantName: 'Alex', description: 'Rent payment', direction: 'Credit', amount: 925, currency: 'USD' }
		] } }
		: { isLoading: false, isError: false, data: { items: [] } }
}));

vi.mock('$lib/stores/portfolio.svelte', () => ({ getCurrentPortfolioId: () => 1 }));

afterEach(cleanup);

describe('portfolio lease ledger signs', () => {
	it('shows charges as negative and payments as positive', () => {
		const view = render(PortfolioLeaseLedgerPanel);

		expect(view.getByTestId('portfolio-ledger-row-1').textContent).toContain('-$1,075.00');
		expect(view.getByTestId('portfolio-ledger-row-2').textContent).toContain('$925.00');
		expect(view.getByTestId('portfolio-ledger-row-2').textContent).not.toContain('-$925.00');
	});
});
