import { browser } from '$app/environment';
import type { AuditEntry, AdminAuditEntry } from '$lib/types';
import { api, downloadFile } from '../client';
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

	// Admin-only CSV export of the CURRENTLY FILTERED forensic set (search + operation + entityType +
	// entityId honored; paging ignored — the export is the whole filtered trail). Streamed from the API.
	// Route: GET /api/v1/admin/audit/export. Fetches the blob with the bearer token attached and saves
	// it via a temporary object URL (a plain <a href> can't carry the Authorization header).
	adminExportCsv: async (_portfolioId: number, params?: AuditFilters): Promise<void> => {
		if (!browser) return;
		const { operation, entityType, entityId, ...list } = params ?? {};
		const blob = await downloadFile(
			`/admin/audit/export${buildListQuery(list, { operation, entityType, entityId })}`
		);

		const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-');
		const objectUrl = URL.createObjectURL(blob);
		const a = document.createElement('a');
		a.href = objectUrl;
		a.download = `audit-${stamp}.csv`;
		document.body.appendChild(a);
		a.click();
		document.body.removeChild(a);
		URL.revokeObjectURL(objectUrl);
	},
};
