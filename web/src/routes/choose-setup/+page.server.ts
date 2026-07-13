/**
 * First-login Sandbox-vs-Live choice gate.
 *
 * Shown once, right after a brand-new account's first login, to ask whether to explore with sample
 * data (Sandbox) or set up a real, empty portfolio (Live). Full-screen and chrome-free (it lives
 * OUTSIDE the protected AppShell on purpose). Guards:
 *   - unauthenticated  → /login
 *   - portal-only user → /portal (they never see this)
 *   - choice already made → /  (don't re-show the gate on repeat logins)
 */

import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { serverGet } from '$lib/api/server-fetch';
import type { SandboxState } from '$lib/types';

export const load: PageServerLoad = async ({ locals }) => {
	if (!locals.user || !locals.accessToken) {
		throw redirect(303, '/login?redirectTo=/choose-setup');
	}

	if (locals.access?.selectedContext.activeExperience === 'Tenant') {
		throw redirect(303, '/portal');
	}

	const { data: sandbox } = await serverGet<SandboxState>(
		'/portfolio/sandbox-state',
		locals.accessToken
	);

	// Already decided → the gate is done; send them to the app.
	if (sandbox && sandbox.onboardingChoicePending === false) {
		throw redirect(303, '/');
	}

	return {
		displayName: locals.user.displayName ?? ''
	};
};
