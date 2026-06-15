/**
 * Theatrical "Setting up your sandbox…" screen.
 *
 * Reached only from the choice gate after the user picks Sandbox. It records the choice (which seeds
 * the demo data server-side) and plays a deliberate ~20s staged progress animation, then lands on the
 * dashboard. Full-screen and chrome-free (lives OUTSIDE the protected AppShell).
 *
 * Guards: unauthenticated → /login. Portal-only users → /portal. If the account has already finished
 * the gate (choice no longer pending), skip straight to the dashboard so this screen can't be replayed.
 */

import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { serverGet } from '$lib/api/server-fetch';
import type { SandboxState } from '$lib/types';

export const load: PageServerLoad = async ({ locals }) => {
	if (!locals.user || !locals.accessToken) {
		throw redirect(303, '/login?redirectTo=/choose-setup');
	}

	const roles = locals.user.roles ?? [];
	const portalOnly = roles.includes('Tenant') && !roles.some((r) => ['Admin', 'Manager', 'Agent'].includes(r));
	if (portalOnly) {
		throw redirect(303, '/portal');
	}

	const { data: sandbox } = await serverGet<SandboxState>(
		'/portfolio/sandbox-state',
		locals.accessToken
	);

	// Already live / already a finished sandbox → nothing to set up here.
	if (sandbox && sandbox.onboardingChoicePending === false) {
		throw redirect(303, '/');
	}

	return {};
};
