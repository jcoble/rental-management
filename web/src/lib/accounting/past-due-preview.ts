import type { AccountingPage } from '$lib/api/endpoints/accounting-books';
import {
	tenantLedgers,
	type TenantLedgerParams,
	type TenantLedgerRow
} from '$lib/api/endpoints/tenant-ledgers';

/** The API caps one tenant-ledger response at 200 rows. */
export const OPEN_CHARGES_PAGE_SIZE = 200;

/**
 * A preview is display-only, but it must be complete before a receipt can be submitted. The bound
 * keeps a pathological or continuously changing account from issuing requests forever.
 */
export const MAX_OPEN_CHARGE_PREVIEW_REQUESTS = 32;

export type OpenChargePageLoader = (
	tenantAccountId: number,
	params: Pick<TenantLedgerParams, 'skip' | 'take'> & {
		openOnly: true;
		sort: 'oldestDueOn';
	}
) => Promise<AccountingPage<TenantLedgerRow>>;

export async function loadAllOpenCharges(
	tenantAccountId: number,
	list: OpenChargePageLoader = tenantLedgers.list
): Promise<TenantLedgerRow[]> {
	const items: TenantLedgerRow[] = [];
	let nextSkip = 0;
	let expectedTotalCount: number | null = null;

	for (let requestNumber = 0; requestNumber < MAX_OPEN_CHARGE_PREVIEW_REQUESTS; requestNumber += 1) {
		const page = await list(tenantAccountId, {
			skip: nextSkip,
			take: OPEN_CHARGES_PAGE_SIZE,
			openOnly: true,
			sort: 'oldestDueOn'
		});

		if (!Number.isInteger(page.skip) || page.skip !== nextSkip) {
			throw new Error('Open-charge preview paging returned an unexpected continuation.');
		}
		if (!Number.isInteger(page.totalCount) || page.totalCount < 0) {
			throw new Error('Open-charge preview returned an invalid server total.');
		}
		if (expectedTotalCount === null) {
			expectedTotalCount = page.totalCount;
		} else if (page.totalCount !== expectedTotalCount) {
			throw new Error('Open-charge preview total changed while loading.');
		}
		if (page.items.length > OPEN_CHARGES_PAGE_SIZE) {
			throw new Error('Open-charge preview returned more rows than the server page bound.');
		}

		const pageEnd = page.skip + page.items.length;
		if (page.items.length === 0) {
			if (pageEnd < expectedTotalCount) {
				throw new Error('Open-charge preview paging ended before the server total.');
			}
			return items;
		}
		if (pageEnd <= nextSkip || pageEnd > expectedTotalCount) {
			throw new Error('Open-charge preview paging did not produce a complete ordered page.');
		}

		items.push(...page.items);
		nextSkip = pageEnd;
		if (nextSkip === expectedTotalCount) return items;
	}

	throw new Error(
		`Open-charge preview exceeded the ${MAX_OPEN_CHARGE_PREVIEW_REQUESTS}-request bound.`
	);
}
