/**
 * "Connect your accounting" settings page (provider-agnostic).
 *
 * Every endpoint here is Bearer-authenticated. We post/get from server actions and the loader via
 * `serverGet`/`serverPost(..., locals.accessToken!)`, which set the Authorization header exactly once
 * on a direct fetch to the API. We deliberately do NOT use `event.fetch`: that routes through
 * `handleFetch`, which injects Authorization too, and the double-inject produces a malformed
 * `Bearer X, Bearer Y` header and a silent 401. (Same gotcha as settings/security.)
 *
 * The shell is provider-agnostic: the loader returns the raw `status` array (one entry per provider)
 * and the page renders a card per entry. A second provider needs zero changes here — its row simply
 * appears in `status`, and its mappings/review-queue load alongside QuickBooks'.
 */

import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { serverGet, serverPost } from '$lib/api/server-fetch';

const BASE = '/integrations/accounting';

/** Per-direction pull/push capability matrix for a provider. */
export interface AccountingCapabilities {
	canPullCustomers: boolean;
	canPullVendors: boolean;
	canPullAccounts: boolean;
	canPullPayments: boolean;
	canPullExpenses: boolean;
	canPushIncome: boolean;
	canPushExpense: boolean;
}

/** One status card per available accounting provider (connected or not). */
export interface AccountingConnectionStatus {
	provider: string;
	providerName: string;
	configured: boolean;
	status: 'Pending' | 'Connected' | 'NeedsReconnect' | 'Error' | 'Disconnected' | null;
	companyName: string | null;
	connectedAt: string | null;
	lastSyncedAt: string | null;
	lastError: string | null;
	pullEnabled: boolean;
	pushEnabled: boolean;
	pendingReviewCount: number;
	importedCount: number;
	capabilities: AccountingCapabilities | null;
}

/** One entity mapping (suggested or confirmed) for the mapping-review panel. */
export interface AccountingMapping {
	id: number;
	externalType: string;
	externalId: string;
	externalDisplayName: string | null;
	localEntityType: string;
	localEntityId: number | null;
	localEnumValue: string | null;
	confidence: number | null;
	confirmed: boolean;
	confirmedAt: string | null;
}

/** One imported transaction parked in the review queue (unmatched / needs-review). */
export interface AccountingReviewItem {
	id: number;
	externalType: string;
	externalId: string;
	status: string;
	reason: string | null;
}

/** A provider plus its lazily-loaded mappings + review queue (only fetched once connected). */
export interface ProviderView {
	status: AccountingConnectionStatus;
	unconfirmedMappings: AccountingMapping[];
	confirmedMappings: AccountingMapping[];
	unconfirmedMappingsHasMore: boolean;
	confirmedMappingsHasMore: boolean;
	reviewQueue: AccountingReviewItem[];
	reviewQueueHasMore: boolean;
	loadError: string | null;
}

const MAPPING_SECTION_SIZE = 50;
const MAPPING_FETCH_SIZE = MAPPING_SECTION_SIZE + 1;
const REVIEW_QUEUE_SIZE = 50;
const REVIEW_QUEUE_FETCH_SIZE = REVIEW_QUEUE_SIZE + 1;

export const load: PageServerLoad = async ({ locals }) => {
	if (!locals.accessToken) {
		return { providers: [] as ProviderView[], loadError: 'Your session has expired. Please sign in again.' };
	}

	const statusResult = await serverGet<AccountingConnectionStatus[]>(`${BASE}/status`, locals.accessToken);
	if (statusResult.error || !statusResult.data) {
		return { providers: [] as ProviderView[], loadError: statusResult.error ?? 'Could not load accounting providers.' };
	}

	// For each provider that's actually connected, pull its mappings + review queue alongside.
	// These are independent REST resources, so fetch them in parallel rather than serially.
	const providers = await Promise.all(
		statusResult.data.map(async (status): Promise<ProviderView> => {
			const isConnected = status.status === 'Connected' || status.status === 'NeedsReconnect';
			if (!isConnected) {
				return {
					status,
					unconfirmedMappings: [],
					confirmedMappings: [],
					unconfirmedMappingsHasMore: false,
					confirmedMappingsHasMore: false,
					reviewQueue: [],
					reviewQueueHasMore: false,
					loadError: null
				};
			}

			const [unconfirmedResult, confirmedResult, reviewResult] = await Promise.all([
				serverGet<AccountingMapping[]>(
					`${BASE}/${status.provider}/mappings?confirmed=false&take=${MAPPING_FETCH_SIZE}`,
					locals.accessToken!
				),
				serverGet<AccountingMapping[]>(
					`${BASE}/${status.provider}/mappings?confirmed=true&take=${MAPPING_FETCH_SIZE}`,
					locals.accessToken!
				),
				serverGet<AccountingReviewItem[]>(
					`${BASE}/${status.provider}/review-queue?take=${REVIEW_QUEUE_FETCH_SIZE}`,
					locals.accessToken!
				)
			]);

			const unconfirmed = unconfirmedResult.data ?? [];
			const confirmed = confirmedResult.data ?? [];
			const reviewQueue = reviewResult.data ?? [];
			return {
				status,
				unconfirmedMappings: unconfirmed.slice(0, MAPPING_SECTION_SIZE),
				confirmedMappings: confirmed.slice(0, MAPPING_SECTION_SIZE),
				unconfirmedMappingsHasMore: unconfirmed.length > MAPPING_SECTION_SIZE,
				confirmedMappingsHasMore: confirmed.length > MAPPING_SECTION_SIZE,
				reviewQueue: reviewQueue.slice(0, REVIEW_QUEUE_SIZE),
				reviewQueueHasMore: reviewQueue.length > REVIEW_QUEUE_SIZE,
				loadError: unconfirmedResult.error ?? confirmedResult.error ?? reviewResult.error ?? null
			};
		})
	);

	return { providers, loadError: null };
};

export const actions: Actions = {
	// Begin a connect flow. Returns the provider authorize URL; the client does a full-page
	// browser redirect to it (the OAuth consent screen lives on the provider's domain).
	connect: async ({ request, locals }) => {
		if (!locals.accessToken) {
			return fail(401, { error: 'Your session has expired. Please sign in again.' });
		}
		const provider = (await request.formData()).get('provider')?.toString();
		if (!provider) {
			return fail(400, { error: 'Missing provider.' });
		}

		const result = await serverPost<{ authorizeUrl: string }>(
			`${BASE}/${provider}/connect`,
			locals.accessToken
		);
		if (result.error || !result.data?.authorizeUrl) {
			return fail(result.status || 400, { error: result.error ?? 'Could not start the connection.' });
		}
		return { authorizeUrl: result.data.authorizeUrl };
	},

	disconnect: async ({ request, locals }) => {
		if (!locals.accessToken) {
			return fail(401, { error: 'Your session has expired. Please sign in again.' });
		}
		const provider = (await request.formData()).get('provider')?.toString();
		if (!provider) {
			return fail(400, { error: 'Missing provider.' });
		}

		const result = await serverPost(`${BASE}/${provider}/disconnect`, locals.accessToken);
		if (result.error) {
			return fail(result.status || 400, { error: result.error });
		}
		return { disconnected: true };
	},

	setDirection: async ({ request, locals }) => {
		if (!locals.accessToken) {
			return fail(401, { error: 'Your session has expired. Please sign in again.' });
		}
		const form = await request.formData();
		const provider = form.get('provider')?.toString();
		if (!provider) {
			return fail(400, { error: 'Missing provider.' });
		}
		const pullEnabled = form.get('pullEnabled') === 'true';
		const pushEnabled = form.get('pushEnabled') === 'true';

		const result = await serverPost(`${BASE}/${provider}/direction`, locals.accessToken, {
			pullEnabled,
			pushEnabled
		});
		if (result.error) {
			return fail(result.status || 400, { error: result.error });
		}
		return { directionSaved: true };
	},

	import: async ({ request, locals }) => {
		if (!locals.accessToken) {
			return fail(401, { error: 'Your session has expired. Please sign in again.' });
		}
		const form = await request.formData();
		const provider = form.get('provider')?.toString();
		if (!provider) {
			return fail(400, { error: 'Missing provider.' });
		}
		const fromDate = form.get('fromDate')?.toString() || null;
		const toDate = form.get('toDate')?.toString() || null;

		const result = await serverPost<{
			customersMapped: number;
			vendorsMapped: number;
			accountsMapped: number;
			paymentsImported: number;
			expensesImported: number;
			needsReview: number;
		}>(`${BASE}/${provider}/import`, locals.accessToken, { fromDate, toDate });
		if (result.error || !result.data) {
			return fail(result.status || 400, { error: result.error ?? 'Import failed.' });
		}
		return { imported: result.data };
	},

	confirmMapping: async ({ request, locals }) => {
		if (!locals.accessToken) {
			return fail(401, { error: 'Your session has expired. Please sign in again.' });
		}
		const form = await request.formData();
		const provider = form.get('provider')?.toString();
		const externalType = form.get('externalType')?.toString();
		const externalId = form.get('externalId')?.toString();
		const localEntityType = form.get('localEntityType')?.toString();
		if (!provider || !externalType || !externalId || !localEntityType) {
			return fail(400, { error: 'Missing mapping details.' });
		}

		const localEntityIdRaw = form.get('localEntityId')?.toString();
		const localEnumValue = form.get('localEnumValue')?.toString() || null;
		const externalDisplayName = form.get('externalDisplayName')?.toString() || null;

		const result = await serverPost<{ promoted: number }>(
			`${BASE}/${provider}/mappings/confirm`,
			locals.accessToken,
			{
				externalType,
				externalId,
				externalDisplayName,
				localEntityType,
				localEntityId: localEntityIdRaw ? Number(localEntityIdRaw) : null,
				localEnumValue
			}
		);
		if (result.error) {
			return fail(result.status || 400, { error: result.error });
		}
		return { mappingConfirmed: true, promoted: result.data?.promoted ?? 0 };
	}
};
