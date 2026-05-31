import type { AccountingSummary } from '$lib/types';
import { api } from '../client';

export const accounting = {
	// GET /api/v1/accounting/summary — portfolio scope comes from the JWT claim.
	// Returns expense totals by Schedule E category + a payment collection rollup
	// (collected / outstanding / overdue).
	summary: () => api.get<AccountingSummary>('/accounting/summary'),
};
