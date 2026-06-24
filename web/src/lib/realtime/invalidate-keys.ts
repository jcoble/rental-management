export type DataUpdateEventName = 'EntityUpdated' | 'EntityDeleted';

export interface DataUpdatePayloadLike {
	entityType: string;
	entityId: number;
	data?: unknown;
	timestamp?: string;
}

export interface QueryCacheInvalidator {
	invalidateQueries(options: { queryKey: unknown[] }): unknown;
	removeQueries(options: { queryKey: unknown[] }): unknown;
}

/**
 * Maps a backend `entityType` to the query-key prefixes it affects. Keys come
 * from the `createQuery` calls across the rental routes; partial matching means
 * we only need the first (entity) segment.
 */
const entityQueryKeys: Record<string, string[][]> = {
	Property: [['properties'], ['units'], ['dashboard']],
	Unit: [['units'], ['units-for-lease'], ['properties'], ['dashboard'], ['unit-dashboard'], ['unit-timeline']],
	Tenant: [['tenants'], ['dashboard'], ['unit-dashboard']],
	Lease: [['leases'], ['units-for-lease'], ['dashboard'], ['unit-dashboard'], ['unit-timeline']],
	// Payment/Expense both feed the accounting `/summary` rollup (collected /
	// outstanding / overdue / total expenses), keyed `['accounting-summary', ...]`
	// on the accounting page. The earlier `payment-summary` / `expense-summary`
	// keys matched no query, so the summary cards stayed stale on realtime events.
	Payment: [['payments'], ['accounting-summary'], ['dashboard'], ['unit-dashboard'], ['unit-timeline']],
	Expense: [['expenses'], ['accounting-summary'], ['dashboard'], ['unit-expenses'], ['unit-dashboard'], ['unit-timeline']],
	WorkOrder: [['work-orders'], ['dashboard'], ['unit-work-orders'], ['unit-dashboard'], ['unit-timeline']],
	Inspection: [['inspections'], ['dashboard'], ['unit-dashboard'], ['unit-timeline']],
	Appointment: [['appointments'], ['dashboard'], ['unit-dashboard'], ['unit-timeline']],
	Vendor: [['vendors']],
	OwnerEntity: [['owners'], ['dashboard']],
	Portfolio: [['portfolio'], ['portfolios'], ['dashboard']],
	// A confirmed scan creates an Expense (the backend also broadcasts that
	// `Expense` event), so refresh the expense list + accounting summary too -
	// the just-confirmed expense and its effect on the cards show without a reload.
	ScanDraft: [['scans'], ['scan'], ['expenses'], ['accounting-summary'], ['dashboard']],
	// A conversation event (a new message from either side) refreshes the thread list and,
	// via the ['conversation'] prefix, whichever thread is currently open. The
	// `portal-*` keys cover the tenant-side portal messenger so a landlord message
	// lands live in the resident portal too.
	Conversation: [
		['conversations'],
		['conversation'],
		['portal-conversations'],
		['portal-conversation'],
		['header-unread-messages']
	]
};

/** Detail query-key prefix for an entity, used to drop a deleted entity's cache. */
const entityDetailKey: Record<string, string> = {
	Property: 'property',
	Unit: 'unit',
	Tenant: 'tenant',
	Lease: 'lease',
	Payment: 'payment',
	Expense: 'expense',
	WorkOrder: 'work-order',
	Inspection: 'inspection',
	Appointment: 'appointment',
	Vendor: 'vendor',
	OwnerEntity: 'owner',
	Portfolio: 'portfolio',
	ScanDraft: 'scan',
	Conversation: 'conversation'
};

function invalidateForEntity(queryClient: QueryCacheInvalidator, entityType: string): void {
	const keys = entityQueryKeys[entityType];
	if (keys) {
		for (const key of keys) {
			queryClient.invalidateQueries({ queryKey: key });
		}
	} else {
		// Unknown entity - be conservative and refresh the dashboard summary.
		queryClient.invalidateQueries({ queryKey: ['dashboard'] });
	}
}

function numberField(data: unknown, key: string): number | null {
	if (!data || typeof data !== 'object') return null;
	const value = (data as Record<string, unknown>)[key];
	if (typeof value === 'number' && Number.isFinite(value)) return value;
	if (typeof value === 'string') {
		const parsed = Number(value);
		if (Number.isFinite(parsed) && parsed > 0) return parsed;
	}
	return null;
}

function invalidateDerivedKeys(
	queryClient: QueryCacheInvalidator,
	event: DataUpdateEventName,
	payload: DataUpdatePayloadLike
): void {
	if (event !== 'EntityUpdated' || payload.entityType !== 'Unit') return;
	const propertyId = numberField(payload.data, 'propertyId');
	if (propertyId != null) {
		queryClient.invalidateQueries({ queryKey: ['property', propertyId] });
	}
}

function removeDeletedEntity(
	queryClient: QueryCacheInvalidator,
	entityType: string,
	entityId: number
): void {
	const detailKey = entityDetailKey[entityType];
	if (detailKey) {
		queryClient.removeQueries({ queryKey: [detailKey, entityId] });
	}
}

export function invalidateQueriesForDataUpdate(
	queryClient: QueryCacheInvalidator,
	event: DataUpdateEventName,
	payload: DataUpdatePayloadLike
): void {
	const { entityType, entityId } = payload;
	if (event === 'EntityDeleted') {
		removeDeletedEntity(queryClient, entityType, entityId);
	}
	invalidateForEntity(queryClient, entityType);
	invalidateDerivedKeys(queryClient, event, payload);
}
