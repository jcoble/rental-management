import type { AuditEntry } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

export const audit = {
	// The API scopes the audit trail to the caller's portfolio via the JWT portfolioId claim, so the
	// portfolio is not part of the path. `portfolioId` is kept only for query-cache keying by callers.
	// Route: GET /api/v1/audit?skip&take&search&sort&operation&entityType&entityId.
	list: (
		_portfolioId: number,
		params?: ListParams & { operation?: string; entityType?: string; entityId?: number }
	) => {
		const { operation, entityType, entityId, ...list } = params ?? {};
		return api.get<AuditEntry[]>(`/audit${buildListQuery(list, { operation, entityType, entityId })}`);
	},
};
