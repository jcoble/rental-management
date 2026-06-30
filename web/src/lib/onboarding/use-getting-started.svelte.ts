/**
 * Shared data hook for the getting-started checklist. It reads one server-shaped summary endpoint
 * whose counts/booleans are computed DB-side, so the dashboard never downloads properties, tenants,
 * leases, or notification settings just to count/sum them locally.
 */
import { createQuery } from '@tanstack/svelte-query';
import { portfolios } from '$lib/api/endpoints/portfolios';
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

	const signalsQuery = createQuery(() => ({
		queryKey: ['getting-started', portfolioId()],
		enabled: portfolioId() > 0,
		queryFn: () => portfolios.gettingStarted(),
		staleTime: 60_000,
	}));

	function signals(): GettingStartedSignals {
		const data = signalsQuery.data;
		return {
			portfolioNamed: data?.portfolioNamed ?? false,
			ownerCount: data?.ownerCount ?? 0,
			propertyCount: data?.propertyCount ?? 0,
			unitCount: data?.unitCount ?? 0,
			tenantCount: data?.tenantCount ?? 0,
			leaseCount: data?.leaseCount ?? 0,
			hasNotificationEmail: data?.hasNotificationEmail ?? false,
			hasTexting: data?.hasTexting ?? false,
			hasAutomations: data?.hasAutomations ?? false,
		};
	}

	function ready(): boolean {
		return signalsQuery.isSuccess;
	}

	function isSandbox(): boolean {
		return signalsQuery.data?.isSandbox === true;
	}

	// Persist "settled" once every displayed task is done. We still use one cheap summary request after
	// that because it prevents stale local state from hiding real gaps if data is later changed.
	$effect(() => {
		const pid = portfolioId();
		if (pid <= 0 || settled) return;
		if (!ready()) return;
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
			settled = true;
		}
	});

	return { signals, ready, isSandbox };
}
