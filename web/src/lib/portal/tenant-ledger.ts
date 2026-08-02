import type { PortalTenantAccountHistoryItem } from '$lib/api/endpoints/portal';

/**
 * The portal's complete public vocabulary for account-history rows.
 * Backend enum names and descriptions stay behind this boundary.
 */
export type TenantLedgerLabel =
	| 'Rent charge'
	| 'Late fee'
	| 'Payment received — thank you'
	| 'Credit'
	| 'Refund'
	| 'Correction';

/**
 * Map a server history row to tenant language without changing any server amount
 * or balance. Unknown corrections stay intentionally generic instead of leaking
 * an accounting enum or management description.
 */
export function tenantLedgerLabel(
	entry: Pick<PortalTenantAccountHistoryItem, 'entryType' | 'direction'>
): TenantLedgerLabel {
	switch (entry.entryType) {
		case 'RentCharge':
		case 'AddendumCharge':
		case 'DepositCharge':
		case 'ManualCharge':
		case 'OpeningBalance':
			return 'Rent charge';
		case 'LateFeeCharge':
			return 'Late fee';
		case 'PaymentReceipt':
			return 'Payment received — thank you';
		case 'Credit':
		case 'TransferIn':
			return 'Credit';
		case 'Refund':
			return 'Refund';
		case 'Adjustment':
		case 'Reversal':
		case 'TransferOut':
		default:
			return 'Correction';
	}
}
