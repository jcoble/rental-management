/**
 * Bridge SignalR data-update events to TanStack Query cache invalidation.
 *
 * The backend broadcasts `EntityUpdated` / `EntityDeleted` keyed by an
 * `entityType` string (Property, Tenant, Lease, ...). This maps each type to
 * the query-key prefixes that depend on it and invalidates them so the UI
 * refetches. On delete we also `removeQueries` for the detail key of the gone
 * entity to drop its stale cache entry.
 *
 * Mirrors EdiPlatform's signalr-query-bridge, adapted to the rental query keys
 * (`['properties', portfolioId]`, `['leases', portfolioId]`, ...). TanStack
 * matches partially from the start of the key, so invalidating `['properties']`
 * covers every portfolio-scoped variant.
 */

import type { QueryClient } from '@tanstack/svelte-query';
import {
	signalRService,
	type DataUpdateEvent,
	type EntityUpdatePayload,
	type EntityDeletePayload
} from '$lib/realtime/signalr';

/**
 * Maps a backend `entityType` to the query-key prefixes it affects. Keys come
 * from the `createQuery` calls across the rental routes; partial matching means
 * we only need the first (entity) segment.
 */
const entityQueryKeys: Record<string, string[][]> = {
	Property: [['properties'], ['units'], ['dashboard']],
	Unit: [['units'], ['units-for-lease'], ['properties'], ['dashboard']],
	Tenant: [['tenants'], ['dashboard']],
	Lease: [['leases'], ['units-for-lease'], ['dashboard']],
	// Payment/Expense both feed the accounting `/summary` rollup (collected /
	// outstanding / overdue / total expenses), keyed `['accounting-summary', …]`
	// on the accounting page. The earlier `payment-summary` / `expense-summary`
	// keys matched no query, so the summary cards stayed stale on realtime events.
	Payment: [['payments'], ['accounting-summary'], ['dashboard']],
	Expense: [['expenses'], ['accounting-summary'], ['dashboard']],
	WorkOrder: [['work-orders'], ['dashboard']],
	Inspection: [['inspections'], ['dashboard']],
	Appointment: [['appointments'], ['dashboard']],
	Vendor: [['vendors']],
	OwnerEntity: [['owners'], ['dashboard']],
	Portfolio: [['portfolio'], ['portfolios'], ['dashboard']],
	// A confirmed scan creates an Expense (the backend also broadcasts that
	// `Expense` event), so refresh the expense list + accounting summary too —
	// the just-confirmed expense and its effect on the cards show without a reload.
	ScanDraft: [['scans'], ['scan'], ['expenses'], ['accounting-summary'], ['dashboard']],
	// A conversation event (a new message from either side) refreshes the thread list and,
	// via the ['conversation'] prefix, whichever thread is currently open.
	Conversation: [['conversations'], ['conversation']]
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

function invalidateForEntity(queryClient: QueryClient, entityType: string): void {
	const keys = entityQueryKeys[entityType];
	if (keys) {
		for (const key of keys) {
			queryClient.invalidateQueries({ queryKey: key });
		}
	} else {
		// Unknown entity — be conservative and refresh the dashboard summary.
		queryClient.invalidateQueries({ queryKey: ['dashboard'] });
	}
}

function removeDeletedEntity(
	queryClient: QueryClient,
	entityType: string,
	entityId: number
): void {
	const detailKey = entityDetailKey[entityType];
	if (detailKey) {
		queryClient.removeQueries({ queryKey: [detailKey, entityId] });
	}
}

/**
 * Wire SignalR data-update events to the given QueryClient. Returns an
 * unsubscribe function. Call once (e.g. in the protected layout) — the layout
 * owns the connection lifecycle; this only listens.
 */
export function useInvalidateOnSignalR(queryClient: QueryClient): () => void {
	return signalRService.subscribe(
		(event: DataUpdateEvent, payload: EntityUpdatePayload | EntityDeletePayload) => {
			const { entityType, entityId } = payload;
			if (event === 'EntityDeleted') {
				removeDeletedEntity(queryClient, entityType, entityId);
			}
			invalidateForEntity(queryClient, entityType);
		}
	);
}
