import type { AccountingSummary, ScheduleEReport } from '$lib/types';
import { api, refreshToken } from '../client';
import { CLIENT_API_BASE_URL } from '$lib/config';
import { getAuthState, isTokenExpired } from '$lib/stores/auth.svelte';
import { browser } from '$app/environment';

export const accounting = {
	// GET /api/v1/accounting/summary — portfolio scope comes from the JWT claim.
	// Returns expense totals by Schedule E category + a payment collection rollup
	// (collected / outstanding / overdue).
	summary: () => api.get<AccountingSummary>('/accounting/summary'),

	// GET /api/v1/accounting/schedule-e?year=YYYY
	scheduleE: (year: number) => api.get<ScheduleEReport>(`/accounting/schedule-e?year=${year}`),
};

/**
 * Download the Schedule E CSV for the given year with the bearer token attached.
 * A plain <a href> can't send the Authorization header, so we fetch the blob
 * manually and trigger a browser download via a temporary object URL.
 */
export async function downloadScheduleECsv(year: number): Promise<void> {
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
	const url = `${CLIENT_API_BASE_URL}/accounting/schedule-e/export?year=${year}`;

	const response = await fetch(url, {
		credentials: 'include',
		headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {}
	});

	if (!response.ok) {
		throw new Error(`CSV download failed (${response.status})`);
	}

	const blob = await response.blob();
	const objectUrl = URL.createObjectURL(blob);
	const a = document.createElement('a');
	a.href = objectUrl;
	a.download = `schedule-e-${year}.csv`;
	document.body.appendChild(a);
	a.click();
	document.body.removeChild(a);
	URL.revokeObjectURL(objectUrl);
}
