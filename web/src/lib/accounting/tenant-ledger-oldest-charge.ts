export interface TenantLedgerSummaryFacts {
	businessDate?: string | null;
	oldestOpenChargeDueOn: string | null;
	oldestOpenChargeAmount: number | null;
}

export interface TenantLedgerOldestChargeDisplay {
	label: 'late' | 'due';
	ageDays: number;
	openAmount: number;
	dueOn: string;
}

function dateOnlyToUtc(iso: string): number {
	return Date.parse(`${iso}T00:00:00Z`);
}

/**
 * Formats only the server-owned oldest-open-charge facts. Visible ledger rows,
 * page size, period, and entry filter are intentionally not inputs here.
 */
export function oldestOpenChargeDisplay(
	facts: TenantLedgerSummaryFacts
): TenantLedgerOldestChargeDisplay | null {
	if (!facts.businessDate || !facts.oldestOpenChargeDueOn || facts.oldestOpenChargeAmount == null || facts.oldestOpenChargeAmount <= 0) {
		return null;
	}
	const ageDays = Math.max(
		0,
		Math.floor((dateOnlyToUtc(facts.businessDate) - dateOnlyToUtc(facts.oldestOpenChargeDueOn)) / 86_400_000)
	);
	return {
		label: ageDays > 0 ? 'late' : 'due',
		ageDays,
		openAmount: facts.oldestOpenChargeAmount,
		dueOn: facts.oldestOpenChargeDueOn,
	};
}
