import { redirect } from '@sveltejs/kit';

// The activity feed was unified into the audit trail. Keep old links/bookmarks working.
export function load() {
	redirect(307, '/audit');
}
