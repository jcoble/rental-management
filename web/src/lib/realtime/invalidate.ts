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
	Payment: [['payments'], ['payment-summary'], ['dashboard']],
	Expense: [['expenses'], ['expense-summary'], ['dashboard']],
	WorkOrder: [['work-orders'], ['dashboard']],
	Inspection: [['inspections'], ['dashboard']],
	Appointment: [['appointments'], ['dashboard']],
	Vendor: [['vendors']],
	OwnerEntity: [['owners'], ['dashboard']],
	Portfolio: [['portfolio'], ['portfolios'], ['dashboard']],
	ScanDraft: [['scans'], ['scan']]
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
	ScanDraft: 'scan'
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
