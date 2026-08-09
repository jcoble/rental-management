import type { TenantMonthSummary } from '../api/endpoints/tenant-ledgers';

const MONEY_TOLERANCE = 0.005;

/** The tenant ledger is charge-based: credits reduce the amount owed just like payments. */
export function tenantMonthClosingBalance(summary: Pick<
	TenantMonthSummary,
	'openingBalance' | 'chargeAmount' | 'paymentAmount' | 'creditAmount'
>): number {
	return summary.openingBalance + summary.chargeAmount - summary.paymentAmount - summary.creditAmount;
}

export function tenantMonthSummaryReconciles(summary: TenantMonthSummary): boolean {
	return Math.abs(tenantMonthClosingBalance(summary) - summary.closingBalance) < MONEY_TOLERANCE;
}
