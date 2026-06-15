/**
 * Shared data hook for the getting-started checklist. Runs the SAME list/settings queries the rest of
 * the app already uses (so results are served from the TanStack cache, not re-fetched) and projects
 * them into the small {@link GettingStartedSignals} bag the checklist auto-checks against.
 *
 * Crucially: completion is derived from list endpoints that are already loaded elsewhere — there are
 * NO per-task or per-row API calls. "Has a unit" comes from `sum(property.unitCount)` on the property
 * list, not a units fetch per property.
 *
 * Hot-path gate (Q-6): the dashboard mounts this on every paint. The five CORE queries (portfolio,
 * owners, properties, tenants, leases) double as data the dashboard/app loads anyway. The three
 * OPTIONAL-ONLY queries — notification email, notification settings, sandbox state — feed only the
 * optional tasks and aren't otherwise loaded by the dashboard, so they are gated: they run only while
 * the account is "plausibly still onboarding" (core spine incomplete, OR the checklist hasn't been
 * observed fully done yet — a persisted per-portfolio "settled" flag). Once everything is done that flag
 * is set and a fully-set-up landlord's dashboard stops firing those three requests entirely.
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
import { isChecklistSettled, markChecklistSettled } from './getting-started-progress.svelte';

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

	// Cheap, persisted gate, mirrored into reactive state so flipping it stops the optional-only queries
	// immediately (within the session) and re-reads when the portfolio changes. Once the checklist has
	// been seen fully done this is true and those queries no longer run.
	let settled = $state(false);
	let settledFor = $state(-1);
	$effect(() => {
		const pid = portfolioId();
		if (pid !== settledFor) {
			settledFor = pid;
			settled = isChecklistSettled(pid);
		}
	});

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

	// --- core spine completeness (drives the optional-query gate, computed from the cheap core data) ---
	const coreLoaded = () =>
		portfolioQuery.isSuccess &&
		ownersQuery.isSuccess &&
		propertiesQuery.isSuccess &&
		tenantsQuery.isSuccess &&
		leasesQuery.isSuccess;

	const coreComplete = () => {
		const props = propertiesQuery.data ?? [];
		return (
			!!portfolioQuery.data?.name?.trim() &&
			(ownersQuery.data?.length ?? 0) > 0 &&
			props.length > 0 &&
			props.reduce((sum, p) => sum + (p.unitCount ?? 0), 0) > 0 &&
			(tenantsQuery.data?.length ?? 0) > 0 &&
			(leasesQuery.data?.length ?? 0) > 0
		);
	};

	// Fetch the optional-only queries while the checklist is plausibly still relevant: we haven't recorded
	// that everything is done yet (the common case — keeps surfacing optional tasks even after the core
	// spine is complete), OR the spine is incomplete (belt-and-suspenders: forces a re-fetch if the
	// persisted "settled" flag is ever stale, e.g. data was wiped/started-over). A fully set-up, settled
	// landlord's dashboard skips all three.
	const optionalEnabled = () => portfolioId() > 0 && (!settled || !coreLoaded() || !coreComplete());

	const notificationEmailQuery = createQuery(() => ({
		queryKey: ['notification-email', portfolioId()],
		enabled: optionalEnabled(),
		queryFn: () => notifications.getNotificationEmail(),
	}));
	const notificationSettingsQuery = createQuery(() => ({
		queryKey: ['notification-settings'],
		enabled: optionalEnabled(),
		queryFn: () => notifications.getSettings(),
	}));
	const sandboxQuery = createQuery(() => ({
		queryKey: ['sandbox-state', portfolioId()],
		enabled: optionalEnabled(),
		queryFn: () => portfolios.sandboxState(),
		staleTime: 60_000,
	}));

	// When the optional queries are gated off (settled, or spine already complete) we treat the optional
	// tasks as satisfied so the checklist reads "done" without those requests — the only reason to fetch
	// them is to surface remaining optional work while onboarding.
	const optionalGatedOff = () => !optionalEnabled();

	function signals(): GettingStartedSignals {
		const props = propertiesQuery.data ?? [];
		const settings = notificationSettingsQuery.data;
		const gatedOff = optionalGatedOff();
		return {
			portfolioNamed: !!portfolioQuery.data?.name?.trim(),
			ownerCount: ownersQuery.data?.length ?? 0,
			propertyCount: props.length,
			unitCount: props.reduce((sum, p) => sum + (p.unitCount ?? 0), 0),
			tenantCount: tenantsQuery.data?.length ?? 0,
			leaseCount: leasesQuery.data?.length ?? 0,
			hasNotificationEmail: gatedOff ? true : !!notificationEmailQuery.data?.email,
			hasTexting: gatedOff
				? true
				: settings?.smsCredentialASet === true || settings?.smsCredentialBSet === true,
			// Only the two toggles that default OFF (rent charges, late fees) count as "the user turned
			// automations on". enableLeaseExpiryReminders defaults TRUE on the server
			// (NotificationSettings.cs), so counting it here auto-checked this task for brand-new accounts
			// that never opened Settings — the checklist claiming a deliberate setup that never happened.
			hasAutomations: gatedOff
				? true
				: settings?.enableRentCharges === true || settings?.enableLateFees === true,
		};
	}

	function ready(): boolean {
		if (!coreLoaded()) return false;
		// When the optional queries are gated off we don't wait on them.
		if (optionalGatedOff()) return true;
		return (
			notificationEmailQuery.isSuccess &&
			notificationSettingsQuery.isSuccess &&
			sandboxQuery.isSuccess
		);
	}

	function isSandbox(): boolean {
		// A settled (fully set up, gone-live) account is never sandbox; while onboarding, read the query.
		if (optionalGatedOff()) return false;
		return sandboxQuery.data?.isSandbox === true;
	}

	// Persist "settled" once everything (core + the optional, data-derived signals) is done, so future
	// dashboard paints can skip the optional-only queries. Guarded by `ready()` so we never settle on a
	// half-loaded snapshot, and only while the queries actually ran (not already gated off).
	$effect(() => {
		const pid = portfolioId();
		if (pid <= 0 || settled) return;
		if (!ready() || optionalGatedOff()) return;
		const s = signals();
		// Mirrors the checklist's "all tasks done" rule. Texting (SignalWire) was removed from the
		// checklist (A13), so it no longer gates the settled flag — only the tasks the checklist shows.
		const allDone =
			s.portfolioNamed &&
			s.ownerCount > 0 &&
			s.propertyCount > 0 &&
			s.unitCount > 0 &&
			s.tenantCount > 0 &&
			s.leaseCount > 0 &&
			s.hasNotificationEmail &&
			s.hasAutomations;
		if (allDone) {
			markChecklistSettled(pid);
			settled = true; // stop the optional-only queries for the rest of this session too
		}
	});

	return { signals, ready, isSandbox };
}
