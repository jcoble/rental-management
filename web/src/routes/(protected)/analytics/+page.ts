import { redirect } from '@sveltejs/kit';

// Insights merged into the Dashboard (IA Wave 1, §4.4) — two overview pages with
// drifting numbers is worse than one. Keep old /analytics links/bookmarks working.
export function load() {
	redirect(307, '/');
}
