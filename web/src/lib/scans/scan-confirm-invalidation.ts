interface QueryInvalidator {
	invalidateQueries(options: { queryKey: unknown[] }): unknown;
}

const COMMON_SCAN_KEYS: unknown[][] = [['scans']];

const ENTITY_KEYS: Record<string, unknown[][]> = {
	Lease: [['leases'], ['properties'], ['tenants'], ['units'], ['units-for-lease'], ['dashboard']],
	Application: [['applications'], ['dashboard']],
	Payment: [['payments'], ['accounting-summary'], ['accounting-transactions'], ['dashboard']],
	Expense: [['expenses'], ['accounting-summary'], ['accounting-transactions'], ['dashboard']],
	WorkOrder: [['work-orders'], ['maintenance'], ['dashboard']]
};

export function invalidateQueriesAfterScanConfirm(queryClient: QueryInvalidator, entityType: string | null | undefined): void {
	const keys = [...COMMON_SCAN_KEYS, ...(entityType ? (ENTITY_KEYS[entityType] ?? []) : [])];
	for (const queryKey of keys) {
		queryClient.invalidateQueries({ queryKey });
	}
}
