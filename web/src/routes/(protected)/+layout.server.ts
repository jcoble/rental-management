/**
 * Protected (staff app) layout guard. Requires an authenticated user; sends
 * unauthenticated visitors to /login with a redirectTo back to the page they
 * tried to reach.
 *
 * First-login gate: a brand-new account hasn't yet chosen Sandbox vs Live. Until it does, every
 * protected route bounces to /choose-setup (the choice screen) — so the user can't slip past the
 * decision by deep-linking or refreshing. Once a choice is recorded the gate is inert.
 */

import { redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';
import { serverGet } from '$lib/api/server-fetch';
import type { SandboxState } from '$lib/types';
import { canAccessRoute, CAPABILITY, safeLandingForAccess } from '$lib/auth/experience-policy';

// Routes that are part of the first-login flow itself — never gate these (would loop).
const ONBOARDING_GATE_PATHS = ['/choose-setup', '/setting-up'];

export const load: LayoutServerLoad = async ({ locals, url }) => {
	if (!locals.user) {
		// An unauthenticated visitor to the bare root sees the public marketing
		// landing page rather than being bounced straight to the login form.
		if (url.pathname === '/') {
			throw redirect(303, '/welcome');
		}
		const redirectTo = url.pathname + url.search;
		throw redirect(303, `/login?redirectTo=${encodeURIComponent(redirectTo)}`);
	}

	if (!locals.access) {
		throw redirect(303, '/logout');
	}
	const activeExperience = locals.access.selectedContext.activeExperience;
	const effectiveCapabilities = new Set(
		locals.access.navigation.find((entry) => entry.experience === activeExperience)?.capabilityKeys ?? []
	);
	if (!canAccessRoute(url.pathname, activeExperience, effectiveCapabilities)) {
		throw redirect(303, safeLandingForAccess(locals.access) ?? '/logout');
	}

	// First-login Sandbox-vs-Live gate (staff only; portal users already redirected above). Skip the
	// gate's own pages to avoid a redirect loop. The lookup is one cheap portfolio row and is inert
	// (returns onboardingChoicePending=false) once the user has chosen.
	if (
		locals.accessToken &&
		activeExperience === 'Management' &&
		effectiveCapabilities.has(CAPABILITY.rentalsManage) &&
		!ONBOARDING_GATE_PATHS.includes(url.pathname)
	) {
		const { data: sandbox } = await serverGet<SandboxState>(
			'/portfolio/sandbox-state',
			locals.accessToken
		);
		if (sandbox?.onboardingChoicePending) {
			throw redirect(303, '/choose-setup');
		}
	}

	return {
		user: locals.user,
		accessToken: locals.accessToken,
		accessTokenExpiration: locals.accessTokenExpiration ?? null,
		access: locals.access
	};
};
