import { normalizeTenantLedgerDescription } from './tenant-ledger-display';
import type { AllocationRef, TenantLedgerRow } from '$lib/api/endpoints/tenant-ledgers';

export type TenantLedgerRowAction =
	| 'view'
	| 'give-credit'
	| 'related-charge'
	| 'fix-charge'
	| 'fix-payment';

export interface TenantLedgerRelatedChargeSeed {
	description: string;
	effectiveOn: string;
	dueOn: string;
	chargeType: 'Other';
}

export type TenantLedgerRowActionFlow =
	| { kind: 'view'; row: TenantLedgerRow; entryId: number; journalPublicId: string | null }
	| { kind: 'give-credit'; row: TenantLedgerRow; targetEntryId: number }
	| { kind: 'related-charge'; row: TenantLedgerRow; seed: TenantLedgerRelatedChargeSeed }
	| { kind: 'reverse'; row: TenantLedgerRow; targetEntryId: number }
	| { kind: 'allocation-review'; row: TenantLedgerRow; entryId: number; allocations: AllocationRef[] };

export function buildTenantLedgerRowActionFlow(
	row: TenantLedgerRow,
	action: TenantLedgerRowAction
): TenantLedgerRowActionFlow {
	switch (action) {
		case 'view':
			return {
				kind: 'view',
				row,
				entryId: row.tenantLedgerEntryId,
				journalPublicId: row.journalEntryPublicId
			};
		case 'give-credit':
			return { kind: 'give-credit', row, targetEntryId: row.tenantLedgerEntryId };
		case 'related-charge':
			return {
				kind: 'related-charge',
				row,
				seed: {
					description: `Additional charge related to ${normalizeTenantLedgerDescription(row.description)}`,
					effectiveOn: row.effectiveOn,
					dueOn: row.dueOn ?? row.effectiveOn,
					chargeType: 'Other'
				}
			};
		case 'fix-charge':
			return { kind: 'reverse', row, targetEntryId: row.tenantLedgerEntryId };
		case 'fix-payment':
			return {
				kind: 'allocation-review',
				row,
				entryId: row.tenantLedgerEntryId,
				allocations: row.allocations
			};
	}
}

export function buildTenantLedgerReversalRequest(
	tenantAccountId: number,
	row: TenantLedgerRow,
	effectiveOn: string
): {
	endpoint: string;
	body: { reversesEntryId?: number; effectiveOn: string; reason: string };
} {
	const description = normalizeTenantLedgerDescription(row.description);
	if (row.type === 'OpeningBalance') {
		return {
			endpoint: `/tenant-accounts/${tenantAccountId}/reversals`,
			body: {
				reversesEntryId: row.tenantLedgerEntryId,
				effectiveOn,
				reason: `Reverse opening balance: ${description}`
			}
		};
	}
	return {
		endpoint: `/tenant-accounts/${tenantAccountId}/charges/${row.tenantLedgerEntryId}/reversals`,
		body: { effectiveOn, reason: `Reverse charge: ${description}` }
	};
}
