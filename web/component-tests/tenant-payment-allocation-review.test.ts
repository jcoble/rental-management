import { cleanup, render } from '@testing-library/svelte';
import { afterEach, describe, expect, it } from 'vitest';
import TenantPaymentAllocationReview from '$lib/components/accounting/TenantPaymentAllocationReview.svelte';

afterEach(() => cleanup());

describe('payment allocation review surface', () => {
	it('renders the selected payment entry and every server-projected allocation target', () => {
		const view = render(TenantPaymentAllocationReview, {
			props: {
				open: true,
				entryId: 52,
				currency: 'USD',
				allocations: [
					{
						allocationId: 701,
						targetSourceId: 7,
						targetPublicId: 'charge-7',
						targetDescription: 'Rent for 2026-07',
						amount: 1250,
						effectiveOn: '2026-07-01'
					},
					{
						allocationId: 702,
						targetSourceId: 8,
						targetPublicId: 'charge-8',
						targetDescription: 'Rent for August 2026',
						amount: 700,
						effectiveOn: '2026-08-01'
					}
				],
				onclose: () => undefined
			}
		});

		expect(view.getByTestId('tenant-payment-allocation-entry-id').textContent).toContain('Payment entry #52');
		expect(view.getByTestId('tenant-payment-allocation-list').textContent).toContain('Rent for July 2026');
		expect(view.getByTestId('tenant-payment-allocation-list').textContent).toContain('$1,250.00');
		expect(view.getByTestId('tenant-payment-allocation-list').textContent).toContain('Rent for August 2026');
		expect(view.getByTestId('tenant-payment-allocation-list').textContent).toContain('$700.00');
	});
});
