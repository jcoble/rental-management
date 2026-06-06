import type { AuditEntry, AdminAuditEntry } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

type AuditFilters = ListParams & { operation?: string; entityType?: string; entityId?: number };

export const audit = {
	// The API scopes the audit trail to the caller's portfolio via the JWT portfolioId claim, so the
	// portfolio is not part of the path. `portfolioId` is kept only for query-cache keying by callers.
	// Route: GET /api/v1/audit?skip&take&search&sort&operation&entityType&entityId.
	list: (_portfolioId: number, params?: AuditFilters) => {
		const { operation, entityType, entityId, ...list } = params ?? {};
		return api.get<AuditEntry[]>(`/audit${buildListQuery(list, { operation, entityType, entityId })}`);
	},

	// Admin-only forensic variant: same filters, but each row also carries the IP address and raw
	// old→new JSON. Route: GET /api/v1/admin/audit (gated behind the Admin role server-side).
	adminList: (_portfolioId: number, params?: AuditFilters) => {
		const { operation, entityType, entityId, ...list } = params ?? {};
		return api.get<AdminAuditEntry[]>(
			`/admin/audit${buildListQuery(list, { operation, entityType, entityId })}`
		);
	},
};
