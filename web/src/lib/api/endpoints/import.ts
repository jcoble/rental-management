/**
 * CSV bulk-import client.
 *
 * The backend exposes:
 *  - POST /api/v1/import/{entityType}?dryRun=true|false  (multipart `file`)
 *      → an {@link ImportResult} with a per-row breakdown.
 *  - GET  /api/v1/import/{entityType}/template
 *      → a downloadable CSV header row.
 *
 * `importCsv` routes through the authenticated fetch client (which attaches the
 * bearer token and refreshes-and-retries on 401), mirroring `scan.upload`.
 * `downloadTemplate` fetches the blob manually with the bearer token attached —
 * a plain <a href> can't send the Authorization header — mirroring the
 * year-end / owner-statement download helpers in accounting.ts.
 */

import { browser } from '$app/environment';
import { CLIENT_API_BASE_URL } from '$lib/config';
import { getAuthState, isTokenExpired } from '$lib/stores/auth.svelte';
import { fetchApi, refreshToken } from '../client';

/** The entity types the CSV importer supports (case-insensitive on the wire). */
export type ImportEntityType = 'tenant' | 'property' | 'unit' | 'payment' | 'expense' | 'loan';

/** One row of the dry-run / import result, 1-based as in the spreadsheet. */
export interface ImportRowResult {
	/** 1-based spreadsheet row number (data starts at row 2). */
	rowNumber: number;
	valid: boolean;
	errors: string[];
	isDuplicate: boolean;
	skipReason: string | null;
	/** Set only on a real (non-dry-run) import once the row is created. */
	createdId: number | null;
}

/** Full preview/import response. */
export interface ImportResult {
	entityType: string;
	dryRun: boolean;
	totalRows: number;
	validRows: number;
	duplicateRows: number;
	createdRows: number;
	rows: ImportRowResult[];
}

const paymentImportOperationIds = new WeakMap<File, string>();

/**
 * Upload a CSV for preview (dryRun=true) or commit (dryRun=false).
 * Returns the per-row breakdown.
 */
export function importCsv(
	entityType: ImportEntityType,
	file: File,
	dryRun: boolean
): Promise<ImportResult> {
	const fd = new FormData();
	fd.append('file', file);
	const headers: Record<string, string> = {};
	if (!dryRun && entityType === 'payment') {
		let operationId = paymentImportOperationIds.get(file);
		if (!operationId) {
			operationId = crypto.randomUUID();
			paymentImportOperationIds.set(file, operationId);
		}
		headers['Idempotency-Key'] = operationId;
	}
	return fetchApi<ImportResult>(`/import/${entityType}?dryRun=${dryRun}`, {
		method: 'POST',
		body: fd,
		headers
	});
}

/**
 * Download the CSV template (header row) for the given entity type with the
 * bearer token attached, triggering a browser download via a temporary object URL.
 */
export async function downloadTemplate(entityType: ImportEntityType): Promise<void> {
	if (!browser) return;

	// Proactively refresh if near expiry, mirroring the main client logic.
	if (isTokenExpired(120)) {
		try {
			await refreshToken();
		} catch {
			// Proceed; bearer may still be usable.
		}
	}

	const { accessToken } = getAuthState();
	const url = `${CLIENT_API_BASE_URL}/import/${entityType}/template`;

	const response = await fetch(url, {
		credentials: 'include',
		headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {}
	});

	if (!response.ok) {
		throw new Error(`Template download failed (${response.status})`);
	}

	const blob = await response.blob();
	const objectUrl = URL.createObjectURL(blob);
	const a = document.createElement('a');
	a.href = objectUrl;
	a.download = `${entityType}-import-template.csv`;
	document.body.appendChild(a);
	a.click();
	document.body.removeChild(a);
	URL.revokeObjectURL(objectUrl);
}
