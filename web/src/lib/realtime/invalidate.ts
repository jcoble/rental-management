/**
 * Bridge SignalR data-update events to TanStack Query cache invalidation.
 *
 * The backend broadcasts `EntityUpdated` / `EntityDeleted` keyed by an
 * `entityType` string (Property, Tenant, LeaseManagement, ...). This maps each type to
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
import { invalidateQueriesForDataUpdate } from './invalidate-keys';
export { invalidateQueriesForDataUpdate } from './invalidate-keys';

/**
 * Wire SignalR data-update events to the given QueryClient. Returns an
 * unsubscribe function. Call once (e.g. in the protected layout) — the layout
 * owns the connection lifecycle; this only listens.
 */
export function useInvalidateOnSignalR(queryClient: QueryClient): () => void {
	return signalRService.subscribe((event: DataUpdateEvent, payload: EntityUpdatePayload | EntityDeletePayload) => {
		invalidateQueriesForDataUpdate(queryClient, event, payload);
	});
}
