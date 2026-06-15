/**
 * Shared data hook for the getting-started checklist. Runs the SAME list/settings queries the rest of
 * the app already uses (so results are served from the TanStack cache, not re-fetched) and projects
 * them into the small {@link GettingStartedSignals} bag the checklist auto-checks against.
 *
 * Crucially: completion is derived from list endpoints that are already loaded elsewhere — there are
 * NO per-task or per-row API calls. "Has a unit" comes from `sum(property.unitCount)` on the property
 * list, not a units fetch per property.
 *
 * Returns plain getter functions (not `$derived` values) so callers can read them reactively inside
 * their own components without re-deriving the query wiring.
 */
import { createQuery } from '@tanstack/svelte-query';
import { portfolios } from '$lib/api/endpoints/portfolios';
import { owners } from '$lib/api/endpoints/owners';
import { properties } from '$lib/api/endpoints/properties';
import { tenants } from '$lib/api/endpoints/tenants';
import { leases } from '$lib/api/endpoints/leases';
import { notifications } from '$lib/api/endpoints/notifications';
import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
import type { GettingStartedSignals } from './getting-started-tasks';

export interface UseGettingStarted {
	/** Live, projected completion signals. */
	signals: () => GettingStartedSignals;
	/** True once every underlying query has resolved (so the UI doesn't flash "0 done" while loading). */
	ready: () => boolean;
	/** This portfolio is a pre-seeded demo (Sandbox) rather than the user's real Live data. */
	isSandbox: () => boolean;
}

export function useGettingStarted(): UseGettingStarted {
	const portfolioId = () => getCurrentPortfolioId();

	const portfolioQuery = createQuery(() => ({
		queryKey: ['portfolio', portfolioId()],
		enabled: portfolioId() > 0,
		queryFn: () => portfolios.get(portfolioId()),
	}));
	const ownersQuery = createQuery(() => ({
		queryKey: ['owners', portfolioId()],
		enabled: portfolioId() > 0,
		queryFn: () => owners.list(portfolioId(), { take: 1 }),
	}));
	const propertiesQuery = createQuery(() => ({
		// Reuse the list (small portfolios); unitCount per row lets us derive "has a unit" with no
		// per-property units call. take: 500 matches the properties page query so the cache is shared.
		queryKey: ['properties', portfolioId()],
		enabled: portfolioId() > 0,
		queryFn: () => properties.list(portfolioId(), { take: 500 }),
	}));
	const tenantsQuery = createQuery(() => ({
		queryKey: ['tenants', portfolioId()],
		enabled: portfolioId() > 0,
		queryFn: () => tenants.list(portfolioId(), { take: 1 }),
	}));
	const leasesQuery = createQuery(() => ({
		queryKey: ['leases', portfolioId()],
		enabled: portfolioId() > 0,
		queryFn: () => leases.list(portfolioId(), { take: 1 }),
	}));
	const notificationEmailQuery = createQuery(() => ({
		queryKey: ['notification-email', portfolioId()],
		enabled: portfolioId() > 0,
		queryFn: () => notifications.getNotificationEmail(),
	}));
	const notificationSettingsQuery = createQuery(() => ({
		queryKey: ['notification-settings'],
		enabled: portfolioId() > 0,
		queryFn: () => notifications.getSettings(),
	}));
	const sandboxQuery = createQuery(() => ({
		queryKey: ['sandbox-state', portfolioId()],
		enabled: portfolioId() > 0,
		queryFn: () => portfolios.sandboxState(),
		staleTime: 60_000,
	}));

	function signals(): GettingStartedSignals {
		const props = propertiesQuery.data ?? [];
		const settings = notificationSettingsQuery.data;
		return {
			portfolioNamed: !!portfolioQuery.data?.name?.trim(),
			ownerCount: ownersQuery.data?.length ?? 0,
			propertyCount: props.length,
			unitCount: props.reduce((sum, p) => sum + (p.unitCount ?? 0), 0),
			tenantCount: tenantsQuery.data?.length ?? 0,
			leaseCount: leasesQuery.data?.length ?? 0,
			hasNotificationEmail: !!notificationEmailQuery.data?.email,
			hasTexting:
				settings?.smsCredentialASet === true || settings?.smsCredentialBSet === true,
			hasAutomations:
				settings?.enableRentCharges === true ||
				settings?.enableLateFees === true ||
				settings?.enableLeaseExpiryReminders === true,
		};
	}

	function ready(): boolean {
		return (
			portfolioQuery.isSuccess &&
			ownersQuery.isSuccess &&
			propertiesQuery.isSuccess &&
			tenantsQuery.isSuccess &&
			leasesQuery.isSuccess &&
			notificationEmailQuery.isSuccess &&
			notificationSettingsQuery.isSuccess
		);
	}

	function isSandbox(): boolean {
		return sandboxQuery.data?.isSandbox === true;
	}

	return { signals, ready, isSandbox };
}
